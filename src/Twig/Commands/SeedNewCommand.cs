using Twig.Domain.Aggregates;
using Twig.Domain.Common;
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
        int? parent = null,
        bool noParent = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // Title is required unless --editor is used (editor can supply it)
        if (!editor && string.IsNullOrWhiteSpace(title))
        {
            Console.Error.WriteLine(fmt.FormatError("Usage: twig seed new --title \"title\" [--type <type>] [--parent <id> | --no-parent]"));
            return 2;
        }

        if (noParent && parent.HasValue)
        {
            Console.Error.WriteLine(fmt.FormatError("--no-parent and --parent are mutually exclusive."));
            return 2;
        }

        // With no parent there is nothing to infer the child type from, so --no-parent
        // requires --type. SeedFactory enforces this; caught here for a clearer message.
        if (noParent && type is null)
        {
            Console.Error.WriteLine(fmt.FormatError(
                "--no-parent requires --type, since there is no parent to infer the work item type from."));
            return 2;
        }

        // An explicit --parent wins over the active item; otherwise the active item is
        // inferred, and the inference is announced rather than applied silently (twig#254).
        WorkItem? parentContext;
        var parentWasInferred = false;

        if (noParent)
        {
            parentContext = null;
        }
        else if (parent.HasValue)
        {
            parentContext = await workItemRepo.GetByIdAsync(parent.Value, ct);
            if (parentContext is null)
            {
                Console.Error.WriteLine(fmt.FormatError(
                    $"Parent work item #{parent.Value} not found in the local cache. Run 'twig show {parent.Value}' first."));
                return 1;
            }
        }
        else
        {
            var resolved = await activeItemResolver.GetActiveItemAsync(ct);
            if (!resolved.TryGetWorkItem(out var activeParent, out var errorId, out var errorReason) && errorId is not null)
            {
                Console.Error.WriteLine(fmt.FormatError($"Work item #{errorId} is unreachable: {errorReason}"));
                return 1;
            }

            parentContext = activeParent;
            parentWasInferred = activeParent is not null;
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

        // With --no-parent there is no context to inherit area/iteration from, so fall back
        // to configured defaults — the same resolution the MCP twig_seed_new path uses for
        // its unparented case, keeping the two surfaces at parity.
        var seedResult = noParent
            ? seedFactory.CreateUnparented(
                seedTitle,
                typeOverride!.Value,
                ResolveDefaultPath(config.Defaults?.AreaPath, config.Project, AreaPath.Parse),
                ResolveDefaultPath(config.Defaults?.IterationPath, config.Project, IterationPath.Parse),
                config.User.DisplayName)
            : seedFactory.Create(seedTitle, parentContext, processConfig, typeOverride,
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

        RenderCreated(seed, parentContext, parentWasInferred, hints, outputFormat);
        return 0;
    }

    private void RenderCreated(
        WorkItem seed,
        WorkItem? parentContext,
        bool parentWasInferred,
        IReadOnlyList<string> hints,
        string outputFormat)
    {
        var message = $"Created local seed: #{seed.Id} {seed.Title} ({seed.Type})";
        var tree = BuildCreatedTree(seed, parentContext, parentWasInferred, message, hints, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private void RenderEditorCancelled(string outputFormat)
    {
        const string message = "Seed creation cancelled (editor aborted).";
        var tree = BuildEditorCancelledTree(message, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private static RenderTree.RenderTree BuildCreatedTree(
        WorkItem seed,
        WorkItem? parentContext,
        bool parentWasInferred,
        string message,
        IReadOnlyList<string> hints,
        string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        var isMachine = lower is "json" or "json-full" or "json-compact" or "minimal" or "ids";
        var nodes = new List<RenderNode>(capacity: 2 + (isMachine ? 0 : hints.Count));

        nodes.Add(lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" => BuildCreatedRecord(seed, parentContext, parentWasInferred, message),
            _ => new RenderNode.Text(message, Severity.Success),
        });

        // Never assign a parent silently — an unnoticed inherited parent is the whole of
        // twig#254. Human output states the parent and where it came from.
        if (!isMachine && parentContext is not null)
        {
            var origin = parentWasInferred ? "from active item" : "from --parent";
            nodes.Add(new RenderNode.Text(
                $"  Parent: #{parentContext.Id} {parentContext.Title} ({origin})",
                parentWasInferred ? Severity.Warning : Severity.Info));
        }

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

    private static RenderNode BuildCreatedRecord(
        WorkItem seed,
        WorkItem? parentContext,
        bool parentWasInferred,
        string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = new RenderCell(seed.Id.ToString(), new RenderValue.Integer(seed.Id)),
            ["title"] = new RenderCell(seed.Title, new RenderValue.String(seed.Title)),
            ["type"] = new RenderCell(seed.Type.Value, new RenderValue.String(seed.Type.Value)),
            ["isSeed"] = new RenderCell("true", new RenderValue.Boolean(true)),
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };

        if (parentContext is not null)
        {
            fields["parentId"] = new RenderCell(
                parentContext.Id.ToString(), new RenderValue.Integer(parentContext.Id));
            fields["parentTitle"] = new RenderCell(
                parentContext.Title, new RenderValue.String(parentContext.Title));
            fields["parentInferred"] = new RenderCell(
                parentWasInferred ? "true" : "false", new RenderValue.Boolean(parentWasInferred));
        }

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

    /// <summary>
    /// Resolves a configured default area/iteration path, falling back to the project name
    /// and finally to <c>default</c>. Mirrors the MCP <c>SeedTools.ResolveDefaultPath</c>
    /// used for unparented seeds.
    /// </summary>
    private static T ResolveDefaultPath<T>(
        string? configPath,
        string? projectName,
        Func<string?, Result<T>> parse)
        where T : struct
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var r = parse(configPath);
            if (r.IsSuccess) return r.Value;
        }
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var r = parse(projectName);
            if (r.IsSuccess) return r.Value;
        }
        return default;
    }
}