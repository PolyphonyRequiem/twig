using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed new [--type &lt;type&gt;] [--editor] "title"</c>: creates a seed work item
/// locally under the active parent without any ADO interaction.
/// Also backs the bare <c>twig seed "title"</c> shortcut for backward compatibility.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// success/info output is built as a <see cref="RenderTree.RenderTree"/> per output format.
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error formatting.
/// </remarks>
public sealed class SeedNewCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IProcessConfigurationProvider processConfigProvider,
    IFieldDefinitionStore fieldDefStore,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    SeedFactory seedFactory,
    ISeedIdCounter seedIdCounter,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Create a new local seed work item (no ADO push).</summary>
    public async Task<int> ExecuteAsync(
        string? title,
        string? type = null,
        bool editor = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // Title is required unless --editor is used (editor can supply it)
        if (!editor && string.IsNullOrWhiteSpace(title))
        {
            Console.Error.WriteLine(fmt.FormatError("Usage: twig seed new --title \"title\" [--type <type>]"));
            return 2;
        }

        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
        if (!resolved.TryGetWorkItem(out var parent, out var errorId, out var errorReason) && errorId is not null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{errorId} is unreachable: {errorReason}"));
            return 1;
        }

        var processConfig = processConfigProvider.GetConfiguration();

        WorkItemType? typeOverride = null;
        if (type is not null)
        {
            var typeResult = WorkItemType.Parse(type);
            if (!typeResult.IsSuccess)
            {
                Console.Error.WriteLine(fmt.FormatError(typeResult.Error));
                return 1;
            }
            typeOverride = typeResult.Value;
        }

        // Initialize seed counter from DB to avoid ID collisions (D7)
        var minSeedId = await workItemRepo.GetMinSeedIdAsync(ct);
        if (minSeedId.HasValue)
            seedIdCounter.Initialize(minSeedId.Value);

        // Use placeholder title for editor-only flow when no title provided
        var seedTitle = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title;

        var seedResult = seedFactory.Create(seedTitle, parent, processConfig, typeOverride,
            config.User.DisplayName);
        if (!seedResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(seedResult.Error));
            return 1;
        }

        var seed = seedResult.Value;

        if (editor)
        {
            // Editor workflow: generate buffer, launch editor, parse result, apply fields
            var fieldDefs = await fieldDefStore.GetAllAsync(ct);
            var buffer = SeedEditorFormat.Generate(seed, fieldDefs);
            var edited = await editorLauncher.LaunchAsync(buffer, ct);

            if (edited is null)
            {
                RenderEditorCancelled(outputFormat);
                return 0;
            }

            var parsedFields = SeedEditorFormat.Parse(edited, fieldDefs);

            var newTitle = parsedFields.TryGetValue("System.Title", out var parsedTitle) && !string.IsNullOrWhiteSpace(parsedTitle)
                ? parsedTitle : seedTitle;
            var updateResult = seed.TryWithSeedFields(newTitle, parsedFields);
            if (!updateResult.IsSuccess)
            {
                Console.Error.WriteLine(fmt.FormatError(updateResult.Error));
                return 1;
            }

            seed = updateResult.Value;
        }

        // Persist locally — no ADO interaction
        await workItemRepo.SaveAsync(seed, ct);

        var hints = hintEngine.GetHints("seed",
            outputFormat: outputFormat,
            createdId: seed.Id);

        RenderCreated(seed, hints, outputFormat);
        return 0;
    }

    private void RenderCreated(WorkItem seed, IReadOnlyList<string> hints, string outputFormat)
    {
        var message = $"Created local seed: #{seed.Id} {seed.Title} ({seed.Type})";
        var tree = BuildCreatedTree(seed, message, hints, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private void RenderEditorCancelled(string outputFormat)
    {
        const string message = "Seed creation cancelled (editor aborted).";
        var tree = BuildEditorCancelledTree(message, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private static RenderTree.RenderTree BuildCreatedTree(
        WorkItem seed, string message, IReadOnlyList<string> hints, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        var isMachine = lower is "json" or "json-full" or "json-compact" or "minimal" or "ids";
        var nodes = new List<RenderNode>(capacity: 1 + (isMachine ? 0 : hints.Count));

        nodes.Add(lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" => BuildCreatedRecord(seed, message),
            _ => new RenderNode.Text(message, Severity.Success),
        });

        if (!isMachine)
        {
            foreach (var hint in hints)
            {
                if (!string.IsNullOrWhiteSpace(hint))
                    nodes.Add(new RenderNode.Hint(hint));
            }
        }

        return new RenderTree.RenderTree(nodes);
    }

    private static RenderTree.RenderTree BuildEditorCancelledTree(string message, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" => BuildEditorCancelledRecord(message),
            _ => new RenderNode.Text(message, Severity.Info),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderNode BuildCreatedRecord(WorkItem seed, string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = new RenderCell(seed.Id.ToString(), new RenderValue.Integer(seed.Id)),
            ["title"] = new RenderCell(seed.Title, new RenderValue.String(seed.Title)),
            ["type"] = new RenderCell(seed.Type.Value, new RenderValue.String(seed.Type.Value)),
            ["isSeed"] = new RenderCell("true", new RenderValue.Boolean(true)),
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };
        return new RenderNode.Record("seedCreated", fields);
    }

    private static RenderNode BuildEditorCancelledRecord(string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["cancelled"] = new RenderCell("true", new RenderValue.Boolean(true)),
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };
        return new RenderNode.Record("seedCreationCancelled", fields);
    }
}