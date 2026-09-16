using System.Diagnostics;
using Spectre.Console.Rendering;
using Twig.Domain.Common;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
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
/// navigation history. Reads are cache-only by default (wayfinder 0004 §3);
/// pass <c>--refresh</c> to fetch the item and its links, including on a cache miss.
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

    public async Task<int> ExecuteAsync(int? id = null, string outputFormat = OutputFormatterFactory.DefaultFormat, bool tree = false, bool refresh = false, CancellationToken ct = default, int? depth = null, bool noLive = false, string? fields = null, string? sections = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        int exitCode;
        var request = ShowProjection.Request.Parse(fields, sections);
        if (request.IsActive && (tree || OutputFormats.Normalize(outputFormat) is not ("json" or "json-full" or "json-compact")))
        {
            CommandError.Write(_rendererFactory, ctx.StderrWriter, outputFormat, "Field/section projection requires JSON output and cannot be combined with --tree.");
            return 2;
        }

        if (tree)
        {
            if (treeRenderingService is null)
            {
                ctx.StderrWriter.WriteLine("error: Tree rendering is not available.");
                exitCode = 1;
            }
            else
            {
                exitCode = await treeRenderingService.RenderTreeAsync(id, outputFormat, depth, noLive, refresh, ct);
            }

            TelemetryHelper.TrackCommand(ctx.TelemetryClient, "show", outputFormat, exitCode, startTimestamp,
                new Dictionary<string, string> { ["tree"] = "true" });
            return exitCode;
        }

        exitCode = await ExecuteCoreAsync(id, outputFormat, refresh, request, ct);
        TelemetryHelper.TrackCommand(ctx.TelemetryClient, "show", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    /// <summary>
    /// Batch lookup: accepts comma-separated IDs, returns found items on stdout and a
    /// structured stderr disclosure of any requested IDs the cache does not carry (AB#880).
    /// Cache-only — no ADO fetch. When <paramref name="fields"/> or <paramref name="sections"/>
    /// is set, the response is a compact projection envelope with truthful field statuses
    /// (present / absent / unknown) instead of the full items[] array.
    /// </summary>
    public async Task<int> ExecuteBatchAsync(string batch, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default, string? fields = null, string? sections = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var request = ShowProjection.Request.Parse(fields, sections);
        if (request.IsActive && OutputFormats.Normalize(outputFormat) is not ("json" or "json-full" or "json-compact"))
        {
            CommandError.Write(_rendererFactory, ctx.StderrWriter, outputFormat, "Field/section projection requires JSON output.");
            return 2;
        }
        var exitCode = await ExecuteBatchCoreAsync(batch, outputFormat, request, ct);
        TelemetryHelper.TrackCommand(ctx.TelemetryClient, "show-batch", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    private async Task<int> ExecuteCoreAsync(int? id, string outputFormat, bool refresh, ShowProjection.Request request, CancellationToken ct)
    {
        var (fmt, renderer) = ctx.Resolve(outputFormat);

        Domain.Aggregates.WorkItem item;
        int resolvedId;
        SyncResult? initialRefreshResult = null;

        if (id.HasValue)
        {
            // ── By-ID path — cache-first lookup ──
            resolvedId = id.Value;
            var cached = await workItemRepo.GetByIdAsync(resolvedId, ct);
            if (cached is null && refresh && resolvedId > 0)
            {
                initialRefreshResult = await syncCoordinatorFactory.ReadOnly.SyncRootLinksAsync(resolvedId, ct);
                if (initialRefreshResult is SyncFailed failed)
                {
                    CommandError.Write(_rendererFactory, ctx.StderrWriter, outputFormat, $"Refresh failed for #{resolvedId}: {failed.Reason}");
                    return 1;
                }
                cached = await workItemRepo.GetByIdAsync(resolvedId, ct);
            }
            if (cached is null)
            {
                CommandError.Write(_rendererFactory, ctx.StderrWriter, outputFormat, refresh
                    ? $"Work item #{resolvedId} could not be loaded after refresh; local pending changes may protect it."
                    : $"Work item #{resolvedId} not found in local cache. Run 'twig show {resolvedId} --refresh' to fetch it without changing context.");
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
                    // AB#738 requires primary-scope status to render even
                    // when Twig Context is unset (attachment is a separate
                    // identity). Emit the status line before the branch hint
                    // so a managed unattached checkout is observable without
                    // first setting Context.
                    if (!IsMachineFormat(outputFormat) && ctx.AttachmentStatus is { } noCtxAttachment)
                    {
                        var noCtxProj = await noCtxAttachment.ReadAsync(ct);
                        RenderAttachmentStatusLine(noCtxProj);
                    }
                    EmitBranchDetectionHint();
                    return 1;
            }
            resolvedId = item.Id;
        }

        // AB#880 opt-in projection: bypass full-detail enrichment when the
        // caller asked for a slice; omit both flags to get the full read back.
        if (request.IsActive)
            return await ExecuteProjectionAsync(item, resolvedId, outputFormat, refresh, request, initialRefreshResult, ct);

        // Wayfinder 0004 §3: the read reports freshness rather than acting on it. Only the
        // rich/human surface renders the hint; machine formats keep a stable, quiet contract.
        if (!refresh && !IsMachineFormat(outputFormat))
        {
            var freshness = await syncCoordinatorFactory.ReadOnly.ReadItemAsync(resolvedId, ct);
            if (freshness is Stale stale)
                ctx.StderrWriter.WriteLine(StaleHint.Format(stale.LastSyncedAt));
        }

        // Enrichment — all cache-only, best-effort
        var children = await workItemRepo.GetChildrenAsync(item.Id, ct);
        Domain.Aggregates.WorkItem? parent = item.ParentId.HasValue
            ? await workItemRepo.GetByIdAsync(item.ParentId.Value, ct)
            : null;

        // AB#831: the edges and the answer to "were these edges ever fetched?" are read
        // together, because an empty list on its own cannot tell those two states apart.
        IReadOnlyList<WorkItemLink> links = [];
        DateTimeOffset? linksVerifiedAt = null;
        try
        {
            links = await linkRepo.GetLinksAsync(item.Id, ct);
            linksVerifiedAt = await linkRepo.GetLinksVerifiedAtAsync(item.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        // Human surface only, in the same idiom as the staleness hint above — including its
        // `!refresh` guard: telling someone to "pass --refresh" on a run that already passed it
        // is a contradiction, and the refresh below is about to verify these very edges.
        // A machine read keeps its quiet contract and gets the same signal structurally, as
        // `linksVerifiedAt: null`.
        if (linksVerifiedAt is null && !refresh && !IsMachineFormat(outputFormat))
            ctx.StderrWriter.WriteLine(UnverifiedLinksHint.Format(resolvedId));

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
            if (resolvedId <= 0)
                return new UpToDate();
            var changedCount = 0;
            var failures = new List<SyncItemFailure>();

            void RecordResult(SyncResult result, int itemId)
            {
                switch (result)
                {
                    case Updated updated:
                        changedCount += updated.ChangedCount;
                        break;
                    case PartiallyUpdated partial:
                        changedCount += partial.SavedCount;
                        failures.AddRange(partial.Failures);
                        break;
                    case SyncFailed failed:
                        var prefix = $"#{itemId}: ";
                        var error = failed.Reason.StartsWith(prefix, StringComparison.Ordinal)
                            ? failed.Reason[prefix.Length..]
                            : failed.Reason;
                        failures.Add(new SyncItemFailure(itemId, error));
                        break;
                }
            }

            var linkSync = initialRefreshResult
                ?? await syncCoordinatorFactory.ReadOnly.SyncRootLinksAsync(resolvedId, refreshCt);
            RecordResult(linkSync, resolvedId);

            var refreshedItem = await workItemRepo.GetByIdAsync(resolvedId, refreshCt);
            if (refreshedItem?.ParentId is > 0)
            {
                var parentSync = await syncCoordinatorFactory.ReadOnly.SyncItemSetAsync(
                    [refreshedItem.ParentId.Value],
                    refreshCt);
                RecordResult(parentSync, refreshedItem.ParentId.Value);
            }

            if (failures.Count > 0)
            {
                return changedCount > 0
                    ? new PartiallyUpdated(changedCount, failures)
                    : new SyncFailed(string.Join("; ", failures.Select(failure => $"#{failure.Id}: {failure.Error}")));
            }

            // Link refresh always fetches the root item, so rebuild the TTY view even when targets were current.
            return new Updated(Math.Max(changedCount, 1));
        }

        // Non-TTY machine output: sync synchronously before emitting so consumers get fresh data.
        // The TTY path handles sync via RenderWithSyncAsync (two-pass: cached → sync → revised).
        if (renderer is null && refresh)
        {
            try
            {
                var syncResult = await RefreshItemAndLinksAsync(ct);
                if (syncResult is SyncFailed failed)
                {
                    CommandError.Write(_rendererFactory, ctx.StderrWriter, outputFormat, $"Refresh failed for #{resolvedId}: {failed.Reason}");
                    return 1;
                }
                if (syncResult is PartiallyUpdated partial)
                {
                    CommandError.Write(_rendererFactory, ctx.StderrWriter, outputFormat,
                        $"Refresh incomplete for #{resolvedId}: " +
                        string.Join("; ", partial.Failures.Select(f => $"#{f.Id}: {f.Error}")));
                    return 1;
                }

                // Reload data from cache after sync
                var freshItem = await workItemRepo.GetByIdAsync(resolvedId, ct);
                if (freshItem is not null)
                {
                    item = freshItem;
                    children = await workItemRepo.GetChildrenAsync(item.Id, ct);
                    parent = item.ParentId.HasValue
                        ? await workItemRepo.GetByIdAsync(item.ParentId.Value, ct)
                        : null;

                    try
                    {
                        links = await linkRepo.GetLinksAsync(item.Id, ct);
                        linksVerifiedAt = await linkRepo.GetLinksVerifiedAtAsync(item.Id, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

                    childProgress = processConfigProvider.ComputeChildProgress(children);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CommandError.Write(_rendererFactory, ctx.StderrWriter, outputFormat, $"Refresh failed for #{resolvedId}: {ex.Message}");
                return 1;
            }
        }


        // AB#738 status projection. Emitted on human-audience surfaces only —
        // the machine surface receives the same signal via .twig/prompt.json's
        // `primaryScope` block (PromptStateWriter). Unattached checkouts state
        // the fact explicitly per the ticket contract; unmanaged checkouts
        // (rendering a work item outside a twig worktree) get no block at all.
        if (!IsMachineFormat(outputFormat) && ctx.AttachmentStatus is { } attachmentStatus)
        {
            var proj = await attachmentStatus.ReadAsync(ct);
            RenderAttachmentStatusLine(proj);
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

            if (renderer is SpectreRenderer spectreRenderer && refresh)
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
                        performSync: () => RefreshItemAndLinksAsync(ct),
                        buildRevisedView: async _ =>
                        {
                            var freshItem = await workItemRepo.GetByIdAsync(resolvedId, ct);
                            if (freshItem is null) return null;

                            var freshChildren = await workItemRepo.GetChildrenAsync(freshItem.Id, ct);
                            var freshParent = freshItem.ParentId.HasValue
                                ? await workItemRepo.GetByIdAsync(freshItem.ParentId.Value, ct)
                                : null;

                            IReadOnlyList<WorkItemLink> freshLinks = [];
                            try { freshLinks = await linkRepo.GetLinksAsync(freshItem.Id, ct); }
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
                        ct);
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

            RenderWorkItemTree(item, links, linksVerifiedAt, parent, children, gitContext, pendingCounts, outputFormat);
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
        DateTimeOffset? linksVerifiedAt,
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
            _ => BuildFullDocument(item, links, linksVerifiedAt, parent, children, gitContext, pendingChanges),
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
        DateTimeOffset? linksVerifiedAt,
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
            // AB#618: ALWAYS emitted, for the same reason as `children`/`links`/`relations`
            // below — missing-vs-zero ambiguity is what makes a `twig note` write
            // unverifiable. A consumer must be able to tell "this item has no comments" from
            // "twig does not report comments", and only an always-present key does that.
            new("commentCount", new RenderNode.KeyValue("commentCount", coreCells["commentCount"])),
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

        // AB#831. ALWAYS emitted, and null-valued rather than omitted when the edge set has
        // never been fetched — this is the key that makes the two arrays above readable at all.
        // `links: []` with a timestamp is a VERIFIED empty edge set; `links: []` with null is
        // "this cache has never asked ADO", which used to be reported as the same thing and led
        // two agent sessions to conclude an item had no blocking graph when its edges existed.
        fields.Add(new DocumentField("linksVerifiedAt", new RenderNode.KeyValue(
            "linksVerifiedAt",
            LinksVerifiedAtCell(linksVerifiedAt))));

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
            ["commentCount"] = RenderCell.Integer(item.ReadCommentCount()),
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
        return new RenderNode.Record("workItemLink", BuildLinkCells(link));
    }

    /// <summary>
    /// Projects the edge-set verification instant (AB#831): an ISO-8601 timestamp when this
    /// item's edges have been read from ADO, an explicit machine <c>null</c> when they never
    /// have. Never <see cref="RenderValue.Absent"/> — omitting the key would recreate the very
    /// missing-vs-empty ambiguity the key exists to resolve.
    /// </summary>
    private static RenderCell LinksVerifiedAtCell(DateTimeOffset? verifiedAt) =>
        verifiedAt is { } instant
            ? new RenderCell(
                instant.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                new RenderValue.DateTime(instant))
            : new RenderCell("never", new RenderValue.Null());

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
        return new RenderNode.Record(null, BuildRelationCells(link));
    }

    /// <summary>
    /// The <c>links</c> wire shape for one edge, shared by the single-item document and the
    /// per-row <c>links</c> array of <c>show-batch</c> (ADO #154) so the two cannot drift.
    /// </summary>
    private static Dictionary<string, RenderCell> BuildLinkCells(WorkItemLink link)
    {
        return new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["sourceId"] = RenderCell.Integer(link.SourceId),
            ["targetId"] = RenderCell.Integer(link.TargetId),
            ["linkType"] = RenderCell.String(link.LinkType ?? string.Empty),
        };
    }

    /// <summary>
    /// The ADO-shaped <c>relations</c> wire shape for one edge, shared by the single-item
    /// document and the per-row <c>relations</c> array of <c>show-batch</c> (ADO #154).
    /// </summary>
    private static Dictionary<string, RenderCell> BuildRelationCells(WorkItemLink link)
    {
        var friendlyName = link.LinkType ?? string.Empty;
        var adoRel = !string.IsNullOrEmpty(friendlyName) && LinkTypeMapper.TryResolve(friendlyName, out var resolved)
            ? resolved
            : friendlyName;

        var attributes = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["name"] = RenderCell.String(friendlyName),
        };

        return new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(link.TargetId),
            ["rel"] = RenderCell.String(adoRel),
            ["url"] = RenderCell.String(string.Empty),
            ["attributes"] = new RenderCell(string.Empty, new RenderValue.Object(attributes)),
        };
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

    private async Task<int> ExecuteBatchCoreAsync(string batch, string outputFormat, ShowProjection.Request request, CancellationToken ct)
    {
        var ids = ParseBatchIds(batch);
        var items = new List<Domain.Aggregates.WorkItem>();
        // Track requested-but-missing ids in caller order. Non-positive segments
        // (parse failures, negative-id staged seeds) are excluded so a caller who
        // passes `10, garbage, 20` is not told #0 is missing — the input parser
        // simply never mapped that segment to an id worth reporting.
        var missing = new List<int>();

        foreach (var id in ids)
        {
            var item = await workItemRepo.GetByIdAsync(id, ct);
            if (item is not null)
                items.Add(item);
            else if (id > 0)
                missing.Add(id);
        }

        // ADO #154: links belong to the SET, so they are read for every item found —
        // one plural repository call, not one call per id. Best-effort: a link-store
        // failure must degrade a batch read to items-without-edges rather than fail it,
        // matching the single-item path above (see the try/catch at ExecuteCoreAsync).
        IReadOnlyList<WorkItemLink> links = [];
        IReadOnlyDictionary<int, DateTimeOffset> linksVerifiedAt = new Dictionary<int, DateTimeOffset>();
        if (items.Count > 0)
        {
            var foundIds = new List<int>(items.Count);
            foreach (var item in items)
                foundIds.Add(item.Id);

            // AB#831: the plural verification read pairs with the plural edge read — one query
            // each, so a set consumer learns which members' edge sets it may trust without
            // falling back to one refresh per id.
            try
            {
                links = await linkRepo.GetLinksForSetAsync(foundIds, ct);
                linksVerifiedAt = await linkRepo.GetLinksVerifiedAtForSetAsync(foundIds, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                links = [];
                linksVerifiedAt = new Dictionary<int, DateTimeOffset>();
            }
        }

        if (request.IsActive)
            return await RenderBatchProjectionAsync(ids, items, missing, links, linksVerifiedAt, request, outputFormat, ct);

        var graph = WorkItemGraph.Build(items, links);

        var fmt = ctx.FormatterFactory.GetFormatter(outputFormat);

        // After AB#3301 the factory always returns HumanOutputFormatter, so
        // dispatch on the requested format string rather than the formatter
        // type. Human/unknown formats render the rich Spectre card; machine
        // formats (json, json-compact, minimal, ids) flow through the
        // RenderTree → IRenderer seam.
        if (!IsMachineFormat(outputFormat) && fmt is HumanOutputFormatter humanFmt)
        {
            foreach (var item in graph.Items)
                Console.WriteLine(humanFmt.FormatWorkItem(item, showDirty: false));
        }
        else
        {
            RenderBatchAsTree(graph, linksVerifiedAt, outputFormat);
        }

        // AB#880: a batch that was asked for ids the cache does not carry MUST
        // disclose the shortfall. The found-items stdout shape is unchanged so
        // legacy consumers keep parsing; the loss is disclosed on stderr as a
        // structured error carrying every missing id in `#N` form, and the exit
        // code becomes 1 so a caller who cares about completeness sees it.
        if (missing.Count > 0)
        {
            CommandError.Write(_rendererFactory, ctx.StderrWriter, outputFormat,
                ShowProjection.FormatMissingMessage(missing));
            return 1;
        }

        return 0;
    }

    private async Task<int> RenderBatchProjectionAsync(
        IReadOnlyList<int> requestedIds,
        IReadOnlyList<Domain.Aggregates.WorkItem> items,
        IReadOnlyList<int> missing,
        IReadOnlyList<WorkItemLink> links,
        IReadOnlyDictionary<int, DateTimeOffset> linksVerifiedAt,
        ShowProjection.Request request,
        string outputFormat,
        CancellationToken ct)
    {
        bool needLinks = false, needParent = false, needChildren = false;
        foreach (var s in request.Sections)
        {
            if (string.Equals(s, "links", StringComparison.OrdinalIgnoreCase)) needLinks = true;
            else if (string.Equals(s, "parent", StringComparison.OrdinalIgnoreCase)) needParent = true;
            else if (string.Equals(s, "children", StringComparison.OrdinalIgnoreCase)) needChildren = true;
        }

        var graph = needLinks ? WorkItemGraph.Build(items, links) : null;
        var protectedIds = await ReadProjectionProtectedIdsAsync(ct);
        var itemDocs = new List<RenderNode.Document>(items.Count);
        foreach (var item in items)
        {
            var itemLinks = needLinks && graph is not null
                ? graph.GetLinks(item.Id)
                : (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>();
            DateTimeOffset? verifiedAt = linksVerifiedAt.TryGetValue(item.Id, out var v) ? v : null;

            Domain.Aggregates.WorkItem? parent = null;
            if (needParent && item.ParentId.HasValue)
                parent = await workItemRepo.GetByIdAsync(item.ParentId.Value, ct);

            IReadOnlyList<Domain.Aggregates.WorkItem> children = needChildren
                ? await workItemRepo.GetChildrenAsync(item.Id, ct)
                : Array.Empty<Domain.Aggregates.WorkItem>();

            itemDocs.Add(ShowProjection.BuildItem(
                item, itemLinks, verifiedAt, parent, children, request,
                connection: ShowProjection.FormatConnection(ctx.Config),
                route: ShowProjection.ReadRoute.Cache,
                hasLocalChanges: protectedIds?.Contains(item.Id)));
        }

        var envelope = ShowProjection.BuildBatch(
            requestedIds: requestedIds,
            foundItems: itemDocs,
            missingIds: missing,
            connection: ShowProjection.FormatConnection(ctx.Config),
            route: ShowProjection.ReadRoute.Cache);

        var tree = new Twig.RenderTree.RenderTree([envelope]);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
        Console.WriteLine();

        return missing.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// Single-item projection path (AB#880). Reuses the refresh sync when
    /// <paramref name="refresh"/> is set; loads links/parent/children only when
    /// requested. Skips git context, status-field lookups, child-progress, and
    /// all-fields definitions.
    /// </summary>
    private async Task<int> ExecuteProjectionAsync(
        Domain.Aggregates.WorkItem item,
        int resolvedId,
        string outputFormat,
        bool refresh,
        ShowProjection.Request request,
        SyncResult? initialRefreshResult,
        CancellationToken ct)
    {
        if (refresh && resolvedId > 0)
        {
            var syncResult = initialRefreshResult
                ?? await syncCoordinatorFactory.ReadOnly.SyncRootLinksAsync(resolvedId, ct);
            if (syncResult is SyncFailed failed)
            {
                CommandError.Write(_rendererFactory, ctx.StderrWriter, outputFormat,
                    $"Refresh failed for #{resolvedId}: {failed.Reason}");
                return 1;
            }
            if (syncResult is PartiallyUpdated partial)
            {
                CommandError.Write(_rendererFactory, ctx.StderrWriter, outputFormat,
                    $"Refresh incomplete for #{resolvedId}: " +
                    string.Join("; ", partial.Failures.Select(f => $"#{f.Id}: {f.Error}")));
                return 1;
            }
            var refreshed = await workItemRepo.GetByIdAsync(resolvedId, ct);
            if (refreshed is not null)
                item = refreshed;
        }

        bool needLinks = false, needParent = false, needChildren = false;
        foreach (var s in request.Sections)
        {
            if (string.Equals(s, "links", StringComparison.OrdinalIgnoreCase)) needLinks = true;
            else if (string.Equals(s, "parent", StringComparison.OrdinalIgnoreCase)) needParent = true;
            else if (string.Equals(s, "children", StringComparison.OrdinalIgnoreCase)) needChildren = true;
        }

        // linksVerifiedAt is part of freshness, read whether or not the caller
        // requested the links section.
        IReadOnlyList<WorkItemLink> links = Array.Empty<WorkItemLink>();
        DateTimeOffset? linksVerifiedAt = null;
        try
        {
            linksVerifiedAt = await linkRepo.GetLinksVerifiedAtAsync(item.Id, ct);
            if (needLinks)
                links = await linkRepo.GetLinksAsync(item.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { linksVerifiedAt = null; }

        Domain.Aggregates.WorkItem? parent = null;
        if (needParent && item.ParentId.HasValue)
            parent = await workItemRepo.GetByIdAsync(item.ParentId.Value, ct);

        IReadOnlyList<Domain.Aggregates.WorkItem> children = needChildren
            ? await workItemRepo.GetChildrenAsync(item.Id, ct)
            : Array.Empty<Domain.Aggregates.WorkItem>();

        var doc = ShowProjection.BuildItem(
            item, links, linksVerifiedAt, parent, children, request,
            ShowProjection.FormatConnection(ctx.Config),
            refresh ? ShowProjection.ReadRoute.Refresh : ShowProjection.ReadRoute.Cache,
            (await ReadProjectionProtectedIdsAsync(ct))?.Contains(item.Id));

        var tree = new Twig.RenderTree.RenderTree([doc]);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
        Console.WriteLine();
        return 0;
    }

    private async Task<IReadOnlySet<int>?> ReadProjectionProtectedIdsAsync(CancellationToken ct)
    {
        if (_pendingChangeStore is null) return null;
        try
        {
            // Reuse sync protection's dirty-or-pending truth; one plural lookup per batch.
            return await SyncGuard.GetProtectedItemIdsAsync(workItemRepo, _pendingChangeStore, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Missing local evidence must not be reported as a clean server snapshot.
            return null;
        }
    }

    /// <summary>
    /// Renders a batch lookup result through the
    /// <see cref="RenderTree"/> → <see cref="IRenderer"/> seam. Projects each
    /// work item as a row of a single top-level <see cref="RenderNode.Table"/>
    /// so the JSON renderer emits a top-level array and the IDs renderer
    /// emits one ID per row — matching the legacy
    /// <c>JsonOutputFormatter.FormatWorkItemBatch</c> wire shape.
    /// </summary>
    /// <remarks>
    /// Each row additionally carries <c>links</c> and <c>relations</c> arrays plus a
    /// <c>linksVerifiedAt</c> instant (ADO #154, AB#831). All three are ALWAYS emitted —
    /// the arrays possibly empty, the instant possibly null — for the same reason the
    /// single-item document always emits them: a consumer must be able to read the key
    /// without first testing for its presence, and missing-vs-empty ambiguity silently
    /// breaks integrators. An empty array beside a null instant means "never fetched",
    /// not "no edges".
    /// </remarks>
    private void RenderBatchAsTree(
        WorkItemGraph graph,
        IReadOnlyDictionary<int, DateTimeOffset> linksVerifiedAt,
        string outputFormat)
    {
        var rows = new List<RenderRow>(graph.Items.Count);
        foreach (var item in graph.Items)
        {
            var cells = BuildCoreCells(item);
            var itemLinks = graph.GetLinks(item.Id);

            var linkCells = new List<RenderCell>(itemLinks.Count);
            var relationCells = new List<RenderCell>(itemLinks.Count);
            foreach (var link in itemLinks)
            {
                linkCells.Add(new RenderCell(string.Empty, new RenderValue.Object(BuildLinkCells(link))));
                relationCells.Add(new RenderCell(string.Empty, new RenderValue.Object(BuildRelationCells(link))));
            }

            cells["links"] = new RenderCell(string.Empty, new RenderValue.Array(linkCells));
            cells["relations"] = new RenderCell(string.Empty, new RenderValue.Array(relationCells));
            cells["linksVerifiedAt"] = LinksVerifiedAtCell(
                linksVerifiedAt.TryGetValue(item.Id, out var verifiedAt) ? verifiedAt : null);

            rows.Add(new RenderRow(null, cells));
        }

        var table = new RenderNode.Table(Caption: null, Columns: [], Rows: rows);
        var tree = new Twig.RenderTree.RenderTree([table]);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
        Console.WriteLine();
    }

    /// <summary>
    /// True for the machine output formats handled via the
    /// <see cref="RenderTree"/> → <see cref="IRenderer"/> seam
    /// (json, json-full, json-compact, minimal, ids). False for human/unknown formats,
    /// which fall back to <see cref="HumanOutputFormatter"/>.
    /// </summary>
    /// <remarks>
    /// 🔴 <c>json-full</c> belongs here and was missing (found reviewing AB#831). It is a
    /// machine format everywhere else — <see cref="RenderWorkItemTree"/> routes it to
    /// <see cref="BuildFullDocument"/>, and <c>RefreshCommand</c> classifies it as machine —
    /// so omitting it here let human prose reach a scripted read: the staleness hint and the
    /// AB#831 unverified-links hint on stderr, and worse, the AB#738 <c>Primary Scope:</c>
    /// line on <b>stdout</b>, ahead of the document, which made
    /// <c>twig show &lt;id&gt; -o json-full | jq</c> fail to parse outright.
    /// </remarks>
    private static bool IsMachineFormat(string? outputFormat)
    {
        var normalized = outputFormat?.ToLowerInvariant();
        return normalized is "json" or "json-full" or "json-compact" or "minimal" or "ids";
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
    /// Renders the AB#738 primary-scope status line on human-audience
    /// surfaces. Shared by the with-context and no-context paths so a
    /// managed unattached checkout is observable identically whether or
    /// not Twig Context is currently set. Machine surfaces route through
    /// <c>.twig/prompt.json</c>'s <c>primaryScope</c> block instead.
    /// </summary>
    private static void RenderAttachmentStatusLine(Twig.Domain.Interfaces.StatusProjection proj)
    {
        if (proj.FailureCode is { } failure)
        {
            Console.WriteLine($"Primary Scope: (unavailable — {failure})");
            return;
        }
        if (!proj.IsManagedWorktree)
            return;
        if (proj.HasPrimaryScope)
        {
            var label = proj.PrimaryScopeTitle is { Length: > 0 }
                ? $"Primary Scope: #{proj.PrimaryScopeWorkItemId} {proj.PrimaryScopeTitle}"
                : $"Primary Scope: #{proj.PrimaryScopeWorkItemId}";
            Console.WriteLine(label);
        }
        else
        {
            Console.WriteLine("Primary Scope: (not attached)");
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
