using System.Diagnostics;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Handles <c>twig link parent</c>, <c>link unparent</c>, and <c>link reparent</c>
/// commands for managing parent–child hierarchy links on published ADO work items.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// success outcomes emit a Document with the result message plus a links Table on
/// machine formats; human format emits the success message followed by a list of
/// links. <see cref="OutputFormatterFactory"/> is retained only for stderr errors.
/// </remarks>
public sealed class LinkCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    IWorkItemLinkRepository linkRepo,
    SyncCoordinatorFactory syncCoordinatorFactory,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null,
    ITelemetryClient? telemetryClient = null,
    TextWriter? stderr = null)
{
    private const string HierarchyReverse = "System.LinkTypes.Hierarchy-Reverse";
    private readonly TextWriter _stderr = stderr ?? Console.Error;
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>
    /// Set the parent of the active work item, or of <paramref name="id"/> when given.
    /// </summary>
    /// <remarks>
    /// AB#389: <paramref name="id"/> was absent, so the only way to parent a NON-active
    /// item was <c>twig set &lt;child&gt;</c> first — two commands per item, and it
    /// mutates active-item context as a side effect of what reads like a single link
    /// operation. The dependency verbs already took an optional <c>id</c>; this brings
    /// the hierarchy verbs onto that same established pattern rather than inventing one.
    /// </remarks>
    public async Task<int> ParentAsync(
        int targetId,
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        int exitCode;
        exitCode = await ParentCoreAsync(targetId, id, outputFormat, ct);
        TelemetryHelper.TrackCommand(telemetryClient, "link-parent", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    private async Task<int> ParentCoreAsync(
        int targetId,
        int? id,
        string outputFormat,
        CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = id.HasValue
            ? await activeItemResolver.ResolveByIdAsync(id.Value, ct)
            : await activeItemResolver.GetActiveItemAsync(ct);
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out _))
            return WriteActiveItemNotFoundError(fmt, errorId);

        if (CheckParentingGuards(fmt, item, targetId, outputFormat) is int earlyExit) return earlyExit;

        if (item.ParentId.HasValue)
        {
            _stderr.WriteLine(fmt.FormatError(
                $"#{item.Id} already has parent #{item.ParentId.Value}. Use 'twig link reparent {targetId}' to change."));
            return 1;
        }

        // Validate target exists in ADO
        var targetResult = await activeItemResolver.ResolveByIdAsync(targetId, ct);
        if (!targetResult.TryGetWorkItem(out _, out _, out _))
        {
            _stderr.WriteLine(fmt.FormatError($"Target work item #{targetId} not found."));
            return 1;
        }

        await adoService.AddLinkAsync(item.Id, targetId, HierarchyReverse, ct);

        // Resync the child item and the new parent
        await ResyncItemAsync(item.Id, ct);
        await ResyncItemAsync(targetId, ct);

        var links = await linkRepo.GetLinksAsync(item.Id, ct);
        RenderLinkResult("linkParented", $"#{item.Id} is now a child of #{targetId}.", links, outputFormat);
        return 0;
    }

    /// <summary>
    /// Remove the parent link from the active work item, or from <paramref name="id"/>
    /// when given. See <see cref="ParentAsync"/> remarks for why (AB#389).
    /// </summary>
    public async Task<int> UnparentAsync(
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        int exitCode;
        exitCode = await UnparentCoreAsync(id, outputFormat, ct);
        TelemetryHelper.TrackCommand(telemetryClient, "link-unparent", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    private async Task<int> UnparentCoreAsync(
        int? id,
        string outputFormat,
        CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = id.HasValue
            ? await activeItemResolver.ResolveByIdAsync(id.Value, ct)
            : await activeItemResolver.GetActiveItemAsync(ct);
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out _))
            return WriteActiveItemNotFoundError(fmt, errorId);

        if (!item.ParentId.HasValue)
        {
            _stderr.WriteLine(fmt.FormatError($"#{item.Id} has no parent link to remove."));
            return 1;
        }

        var oldParentId = item.ParentId.Value;
        await adoService.RemoveLinkAsync(item.Id, oldParentId, HierarchyReverse, ct);

        // Resync both items
        await ResyncItemAsync(item.Id, ct);
        await ResyncItemAsync(oldParentId, ct);

        var links = await linkRepo.GetLinksAsync(item.Id, ct);
        RenderLinkResult("linkUnparented", $"Removed parent #{oldParentId} from #{item.Id}.", links, outputFormat);
        return 0;
    }

    /// <summary>
    /// Remove the current parent and set a new one atomically, on the active work item
    /// or on <paramref name="id"/> when given. See <see cref="ParentAsync"/> (AB#389).
    /// </summary>
    public async Task<int> ReparentAsync(
        int targetId,
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        int exitCode;
        exitCode = await ReparentCoreAsync(targetId, id, outputFormat, ct);
        TelemetryHelper.TrackCommand(telemetryClient, "link-reparent", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    private async Task<int> ReparentCoreAsync(
        int targetId,
        int? id,
        string outputFormat,
        CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = id.HasValue
            ? await activeItemResolver.ResolveByIdAsync(id.Value, ct)
            : await activeItemResolver.GetActiveItemAsync(ct);
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out _))
            return WriteActiveItemNotFoundError(fmt, errorId);

        if (CheckParentingGuards(fmt, item, targetId, outputFormat) is int earlyExit) return earlyExit;

        // Validate target exists in ADO
        var targetResult = await activeItemResolver.ResolveByIdAsync(targetId, ct);
        if (!targetResult.TryGetWorkItem(out _, out _, out _))
        {
            _stderr.WriteLine(fmt.FormatError($"Target work item #{targetId} not found."));
            return 1;
        }

        var oldParentId = item.ParentId;

        // Remove existing parent if present
        if (oldParentId.HasValue)
        {
            await adoService.RemoveLinkAsync(item.Id, oldParentId.Value, HierarchyReverse, ct);
        }

        // Add new parent
        await adoService.AddLinkAsync(item.Id, targetId, HierarchyReverse, ct);

        // Resync the child, the new parent, and the old parent (if different)
        await ResyncItemAsync(item.Id, ct);
        await ResyncItemAsync(targetId, ct);
        if (oldParentId.HasValue && oldParentId.Value != targetId)
        {
            await ResyncItemAsync(oldParentId.Value, ct);
        }

        var links = await linkRepo.GetLinksAsync(item.Id, ct);
        var message = oldParentId.HasValue
            ? $"#{item.Id} reparented from #{oldParentId.Value} to #{targetId}."
            : $"#{item.Id} is now a child of #{targetId}.";
        RenderLinkResult("linkReparented", message, links, outputFormat);
        return 0;
    }

    // ── Dependency links (predecessor / successor) — twig#77 ────────
    //
    // Every layer below the CLI already understood dependency links: LinkTypeMapper
    // maps them both ways, AdoResponseMapper reads them back, and twig_link over MCP
    // has always accepted "predecessor"/"successor". Only the CLI write path was
    // missing, so a map published via the CLI came out structurally weaker than the
    // same map published over MCP — blocked_by edges silently absent.
    //
    // Cycle detection is deliberately NOT implemented: only self-links are rejected.
    // A predecessor chain can still be made cyclic; ADO does not reject it and neither
    // does twig. Say so rather than implying the guard exists.
    //
    // ── Related links — AB#620 ──────────────────────────────────────
    //
    // `related` joins the same core rather than getting a parallel one. It is the third
    // non-hierarchy edge in the SAME ADO family: LinkTypeMapper already resolved it,
    // AdoResponseMapper already read it back, and WorkItemLink already modelled it — the
    // CLI write path was the only gap, which is exactly twig#77's shape one edge over.
    // A dedicated code path would have duplicated the target-exists, self-link and
    // already-linked guards, and this file's own comment above says why that is refused.

    /// <summary>
    /// Add a non-hierarchy link (<c>predecessor</c>, <c>successor</c> or <c>related</c>)
    /// from the active (or <paramref name="id"/>-specified) work item to <paramref name="targetId"/>.
    /// </summary>
    public async Task<int> DependencyAsync(
        string linkType,
        int targetId,
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await DependencyCoreAsync(linkType, targetId, id, comment: null, remove: false, outputFormat, ct);
        TelemetryHelper.TrackCommand(telemetryClient, $"link-{linkType.ToLowerInvariant()}", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    /// <summary>
    /// Add a symmetric <c>System.LinkTypes.Related</c> edge between the active (or
    /// <paramref name="id"/>-specified) work item and <paramref name="targetId"/>,
    /// carrying <paramref name="comment"/> as the relation's reason (AB#620).
    /// </summary>
    /// <remarks>
    /// Related is NON-DIRECTIONAL: ADO materialises the reverse edge itself, so writing it
    /// from either side makes it visible from both. That is why there is no "relate the other
    /// way" verb and why both endpoints are resynced — the item that was not named locally
    /// gained an edge too, and a cache that only knows about the named side is wrong.
    /// </remarks>
    public async Task<int> RelatedAsync(
        int targetId,
        int? id = null,
        string? comment = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await DependencyCoreAsync(LinkTypes.Related, targetId, id, comment, remove: false, outputFormat, ct);
        TelemetryHelper.TrackCommand(telemetryClient, "link-related", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    /// <summary>
    /// Remove the symmetric related edge between the active (or <paramref name="id"/>-specified)
    /// work item and <paramref name="targetId"/> (AB#620).
    /// </summary>
    /// <remarks>
    /// A named verb as well as <c>twig link unlink related &lt;id&gt;</c>, which also works:
    /// <c>unrelate</c> is the counterpart the card asked for and reads as the inverse of
    /// <c>related</c>, while <c>unlink</c> stays the generic form. Both route here, so there is
    /// one behaviour and not two.
    /// </remarks>
    public async Task<int> UnrelateAsync(
        int targetId,
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await DependencyCoreAsync(LinkTypes.Related, targetId, id, comment: null, remove: true, outputFormat, ct);
        TelemetryHelper.TrackCommand(telemetryClient, "link-unrelate", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    /// <summary>
    /// Remove a non-hierarchy link (<c>predecessor</c>, <c>successor</c> or <c>related</c>) from
    /// the active (or <paramref name="id"/>-specified) work item to <paramref name="targetId"/>.
    /// </summary>
    public async Task<int> UnlinkDependencyAsync(
        string linkType,
        int targetId,
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await DependencyCoreAsync(linkType, targetId, id, comment: null, remove: true, outputFormat, ct);
        TelemetryHelper.TrackCommand(telemetryClient, "link-unlink", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    private async Task<int> DependencyCoreAsync(
        string linkType,
        int targetId,
        int? id,
        string? comment,
        bool remove,
        string outputFormat,
        CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // Only the non-hierarchy kinds route here; parent/child have dedicated verbs with
        // their own guards, and accepting them here would give two divergent code paths
        // for the same operation.
        if (!IsNonHierarchyLinkType(linkType, out var friendly))
        {
            _stderr.WriteLine(fmt.FormatError(
                $"Unknown link type: '{linkType}'. Supported types: predecessor, successor, related. "
                + "Use 'twig link parent' for hierarchy links."));
            return 1;
        }

        var adoLinkType = LinkTypeMapper.Resolve(friendly);

        var resolved = id.HasValue
            ? await activeItemResolver.ResolveByIdAsync(id.Value, ct)
            : await activeItemResolver.GetActiveItemAsync(ct);

        if (!resolved.TryGetWorkItem(out var item, out var errorId, out _))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' or pass --id."));
            return 1;
        }

        if (item.Id == targetId)
        {
            _stderr.WriteLine(fmt.FormatError($"Cannot link work item #{item.Id} to itself."));
            return 1;
        }

        // Validate the target exists before mutating. #77's whole point is that a link
        // operation must not report success when nothing was created.
        var targetResult = await activeItemResolver.ResolveByIdAsync(targetId, ct);
        if (!targetResult.TryGetWorkItem(out _, out _, out _))
        {
            _stderr.WriteLine(fmt.FormatError($"Target work item #{targetId} not found."));
            return 1;
        }

        var existing = await linkRepo.GetLinksAsync(item.Id, ct);
        var alreadyLinked = existing.Any(l =>
            l.TargetId == targetId &&
            string.Equals(l.LinkType, friendly, StringComparison.OrdinalIgnoreCase));

        if (!remove && alreadyLinked)
        {
            var noopMessage = $"#{item.Id} already has {friendly} #{targetId}. No changes made.";
            RenderLinkResult("linkUnchanged", noopMessage, existing, outputFormat);
            return 0;
        }

        try
        {
            if (remove)
                await adoService.RemoveLinkAsync(item.Id, targetId, adoLinkType, ct);
            else
                await adoService.AddLinkWithCommentAsync(item.Id, targetId, adoLinkType, comment, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stderr.WriteLine(fmt.FormatError($"Link failed: {ex.Message}"));
            return 1;
        }

        await ResyncItemAsync(item.Id, ct);
        await ResyncItemAsync(targetId, ct);

        var links = await linkRepo.GetLinksAsync(item.Id, ct);
        var message = remove
            ? $"Removed {friendly} #{targetId} from #{item.Id}."
            : $"#{item.Id} now has {friendly} #{targetId}.";
        RenderLinkResult(remove ? "linkRemoved" : "linkAdded", message, links, outputFormat);
        return 0;
    }

    /// <summary>
    /// Accepts only the non-hierarchy half of <see cref="LinkTypeMapper"/>, normalising
    /// case, and emits the canonical friendly name used in messages and link records.
    /// </summary>
    private static bool IsNonHierarchyLinkType(string? linkType, out string friendly)
    {
        friendly = string.Empty;
        if (string.IsNullOrWhiteSpace(linkType)) return false;

        var normalized = linkType.Trim();
        foreach (var candidate in new[] { LinkTypes.Predecessor, LinkTypes.Successor, LinkTypes.Related })
        {
            if (normalized.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                friendly = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Re-fetches an item from ADO and updates the local cache.
    /// Non-fatal — link mutation already succeeded.
    /// </summary>
    private async Task ResyncItemAsync(int id, CancellationToken ct)
    {
        try
        {
            await syncCoordinatorFactory.ReadWrite.SyncLinksAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stderr.WriteLine($"warning: Link changed but cache may be stale for #{id} — run 'twig sync' to resync ({ex.Message})");
        }
    }

    private int? CheckParentingGuards(IOutputFormatter fmt, WorkItem item, int targetId, string outputFormat)
    {
        if (item.Id == targetId)
        {
            _stderr.WriteLine(fmt.FormatError($"Cannot parent work item #{item.Id} to itself."));
            return 1;
        }
        if (item.ParentId == targetId)
        {
            var msg = $"#{item.Id} is already a child of #{targetId}. No changes made.";
            var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
            RenderNode node = lower switch
            {
                "minimal" => new RenderNode.Text(msg),
                "json" or "json-full" or "json-compact" or "ids" =>
                    new RenderNode.Record("linkUnchanged", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                    {
                        ["itemId"] = RenderCell.Integer(item.Id),
                        ["parentId"] = RenderCell.Integer(targetId),
                        ["message"] = RenderCell.String(msg),
                    }),
                _ => new RenderNode.Text(msg, Severity.Info),
            };
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
            return 0;
        }
        return null;
    }

    private void RenderLinkResult(string kind, string message, IReadOnlyList<WorkItemLink> links, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        if (lower is "json" or "json-full" or "json-compact" or "ids")
        {
            var columns = new List<RenderColumn>
            {
                new("sourceId", "Source"),
                new("targetId", "Target"),
                new("linkType", "Type"),
            };
            var rows = new List<RenderRow>(links.Count);
            foreach (var link in links)
            {
                rows.Add(new RenderRow("link", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["sourceId"] = RenderCell.Integer(link.SourceId),
                    ["targetId"] = RenderCell.Integer(link.TargetId),
                    ["linkType"] = RenderCell.String(link.LinkType),
                }));
            }
            var fields = new List<DocumentField>(3)
            {
                new("message", new RenderNode.KeyValue("message", RenderCell.String(message))),
                new("count", new RenderNode.KeyValue("count", RenderCell.Integer(links.Count))),
                new("links", new RenderNode.Table(null, columns, rows)),
            };
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[]
            {
                (RenderNode)new RenderNode.Document(kind, fields),
            }));
            return;
        }

        if (lower == "minimal")
        {
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[]
            {
                (RenderNode)new RenderNode.Text(message),
            }));
            return;
        }

        var nodes = new List<RenderNode>(links.Count + 1)
        {
            new RenderNode.Text(message, Severity.Success),
        };
        foreach (var link in links)
            nodes.Add(new RenderNode.Text($"  #{link.SourceId} ──{link.LinkType}──▶ #{link.TargetId}", Severity.Info));
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(nodes));
    }

    private int WriteActiveItemNotFoundError(IOutputFormatter fmt, int? errorId)
    {
        _stderr.WriteLine(fmt.FormatError(errorId is not null
            ? $"Work item #{errorId} not found in cache."
            : "No active work item. Run 'twig set <id>' first."));
        return 1;
    }
}
