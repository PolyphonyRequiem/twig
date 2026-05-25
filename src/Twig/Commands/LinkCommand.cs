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

    /// <summary>Set the parent of the active work item.</summary>
    public async Task<int> ParentAsync(
        int targetId,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        using var scope = new CommandActivityScope("link-parent", outputFormat);
        int exitCode;
        try
        {
            exitCode = await ParentCoreAsync(targetId, outputFormat, ct);
            scope.Complete(exitCode);
            TelemetryHelper.TrackCommand(telemetryClient, "link-parent", outputFormat, exitCode, scope.StartTimestamp);
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
    }

    private async Task<int> ParentCoreAsync(
        int targetId,
        string outputFormat,
        CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
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

    /// <summary>Remove the parent link from the active work item.</summary>
    public async Task<int> UnparentAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        using var scope = new CommandActivityScope("link-unparent", outputFormat);
        int exitCode;
        try
        {
            exitCode = await UnparentCoreAsync(outputFormat, ct);
            scope.Complete(exitCode);
            TelemetryHelper.TrackCommand(telemetryClient, "link-unparent", outputFormat, exitCode, scope.StartTimestamp);
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
    }

    private async Task<int> UnparentCoreAsync(
        string outputFormat,
        CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
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

    /// <summary>Remove the current parent and set a new one atomically.</summary>
    public async Task<int> ReparentAsync(
        int targetId,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        using var scope = new CommandActivityScope("link-reparent", outputFormat);
        int exitCode;
        try
        {
            exitCode = await ReparentCoreAsync(targetId, outputFormat, ct);
            scope.Complete(exitCode);
            TelemetryHelper.TrackCommand(telemetryClient, "link-reparent", outputFormat, exitCode, scope.StartTimestamp);
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
    }

    private async Task<int> ReparentCoreAsync(
        int targetId,
        string outputFormat,
        CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
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
