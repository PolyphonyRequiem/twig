using System.Diagnostics;
using Spectre.Console.Rendering;
using Twig.Domain.Common;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig show [id]</c>: read-only work item display.
/// When called with an ID, performs a cache-first lookup.
/// When called without an ID, resolves the active work item from context.
/// If no active item is set, emits a branch detection hint and exits 1.
/// Unlike <see cref="SetCommand"/>, this command does not change active context or record
/// navigation history. By default, renders cached data immediately then syncs the item
/// and revises the display. Use <c>--no-refresh</c> to skip the sync pass.
/// </summary>
public sealed class ShowCommand(
    CommandContext ctx,
    IWorkItemRepository workItemRepo,
    IWorkItemLinkRepository linkRepo,
    SyncCoordinatorFactory syncCoordinatorFactory,
    StatusFieldConfigReader statusFieldReader,
    IFieldDefinitionStore? fieldDefinitionStore = null,
    IProcessConfigurationProvider? processConfigProvider = null,
    IContextStore? contextStore = null,
    ActiveItemResolver? activeItemResolver = null,
    IPendingChangeStore? pendingChangeStore = null,
    WorkingSetService? workingSetService = null,
    TwigPaths? twigPaths = null,
    IAdoGitService? adoGitService = null,
    TreeRenderingService? treeRenderingService = null,
    RendererFactory? rendererFactory = null)
{
    private readonly IContextStore? _contextStore = contextStore;
    private readonly ActiveItemResolver? _activeItemResolver = activeItemResolver;
    private readonly IPendingChangeStore? _pendingChangeStore = pendingChangeStore;
    private readonly WorkingSetService? _workingSetService = workingSetService;
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    public async Task<int> ExecuteAsync(int? id = null, string outputFormat = OutputFormatterFactory.DefaultFormat, bool tree = false, bool noRefresh = false, CancellationToken ct = default, int? depth = null, bool noLive = false)
    {
        using var scope = new CommandActivityScope("show", outputFormat);
        int exitCode;

        try
        {
            if (tree)
            {
                if (treeRenderingService is null)
                {
                    ctx.StderrWriter.WriteLine("error: Tree rendering is not available.");
                    exitCode = 1;
                }
                else
                {
                    exitCode = await treeRenderingService.RenderTreeAsync(id, outputFormat, depth, noLive, noRefresh, ct);
                }

                scope.Complete(exitCode);
                TelemetryHelper.TrackCommand(ctx.TelemetryClient, "show", outputFormat, exitCode, scope.StartTimestamp,
                    new Dictionary<string, string> { ["tree"] = "true" });
                return exitCode;
            }

            exitCode = await ExecuteCoreAsync(id, outputFormat, noRefresh, ct);
            scope.Complete(exitCode);
            TelemetryHelper.TrackCommand(ctx.TelemetryClient, "show", outputFormat, exitCode, scope.StartTimestamp);
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// Batch lookup: accepts comma-separated IDs, returns all found items.
    /// Cache-only — no ADO fetch. Missing IDs are silently skipped.
    /// </summary>
    public async Task<int> ExecuteBatchAsync(string batch, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        using var scope = new CommandActivityScope("show-batch", outputFormat);
        try
        {
            var exitCode = await ExecuteBatchCoreAsync(batch, outputFormat, ct);
            scope.Complete(exitCode);
            TelemetryHelper.TrackCommand(ctx.TelemetryClient, "show-batch", outputFormat, exitCode, scope.StartTimestamp);
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
    }

    private async Task<int> ExecuteCoreAsync(int? id, string outputFormat, bool noRefresh, CancellationToken ct)
    {
        var (fmt, renderer) = ctx.Resolve(outputFormat);

        Domain.Aggregates.WorkItem item;
        int resolvedId;

        if (id.HasValue)
        {
            // ── By-ID path — cache-first lookup ──
            resolvedId = id.Value;
            var cached = await workItemRepo.GetByIdAsync(resolvedId, ct);
            if (cached is null)
            {
                ctx.StderrWriter.WriteLine($"error: Work item #{resolvedId} not found in local cache. Run 'twig set {resolvedId}' to fetch it.");
                return 1;
            }
            item = cached;
        }
        else
        {
            // ── No-args path — resolve from active context ──
            if (_contextStore is null || _activeItemResolver is null)
            {
                ctx.StderrWriter.WriteLine("error: No work item ID specified and context services not available.");
                return 1;
            }

            var result = await _activeItemResolver.GetActiveItemAsync(ct);
            switch (result)
            {
                case Found found:
                    item = found.WorkItem;
                    break;
                case FetchedFromAdo fetched:
                    item = fetched.WorkItem;
                    break;
                case ActiveUnreachable unreachable:
                    ctx.StderrWriter.WriteLine($"error: Active work item #{unreachable.Id} is not reachable: {unreachable.Reason}");
                    return 1;
                case ActiveNoContext:
                default:
                    EmitBranchDetectionHint();
                    return 1;
            }
            resolvedId = item.Id;
        }

        // Enrichment — all cache-only, best-effort
        var children = await workItemRepo.GetChildrenAsync(item.Id, ct);
        Domain.Aggregates.WorkItem? parent = item.ParentId.HasValue
            ? await workItemRepo.GetByIdAsync(item.ParentId.Value, ct)
            : null;

        IReadOnlyList<WorkItemLink> links = [];
        try { links = await linkRepo.GetLinksAsync(item.Id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        var fieldDefs = fieldDefinitionStore is not null
            ? await fieldDefinitionStore.GetAllAsync(ct)
            : null;

        var statusFieldEntries = await statusFieldReader.ReadAsync(ct);

        var childProgress = processConfigProvider.ComputeChildProgress(children);

        var gitContext = await BuildGitContextAsync(ct);

        Func<Task<IReadOnlyList<PendingChangeRecord>>> getPendingChanges = _pendingChangeStore is not null
            ? () => _pendingChangeStore.GetChangesAsync(item.Id)
            : () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>([]);

        async Task<SyncResult> RefreshItemAndLinksAsync(CancellationToken refreshCt)
        {
            var linkSync = await syncCoordinatorFactory.ReadOnly.SyncRootLinksAsync(resolvedId, refreshCt);
            return linkSync is SyncFailed
                ? await syncCoordinatorFactory.ReadOnly.SyncItemSetAsync([resolvedId], refreshCt)
                : linkSync;
        }

        // Non-TTY machine output: sync synchronously before emitting so consumers get fresh data.
        // The TTY path handles sync via RenderWithSyncAsync (two-pass: cached → sync → revised).
        if (renderer is null && !noRefresh)
        {
            try
            {
                await RefreshItemAndLinksAsync(ct);

                // Reload data from cache after sync
                var freshItem = await workItemRepo.GetByIdAsync(resolvedId, ct);
                if (freshItem is not null)
                {
                    item = freshItem;
                    children = await workItemRepo.GetChildrenAsync(item.Id, ct);
                    parent = item.ParentId.HasValue
                        ? await workItemRepo.GetByIdAsync(item.ParentId.Value, ct)
                        : null;

                    try { links = await linkRepo.GetLinksAsync(item.Id, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

                    childProgress = processConfigProvider.ComputeChildProgress(children);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sync failure is non-fatal — emit cache-only data
            }
        }

        if (renderer is not null)
        {
            Task RenderStaticAsync() => renderer.RenderStatusAsync(
                getItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(item),
                getPendingChanges: getPendingChanges,
                ct: CancellationToken.None,
                fieldDefinitions: fieldDefs,
                statusFieldEntries: statusFieldEntries,
                childProgress: childProgress,
                links: links,
                parent: parent,
                children: children,
                cacheStaleMinutes: ctx.Config.Display.CacheStaleMinutes,
                gitContext: gitContext);

            if (renderer is SpectreRenderer spectreRenderer && !noRefresh)
            {
                Task<IRenderable> BuildView(Domain.Aggregates.WorkItem wi, Domain.Aggregates.WorkItem? pa, IReadOnlyList<Domain.Aggregates.WorkItem> ch, (int Done, int Total)? progress)
                    => spectreRenderer.BuildStatusViewAsync(wi,
                        getPendingChanges: getPendingChanges,
                        fieldDefinitions: fieldDefs,
                        statusFieldEntries: statusFieldEntries,
                        childProgress: progress,
                        links: links,
                        parent: pa,
                        children: ch,
                        cacheStaleMinutes: ctx.Config.Display.CacheStaleMinutes,
                        gitContext: gitContext);

                try
                {
                    await renderer.RenderWithSyncAsync(
                        buildCachedView: () => BuildView(item, parent, children, childProgress),
                        performSync: () => RefreshItemAndLinksAsync(CancellationToken.None),
                        buildRevisedView: async _ =>
                        {
                            var freshItem = await workItemRepo.GetByIdAsync(resolvedId, CancellationToken.None);
                            if (freshItem is null) return null;

                            var freshChildren = await workItemRepo.GetChildrenAsync(freshItem.Id, CancellationToken.None);
                            var freshParent = freshItem.ParentId.HasValue
                                ? await workItemRepo.GetByIdAsync(freshItem.ParentId.Value, CancellationToken.None)
                                : null;

                            IReadOnlyList<WorkItemLink> freshLinks = [];
                            try { freshLinks = await linkRepo.GetLinksAsync(freshItem.Id, CancellationToken.None); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

                            return await spectreRenderer.BuildStatusViewAsync(freshItem,
                                getPendingChanges: getPendingChanges,
                                fieldDefinitions: fieldDefs,
                                statusFieldEntries: statusFieldEntries,
                                childProgress: processConfigProvider.ComputeChildProgress(freshChildren),
                                links: freshLinks,
                                parent: freshParent,
                                children: freshChildren,
                                cacheStaleMinutes: ctx.Config.Display.CacheStaleMinutes,
                                gitContext: gitContext);
                        },
                        CancellationToken.None);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    await RenderStaticAsync();
                }
            }
            else
            {
                await RenderStaticAsync();
            }
        }
        else if (!IsMachineFormat(outputFormat) && fmt is HumanOutputFormatter humanFmt)
        {
            (int FieldCount, int NoteCount)? pendingCounts = null;
            if (_pendingChangeStore is not null)
            {
                var pending = await _pendingChangeStore.GetChangesAsync(item.Id);
                if (pending.Count > 0)
                {
                    var noteCount = 0;
                    var fieldCount = 0;
                    foreach (var change in pending)
                    {
                        if (string.Equals(change.ChangeType, "note", StringComparison.OrdinalIgnoreCase))
                            noteCount++;
                        else
                            fieldCount++;
                    }
                    pendingCounts = (fieldCount, noteCount);
                }
            }
            Console.WriteLine(humanFmt.FormatWorkItem(item, showDirty: false, fieldDefs, statusFieldEntries, childProgress, pendingCounts, links, parent, children, gitContext: gitContext));
        }
        else
        {
            (int FieldCount, int NoteCount)? pendingCounts = null;
            if (_pendingChangeStore is not null)
            {
                var pending = await _pendingChangeStore.GetChangesAsync(item.Id);
                if (pending.Count > 0)
                {
                    var noteCount = 0;
                    var fieldCount = 0;
                    foreach (var change in pending)
                    {
                        if (string.Equals(change.ChangeType, "note", StringComparison.OrdinalIgnoreCase))
                            noteCount++;
                        else
                            fieldCount++;
                    }
                    pendingCounts = (fieldCount, noteCount);
                }
            }

            RenderWorkItemTree(item, links, parent, children, gitContext, pendingCounts, outputFormat);
        }

        return 0;
    }

    /// <summary>
    /// Build a Document or Record projection of a work item and render via
    /// <see cref="RendererFactory"/>. Used for non-human, non-TTY output formats
    /// (json, json-full, json-compact, minimal, ids).
    /// </summary>
    /// <remarks>
    /// Projection differs by format:
    /// <list type="bullet">
    /// <item><c>ids</c> / <c>json-compact</c>: top-level <see cref="RenderNode.Record"/>
    /// with a compact cell set so <c>IdsRenderer</c> can extract the id and
    /// <c>JsonRenderer</c> emits a slim object (id, title, type, state).</item>
    /// <item><c>minimal</c>: top-level <see cref="RenderNode.Record"/> with the
    /// full core cell set so the minimal renderer emits one <c>key=value</c>
    /// line per field.</item>
    /// <item><c>json</c> / <c>json-full</c>: top-level <see cref="RenderNode.Document"/>
    /// with structured fields for relationships (parent, children, links,
    /// pendingChanges, gitContext), mirroring the legacy
    /// <c>JsonOutputFormatter.FormatWorkItem</c> wire shape.</item>
    /// </list>
    /// </remarks>
    private void RenderWorkItemTree(
        Domain.Aggregates.WorkItem item,
        IReadOnlyList<WorkItemLink> links,
        Domain.Aggregates.WorkItem? parent,
        IReadOnlyList<Domain.Aggregates.WorkItem> children,
        GitContext gitContext,
        (int FieldCount, int NoteCount)? pendingChanges,
        string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode root = lower switch
        {
            "ids" or "json-compact" => BuildCompactRecord(item),
            "minimal" => BuildFullRecord(item),
            _ => BuildFullDocument(item, links, parent, children, gitContext, pendingChanges),
        };

        var tree = new Twig.RenderTree.RenderTree([root]);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
        Console.WriteLine();
    }

    private static RenderNode.Record BuildCompactRecord(Domain.Aggregates.WorkItem item)
    {
        return new RenderNode.Record("workItem", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(item.Id),
            ["title"] = RenderCell.String(item.Title ?? string.Empty),
            ["type"] = RenderCell.String(item.Type.ToString()),
            ["state"] = RenderCell.String(item.State ?? string.Empty),
        });
    }

    private static RenderNode.Record BuildFullRecord(Domain.Aggregates.WorkItem item)
    {
        return new RenderNode.Record("workItem", BuildCoreCells(item));
    }

    private static RenderNode.Document BuildFullDocument(
        Domain.Aggregates.WorkItem item,
        IReadOnlyList<WorkItemLink> links,
        Domain.Aggregates.WorkItem? parent,
        IReadOnlyList<Domain.Aggregates.WorkItem> children,
        GitContext gitContext,
        (int FieldCount, int NoteCount)? pendingChanges)
    {
        var coreCells = BuildCoreCells(item);

        var fields = new List<DocumentField>
        {
            new("id", new RenderNode.KeyValue("id", coreCells["id"])),
            new("title", new RenderNode.KeyValue("title", coreCells["title"])),
            new("type", new RenderNode.KeyValue("type", coreCells["type"])),
            new("state", new RenderNode.KeyValue("state", coreCells["state"])),
            new("assignedTo", new RenderNode.KeyValue("assignedTo", coreCells["assignedTo"])),
            new("areaPath", new RenderNode.KeyValue("areaPath", coreCells["areaPath"])),
            new("iterationPath", new RenderNode.KeyValue("iterationPath", coreCells["iterationPath"])),
            new("isDirty", new RenderNode.KeyValue("isDirty", coreCells["isDirty"])),
            new("isSeed", new RenderNode.KeyValue("isSeed", coreCells["isSeed"])),
            new("parentId", new RenderNode.KeyValue("parentId", coreCells["parentId"])),
            new("tags", new RenderNode.KeyValue("tags", coreCells["tags"])),
        };

        var fieldsBlock = BuildFieldsBlock(item);
        if (fieldsBlock is not null)
            fields.Add(new DocumentField("fields", fieldsBlock));

        if (parent is not null)
            fields.Add(new DocumentField("parent", BuildParentRecord(parent)));

        // `children`, `links`, and `relations` are ALWAYS emitted as arrays
        // (possibly empty) so integrators can iterate the key without first
        // checking for its presence. Missing-vs-empty ambiguity silently
        // breaks consumers like polyphony's ExtractPredecessors which
        // expects `relations` to always be readable.
        var childNodes = new List<RenderNode>(children?.Count ?? 0);
        if (children is { Count: > 0 })
        {
            foreach (var child in children)
                childNodes.Add(BuildChildRecord(child));
        }
        fields.Add(new DocumentField("children", new RenderNode.Section(null, childNodes)));

        var linkNodes = new List<RenderNode>(links?.Count ?? 0);
        if (links is { Count: > 0 })
        {
            foreach (var link in links)
                linkNodes.Add(BuildLinkRecord(link));
        }
        fields.Add(new DocumentField("links", new RenderNode.Section(null, linkNodes)));

        // Top-level `relations` array mirrors the ADO REST shape that
        // polyphony's TwigClient reads — each entry carries `id`, `rel`
        // (ADO reference name), `url`, and `attributes.name` (friendly
        // name). Built from the same WorkItemLink set as `links` so
        // consumers can pick whichever shape they prefer.
        var relationNodes = new List<RenderNode>(links?.Count ?? 0);
        if (links is { Count: > 0 })
        {
            foreach (var link in links)
                relationNodes.Add(BuildRelationRecord(link));
        }
        fields.Add(new DocumentField("relations", new RenderNode.Section(null, relationNodes)));

        if (pendingChanges is { } pc && (pc.FieldCount > 0 || pc.NoteCount > 0))
        {
            var pcFields = new List<DocumentField>
            {
                new("fieldEditCount", new RenderNode.KeyValue("fieldEditCount", RenderCell.Integer(pc.FieldCount))),
                new("noteCount", new RenderNode.KeyValue("noteCount", RenderCell.Integer(pc.NoteCount))),
            };
            fields.Add(new DocumentField("pendingChanges", new RenderNode.Document(null, pcFields)));
        }

        if (gitContext is { HasData: true })
        {
            var gcFields = new List<DocumentField>
            {
                new("currentBranch", new RenderNode.KeyValue("currentBranch", RenderCell.String(gitContext.CurrentBranch ?? string.Empty))),
            };

            var prNodes = new List<RenderNode>(gitContext.LinkedPullRequests.Count);
            foreach (var pr in gitContext.LinkedPullRequests)
                prNodes.Add(BuildPullRequestRecord(pr));
            gcFields.Add(new DocumentField("linkedPullRequests", new RenderNode.Section(null, prNodes)));

            fields.Add(new DocumentField("gitContext", new RenderNode.Document(null, gcFields)));
        }

        return new RenderNode.Document(null, fields);
    }

    private static Dictionary<string, RenderCell> BuildCoreCells(Domain.Aggregates.WorkItem item)
    {
        item.Fields.TryGetValue("System.Tags", out var tags);

        return new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(item.Id),
            ["title"] = RenderCell.String(item.Title ?? string.Empty),
            ["type"] = RenderCell.String(item.Type.ToString()),
            ["state"] = RenderCell.String(item.State ?? string.Empty),
            ["assignedTo"] = RenderCell.String(item.AssignedTo ?? string.Empty),
            ["areaPath"] = RenderCell.String(item.AreaPath.ToString()),
            ["iterationPath"] = RenderCell.String(item.IterationPath.ToString()),
            ["isDirty"] = RenderCell.Boolean(false),
            ["isSeed"] = RenderCell.Boolean(item.IsSeed),
            ["parentId"] = item.ParentId.HasValue
                ? RenderCell.Integer(item.ParentId.Value)
                : new RenderCell(string.Empty, new RenderValue.Null()),
            ["tags"] = RenderCell.String(tags ?? string.Empty),
        };
    }

    private static RenderNode? BuildFieldsBlock(Domain.Aggregates.WorkItem item)
    {
        if (item.Fields.Count == 0)
            return null;

        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal);
        foreach (var (refName, value) in item.Fields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            // Tags ARE emitted inside the fields block (as well as at the
            // top level as the convenience `tags` string) so polyphony's
            // fallback path `fields["System.Tags"]` continues to work.
            cells[refName] = RenderCell.String(value);
        }

        if (cells.Count == 0)
            return null;

        return new RenderNode.Record(null, cells);
    }

    private static RenderNode BuildParentRecord(Domain.Aggregates.WorkItem parent)
    {
        return new RenderNode.Record("workItem", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(parent.Id),
            ["title"] = RenderCell.String(parent.Title ?? string.Empty),
            ["type"] = RenderCell.String(parent.Type.ToString()),
        });
    }

    private static RenderNode BuildChildRecord(Domain.Aggregates.WorkItem child)
    {
        child.Fields.TryGetValue("System.Tags", out var tags);
        return new RenderNode.Record("workItem", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(child.Id),
            ["title"] = RenderCell.String(child.Title ?? string.Empty),
            ["type"] = RenderCell.String(child.Type.ToString()),
            ["state"] = RenderCell.String(child.State ?? string.Empty),
            ["tags"] = RenderCell.String(tags ?? string.Empty),
        });
    }

    private static RenderNode BuildLinkRecord(WorkItemLink link)
    {
        return new RenderNode.Record("workItemLink", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["sourceId"] = RenderCell.Integer(link.SourceId),
            ["targetId"] = RenderCell.Integer(link.TargetId),
            ["linkType"] = RenderCell.String(link.LinkType ?? string.Empty),
        });
    }

    /// <summary>
    /// Projects a <see cref="WorkItemLink"/> as an ADO-shaped relation record.
    /// Polyphony's <c>TwigClient.ExtractPredecessors</c> reads each relation's
    /// <c>rel</c> (ADO reference name like
    /// <c>System.LinkTypes.Dependency-Reverse</c>), <c>attributes.name</c>
    /// (friendly name like <c>"Predecessor"</c>), and falls back to <c>id</c>
    /// when <c>url</c> is empty. <c>url</c> is emitted as the empty string
    /// because twig does not persist the source ADO relation URL.
    /// </summary>
    private static RenderNode BuildRelationRecord(WorkItemLink link)
    {
        var friendlyName = link.LinkType ?? string.Empty;
        var adoRel = !string.IsNullOrEmpty(friendlyName) && LinkTypeMapper.TryResolve(friendlyName, out var resolved)
            ? resolved
            : friendlyName;

        var attributes = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["name"] = RenderCell.String(friendlyName),
        };

        return new RenderNode.Record(null, new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(link.TargetId),
            ["rel"] = RenderCell.String(adoRel),
            ["url"] = RenderCell.String(string.Empty),
            ["attributes"] = new RenderCell(string.Empty, new RenderValue.Object(attributes)),
        });
    }

    private static RenderNode BuildPullRequestRecord(PullRequestInfo pr)
    {
        return new RenderNode.Record("pullRequest", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["pullRequestId"] = RenderCell.Integer(pr.PullRequestId),
            ["title"] = RenderCell.String(pr.Title ?? string.Empty),
            ["status"] = RenderCell.String(pr.Status ?? string.Empty),
            ["sourceBranch"] = RenderCell.String(pr.SourceBranch ?? string.Empty),
            ["targetBranch"] = RenderCell.String(pr.TargetBranch ?? string.Empty),
            ["url"] = RenderCell.String(pr.Url ?? string.Empty),
        });
    }

    private async Task<int> ExecuteBatchCoreAsync(string batch, string outputFormat, CancellationToken ct)
    {
        var ids = ParseBatchIds(batch);
        var items = new List<Domain.Aggregates.WorkItem>();

        foreach (var id in ids)
        {
            var item = await workItemRepo.GetByIdAsync(id, ct);
            if (item is not null)
                items.Add(item);
        }

        var fmt = ctx.FormatterFactory.GetFormatter(outputFormat);

        // After AB#3301 the factory always returns HumanOutputFormatter, so
        // dispatch on the requested format string rather than the formatter
        // type. Human/unknown formats render the rich Spectre card; machine
        // formats (json, json-compact, minimal, ids) flow through the
        // RenderTree → IRenderer seam.
        if (!IsMachineFormat(outputFormat) && fmt is HumanOutputFormatter humanFmt)
        {
            foreach (var item in items)
                Console.WriteLine(humanFmt.FormatWorkItem(item, showDirty: false));
        }
        else
        {
            RenderBatchAsTree(items, outputFormat);
        }

        return 0;
    }

    /// <summary>
    /// Renders a batch lookup result through the
    /// <see cref="RenderTree"/> → <see cref="IRenderer"/> seam. Projects each
    /// work item as a row of a single top-level <see cref="RenderNode.Table"/>
    /// so the JSON renderer emits a top-level array and the IDs renderer
    /// emits one ID per row — matching the legacy
    /// <c>JsonOutputFormatter.FormatWorkItemBatch</c> wire shape.
    /// </summary>
    private void RenderBatchAsTree(IReadOnlyList<Domain.Aggregates.WorkItem> items, string outputFormat)
    {
        var rows = new List<RenderRow>(items.Count);
        foreach (var item in items)
            rows.Add(new RenderRow(null, BuildCoreCells(item)));

        var table = new RenderNode.Table(Caption: null, Columns: [], Rows: rows);
        var tree = new Twig.RenderTree.RenderTree([table]);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
        Console.WriteLine();
    }

    /// <summary>
    /// True for the machine output formats handled via the
    /// <see cref="RenderTree"/> → <see cref="IRenderer"/> seam
    /// (json, json-compact, minimal, ids). False for human/unknown formats,
    /// which fall back to <see cref="HumanOutputFormatter"/>.
    /// </summary>
    private static bool IsMachineFormat(string? outputFormat)
    {
        var normalized = outputFormat?.ToLowerInvariant();
        return normalized is "json" or "json-compact" or "minimal" or "ids";
    }

    private static List<int> ParseBatchIds(string batch)
    {
        var ids = new List<int>();
        if (string.IsNullOrWhiteSpace(batch))
            return ids;

        foreach (var segment in batch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(segment, out var id))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// Best-effort git context: detect current branch via filesystem, then look up linked PRs.
    /// Never throws — returns <see cref="GitContext.Empty"/> on any failure.
    /// </summary>
    private async Task<GitContext> BuildGitContextAsync(CancellationToken ct)
    {
        string? branch = null;
        if (twigPaths is not null)
        {
            var repoRoot = Path.GetDirectoryName(twigPaths.TwigDir);
            if (repoRoot is not null)
                branch = GitBranchReader.GetCurrentBranch(repoRoot);
        }

        if (branch is null)
            return GitContext.Empty;

        IReadOnlyList<PullRequestInfo> prs = [];
        if (adoGitService is not null)
        {
            try
            {
                prs = await adoGitService.GetPullRequestsForBranchAsync(branch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort — PR lookup failures are non-fatal
            }
        }

        return new GitContext(branch, prs);
    }

    /// <summary>
    /// Emits an error message when no active work item is set, with a hint derived from
    /// the current git branch name if it encodes a work item ID.
    /// </summary>
    private void EmitBranchDetectionHint()
    {
        ctx.StderrWriter.WriteLine("error: No active work item. Use 'twig set <id>' to set one.");

        if (twigPaths is null)
            return;

        var repoRoot = Path.GetDirectoryName(twigPaths.TwigDir);
        if (repoRoot is null)
            return;

        var branch = GitBranchReader.GetCurrentBranch(repoRoot);
        if (branch is null)
            return;

        var detectedId = ExtractWorkItemIdFromBranch(branch);
        if (detectedId.HasValue)
        {
            ctx.StderrWriter.WriteLine($"hint: Branch '{branch}' may reference work item #{detectedId.Value}.");
            ctx.StderrWriter.WriteLine($"      Try: twig set {detectedId.Value}");
        }
    }

    /// <summary>
    /// Extracts a work item ID from a branch name by scanning path segments for leading digits.
    /// Handles common conventions: <c>feature/1234-description</c>, <c>users/name/1234</c>,
    /// <c>bug/1234</c>, etc.
    /// </summary>
    internal static int? ExtractWorkItemIdFromBranch(string branchName)
    {
        foreach (var segment in branchName.Split('/'))
        {
            var dashIndex = segment.IndexOf('-');
            var candidate = dashIndex > 0 ? segment[..dashIndex] : segment;
            if (int.TryParse(candidate, out var id) && id > 0)
                return id;
        }

        return null;
    }
}
