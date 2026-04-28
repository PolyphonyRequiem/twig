using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Handles <c>twig link parent</c>, <c>link unparent</c>, and <c>link reparent</c>
/// commands for managing parent–child hierarchy links on published ADO work items.
/// </summary>
public sealed class LinkCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    IWorkItemLinkRepository linkRepo,
    SyncCoordinatorPair syncCoordinatorPair,
    OutputFormatterFactory formatterFactory,
    TextWriter? stderr = null)
{
    private const string HierarchyReverse = "System.LinkTypes.Hierarchy-Reverse";
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>Set the parent of the active work item.</summary>
    public async Task<int> ParentAsync(
        int targetId,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out _))
            return WriteActiveItemNotFoundError(fmt, errorId);

        if (CheckParentingGuards(fmt, item, targetId) is int earlyExit) return earlyExit;

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
        Console.WriteLine(fmt.FormatSuccess($"#{item.Id} is now a child of #{targetId}."));
        Console.WriteLine(fmt.FormatWorkItemLinks(links));
        return 0;
    }

    /// <summary>Remove the parent link from the active work item.</summary>
    public async Task<int> UnparentAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
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
        Console.WriteLine(fmt.FormatSuccess($"Removed parent #{oldParentId} from #{item.Id}."));
        Console.WriteLine(fmt.FormatWorkItemLinks(links));
        return 0;
    }

    /// <summary>Remove the current parent and set a new one atomically.</summary>
    public async Task<int> ReparentAsync(
        int targetId,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out _))
            return WriteActiveItemNotFoundError(fmt, errorId);

        if (CheckParentingGuards(fmt, item, targetId) is int earlyExit) return earlyExit;

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
        Console.WriteLine(fmt.FormatSuccess(message));
        Console.WriteLine(fmt.FormatWorkItemLinks(links));
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
            await syncCoordinatorPair.ReadWrite.SyncLinksAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stderr.WriteLine($"warning: Link changed but cache may be stale for #{id} — run 'twig sync' to resync ({ex.Message})");
        }
    }

    private int? CheckParentingGuards(IOutputFormatter fmt, WorkItem item, int targetId)
    {
        if (item.Id == targetId)
        {
            _stderr.WriteLine(fmt.FormatError($"Cannot parent work item #{item.Id} to itself."));
            return 1;
        }
        if (item.ParentId == targetId)
        {
            Console.WriteLine(fmt.FormatInfo($"#{item.Id} is already a child of #{targetId}. No changes made."));
            return 0;
        }
        return null;
    }

    private int WriteActiveItemNotFoundError(IOutputFormatter fmt, int? errorId)
    {
        _stderr.WriteLine(fmt.FormatError(errorId is not null
            ? $"Work item #{errorId} not found in cache."
            : "No active work item. Run 'twig set <id>' first."));
        return 1;
    }
}