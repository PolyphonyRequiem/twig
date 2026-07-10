using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Content;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

public sealed class NewCommand(
    IAdoWorkItemService adoService,
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    IFieldDefinitionStore fieldDefStore,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    SeedFactory seedFactory,
    RendererFactory? rendererFactory = null,
    ContextChangeService? contextChangeService = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();
    public async Task<int> ExecuteAsync(
        string? title,
        string? type = null,
        string? area = null,
        string? iteration = null,
        string? description = null,
        int? parent = null,
        bool set = false,
        bool editor = false,
        string? format = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (!editor && string.IsNullOrWhiteSpace(title))
        {
            Console.Error.WriteLine(fmt.FormatError("Usage: twig new --title \"title\" --type <type>"));
            return 2;
        }

        var formatError = HtmlFieldFormatter.ValidateFormat(format);
        if (formatError is not null)
        {
            Console.Error.WriteLine(fmt.FormatError(formatError));
            return 2;
        }

        if (type is null)
        {
            Console.Error.WriteLine(fmt.FormatError(parent is null
                ? "Type is required. Usage: twig new \"title\" --type <type>, or provide --parent to infer type."
                : "--type is required. Type inference from --parent is not yet supported; use --type <type> explicitly."));
            return 1;
        }

        var typeResult = WorkItemType.Parse(type);
        if (!typeResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(typeResult.Error));
            return 1;
        }

        if (parent is <= 0)
        {
            Console.Error.WriteLine(fmt.FormatError($"--parent must be a positive work-item ID (got {parent.Value})."));
            return 1;
        }

        // Per AB#3242: when --parent is given, fetch the parent once and inherit its
        // System.AreaPath / System.IterationPath as defaults for the new child, ahead of
        // config.Defaults. ADO does not auto-inherit these on create, and the project-root
        // fallback usually trips TF237111 ("no permission to save under specified area path")
        // for any caller without write access to the project root.
        // Skip the fetch entirely when both slots are explicit or both opt-outs are set —
        // no need to pay the round-trip or risk a spurious warning we can't use.
        var needsParentArea = parent.HasValue && area is null && config.Defaults.InheritParentArea;
        var needsParentIteration = parent.HasValue && iteration is null && config.Defaults.InheritParentIteration;

        WorkItem? parentItem = null;
        if (needsParentArea || needsParentIteration)
            parentItem = await TryFetchParentForInheritanceAsync(parent!.Value, ct);

        var areaResult = ResolveAreaPath(
            area,
            needsParentArea ? parentItem?.AreaPath.ToString() : null);
        if (!areaResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(areaResult.Error));
            return 1;
        }

        var iterResult = ResolveIterationPath(
            iteration,
            needsParentIteration ? parentItem?.IterationPath.ToString() : null);
        if (!iterResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(iterResult.Error));
            return 1;
        }

        var seedTitle = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title;

        var seedResult = seedFactory.CreateUnparented(
            seedTitle,
            typeResult.Value,
            areaResult.Value,
            iterResult.Value,
            config.User.DisplayName,
            parent);

        if (!seedResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(seedResult.Error));
            return 1;
        }

        var seed = seedResult.Value;

        if (!string.IsNullOrWhiteSpace(description))
        {
            // When --editor is set, leave the description raw so the editor buffer
            // is operator-authored rather than pre-rendered HTML. Explicit
            // --format markdown still forces conversion before launch.
            string descriptionToStore;
            if (editor && format is null)
            {
                descriptionToStore = description;
            }
            else
            {
                var resolved = await HtmlFieldFormatter.ResolveAsync(
                    "System.Description", description, format, fieldDefStore,
                    onMissingFieldDef: name =>
                        Console.Error.WriteLine($"warning: field type unknown for '{name}'; not converting. Use --format markdown to force conversion."),
                    ct);
                descriptionToStore = resolved.EffectiveValue;
            }

            seed.SetField("System.Description", descriptionToStore);
        }

        if (editor)
        {
            var fieldDefs = await fieldDefStore.GetAllAsync(ct);
            var buffer = SeedEditorFormat.Generate(seed, fieldDefs);
            var edited = await editorLauncher.LaunchAsync(buffer, ct);

            if (edited is null)
            {
                RenderCancelled(outputFormat);
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

        int newId;
        try
        {
            newId = await adoService.CreateAsync(seed.ToCreateRequest(), ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError($"Create failed: {ex.Message}"));
            return 1;
        }

        WorkItem fetched;
        try
        {
            fetched = await adoService.FetchAsync(newId, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"Created #{newId} in ADO but fetch-back failed: {ex.Message}. Run 'twig sync' to recover."));
            return 1;
        }

        await workItemRepo.SaveAsync(fetched, ct);

        if (set)
            await contextStore.SetActiveWorkItemIdAsync(newId, ct);

        var url = $"https://dev.azure.com/{Uri.EscapeDataString(config.Organization)}/{Uri.EscapeDataString(config.Project)}/_workitems/edit/{newId}";
        RenderCreated(fetched, url, outputFormat);

        var hints = hintEngine.GetHints("new",
            outputFormat: outputFormat,
            createdId: newId);
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        var isMachine = lower is "json" or "json-full" or "json-compact" or "minimal" or "ids";
        if (!isMachine)
        {
            foreach (var hint in hints)
            {
                if (!string.IsNullOrWhiteSpace(hint))
                    _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[]
                    {
                        (RenderNode)new RenderNode.Hint(hint),
                    }));
            }
        }

        // Extend working set around the new item (fire-and-forget — never fails the command).
        // Runs after output so user sees success immediately.
        if (set && contextChangeService is not null)
            await contextChangeService.ExtendWorkingSetAsync(newId, ct);

        return 0;
    }

    private void RenderCreated(WorkItem item, string url, string outputFormat)
    {
        var message = $"✓ Created #{item.Id} {item.Title} ({item.Type})";
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text($"#{item.Id}"),
            "json" or "json-full" or "json-compact" or "ids" => BuildCreatedRecord(item, url),
            _ => new RenderNode.Text(message, Severity.Success),
        };
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }

    private static RenderNode BuildCreatedRecord(WorkItem item, string url)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(item.Id),
            ["type"] = RenderCell.String(item.Type.ToString()),
            ["title"] = RenderCell.String(item.Title),
            ["url"] = RenderCell.String(url),
            ["parent"] = item.ParentId.HasValue
                ? RenderCell.Integer(item.ParentId.Value)
                : new RenderCell(string.Empty, new RenderValue.Null()),
        };
        return new RenderNode.Record("workItemCreated", fields);
    }

    private void RenderCancelled(string outputFormat)
    {
        const string message = "Creation cancelled (editor aborted).";
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                new RenderNode.Record("workItemCreationCancelled", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["cancelled"] = RenderCell.Boolean(true),
                    ["message"] = RenderCell.String(message),
                }),
            _ => new RenderNode.Text(message, Severity.Info),
        };
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }

    private string? ResolveRaw(string? flag, string? configDefault, string? inheritedFromParent = null)
    {
        // Priority: explicit flag > parent inheritance > config default > project root.
        // The inheritance slot uses a whitespace check because a parent value object can
        // surface as empty string; flag and configDefault retain their original null-only
        // semantics so an explicit `--area ""` continues to be a parse failure, not a
        // silent fall-through.
        if (flag is not null) return flag;
        if (!string.IsNullOrWhiteSpace(inheritedFromParent)) return inheritedFromParent;
        if (configDefault is not null) return configDefault;
        return string.IsNullOrWhiteSpace(config.Project) ? null : config.Project;
    }

    private Result<AreaPath> ResolveAreaPath(string? flag, string? inheritedFromParent = null)
    {
        var raw = ResolveRaw(flag, config.Defaults.AreaPath, inheritedFromParent);
        return raw is null
            ? Result.Fail<AreaPath>("No area path: use --area, set defaults.areaPath in config, or ensure project is configured.")
            : AreaPath.Parse(raw);
    }

    private Result<IterationPath> ResolveIterationPath(string? flag, string? inheritedFromParent = null)
    {
        var raw = ResolveRaw(flag, config.Defaults.IterationPath, inheritedFromParent);
        return raw is null
            ? Result.Fail<IterationPath>("No iteration path: use --iteration, set defaults.iterationPath in config, or ensure project is configured.")
            : IterationPath.Parse(raw);
    }

    /// <summary>
    /// Fetches the parent work item so its <c>System.AreaPath</c> / <c>System.IterationPath</c>
    /// can be used as defaults for a new child. Tries the local cache first (covers any item
    /// the user has touched recently and is the only source for unpublished seeds); falls
    /// through to ADO on cache miss. On any failure, emits a single structured warning to
    /// stderr and returns <c>null</c> so the caller falls back to the configured default —
    /// a flaky parent fetch must not block a create that would have succeeded under the
    /// pre-AB#3242 code path.
    /// </summary>
    private async Task<WorkItem?> TryFetchParentForInheritanceAsync(int parentId, CancellationToken ct)
    {
        string? cacheError = null;
        try
        {
            var cached = await workItemRepo.GetByIdAsync(parentId, ct);
            if (cached is not null) return cached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cache lookup failure is unusual (SQLite I/O). Defer warning until we know
            // ADO also failed — if ADO succeeds we recovered transparently.
            cacheError = ex.Message;
        }

        try
        {
            return await adoService.FetchAsync(parentId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = cacheError is null
                ? ex.Message
                : $"cache: {cacheError}; ado: {ex.Message}";
            Console.Error.WriteLine(
                $"warning: could not fetch parent {parentId} to inherit area/iteration: {detail}; falling back to configured defaults");
            return null;
        }
    }
}