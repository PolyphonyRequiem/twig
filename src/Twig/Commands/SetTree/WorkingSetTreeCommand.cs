using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Rendering;

namespace Twig.Commands.SetTree;

/// <summary>
/// <c>twig tree-set</c> — renders an arbitrary working set of work items as a forest of
/// annotated trees (twig#277).
/// </summary>
/// <remarks>
/// <para>
/// This is a <strong>pure render</strong>: it takes a set of ids plus an optional
/// annotation map and produces a view. It never prompts, never selects, and never
/// mutates a work item. The review loop and the approval boundary belong to the
/// calling tool.
/// </para>
/// <para>
/// The output is a <em>consent surface</em> — someone approves a bulk write based on
/// what it shows — so the failure policy is: fail loudly rather than render something
/// subtly incomplete. Unknown annotation ids, unknown styles, and unknown icon ids are
/// all errors. The one exception is an id missing from the local cache, which renders
/// as an unmistakable placeholder so the rest of the tree remains usable.
/// </para>
/// </remarks>
internal sealed class WorkingSetTreeCommand(
    CommandContext ctx,
    IWorkItemRepository workItemRepo,
    RendererFactory rendererFactory,
    TwigConfiguration config)
{
    internal DurableFieldOverlay? FieldOverlay { get; init; }

    internal async Task<int> ExecuteAsync(
        string? items,
        string? annotate,
        string outputFormat,
        int depth,
        bool rootsOnly,
        string? icons,
        CancellationToken ct)
        => await this.ExecuteAsync(items, annotate, outputFormat, depth, rootsOnly, icons, readFile: null, readStdin: null, ct);

    /// <summary>Testing overload with file/stdin seams.</summary>
    internal async Task<int> ExecuteAsync(
        string? items,
        string? annotate,
        string outputFormat,
        int depth,
        bool rootsOnly,
        string? icons,
        Func<string, string>? readFile,
        Func<string>? readStdin,
        CancellationToken ct)
    {
        var (fmt, _) = ctx.Resolve(outputFormat, noLive: true);

        if (string.IsNullOrWhiteSpace(items))
        {
            Console.Error.WriteLine(fmt.FormatError("--items is required. Pass a comma-separated id list or @file."));
            return 1;
        }

        if (depth < 0)
        {
            Console.Error.WriteLine(fmt.FormatError("--depth must be zero or greater."));
            return 1;
        }

        var iconMode = config.Display.Icons;
        if (!string.IsNullOrWhiteSpace(icons))
        {
            var requested = icons.Trim().ToLowerInvariant();
            if (requested is not ("auto" or "unicode" or "nerd"))
            {
                Console.Error.WriteLine(fmt.FormatError(
                    $"Unknown icon mode '{icons}'. Expected: auto, unicode, nerd."));
                return 1;
            }
            iconMode = requested;
        }

        var idResult = WorkingSetIdParser.Parse(items, readFile, readStdin);
        if (!idResult.Ok)
        {
            Console.Error.WriteLine(fmt.FormatError(idResult.Error!));
            return 1;
        }

        IReadOnlyDictionary<int, TreeAnnotation> annotations = new Dictionary<int, TreeAnnotation>();
        if (!string.IsNullOrWhiteSpace(annotate))
        {
            var annotationResult = AnnotationMapParser.Parse(annotate, readFile, readStdin);
            if (!annotationResult.Ok)
            {
                Console.Error.WriteLine(fmt.FormatError(annotationResult.Error!));
                return 1;
            }

            annotations = annotationResult.Annotations;

            // The ticket's central rule: an annotation that fails to appear is worse
            // than a crash, because the reviewer consents to a tree believing it is
            // complete. Unknown ids therefore abort rather than being dropped.
            var requestedIds = new HashSet<int>(idResult.Ids);
            var unknown = annotations.Keys.Where(id => !requestedIds.Contains(id)).OrderBy(id => id).ToList();
            if (unknown.Count > 0)
            {
                Console.Error.WriteLine(fmt.FormatError(
                    $"Annotation ids not in the working set: {string.Join(", ", unknown.Select(id => $"#{id}"))}. "
                    + "Every annotated id must be present in --items."));
                return 1;
            }
        }

        var builder = new WorkingSetTreeBuilder(workItemRepo);
        var forest = await builder.BuildAsync(idResult.Ids, annotations, rootsOnly, depth, ct);
        forest = await ApplyFieldOverlayAsync(forest, ct);

        // SpectreTheme reads only Icons and TypeColors off DisplayConfig, so a
        // per-invocation icon override is a shallow copy rather than mutating the
        // shared configuration object (which other commands in-process would see).
        var displayForRender = new DisplayConfig
        {
            Icons = iconMode,
            TypeColors = config.Display.TypeColors,
        };
        displayForRender.Icons = displayForRender.ResolveIconMode();

        var theme = new SpectreTheme(displayForRender, config.TypeAppearances);
        var projector = new WorkingSetTreeProjector(theme, displayForRender.Icons);
        var renderTree = projector.Project(forest);

        rendererFactory.GetRenderer(outputFormat).Render(renderTree);
        Console.WriteLine();

        // A cache miss is not a failure of the render — the rest of the tree is still
        // valid consent surface — but it goes to stderr as well so a caller piping
        // stdout into a reviewer's terminal cannot miss it. stderr, not stdout, so
        // machine consumers parsing the JSON are unaffected.
        if (forest.MissingIds.Count > 0)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"Not in local cache: {string.Join(", ", forest.MissingIds.Select(id => $"#{id}"))}. "
                + "Rendered as placeholders. Run 'twig sync' or 'twig show <id>' to populate."));
        }

        return 0;
    }

    private async Task<WorkingSetForest> ApplyFieldOverlayAsync(WorkingSetForest forest, CancellationToken ct)
    {
        if (FieldOverlay is null)
            return forest;

        // Build topology from the mirror first; only the resulting display items are composed.
        var items = new List<WorkItem>();
        var seen = new HashSet<WorkItem>(ReferenceEqualityComparer.Instance);
        void Collect(WorkingSetNode node)
        {
            if (node.Item is { } item && seen.Add(item))
                items.Add(item);
            foreach (var child in node.Children)
                Collect(child);
        }
        foreach (var root in forest.Roots)
            Collect(root);

        var effective = await FieldOverlay.ApplyAsync(items, ct);
        var replacements = new Dictionary<WorkItem, WorkItem>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < items.Count; i++)
        {
            if (!ReferenceEquals(items[i], effective[i]))
                replacements.Add(items[i], effective[i]);
        }
        if (replacements.Count == 0)
            return forest;

        WorkingSetNode Replace(WorkingSetNode node)
        {
            var item = node.Item is { } raw && replacements.TryGetValue(raw, out var composed)
                ? composed : node.Item;
            var children = node.Children.Select(Replace).ToList();
            return ReferenceEquals(item, node.Item)
                && !children.Where((child, index) => !ReferenceEquals(child, node.Children[index])).Any()
                ? node
                : node with { Item = item, Children = children };
        }

        return forest with { Roots = forest.Roots.Select(Replace).ToList() };
    }
}
