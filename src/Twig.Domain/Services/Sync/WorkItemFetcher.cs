using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;

namespace Twig.Domain.Services.Sync;

/// <summary>
/// Cache-first work item reads with an Azure DevOps fallback and a best-effort cache warm.
/// </summary>
/// <remarks>
/// Extracted from <c>Twig.Mcp.Services.WorkspaceContext</c> when that mirror was deleted
/// (wayfinder 0016). The behaviour is unchanged; only its home moved, so every surface can
/// resolve it from the shared registration instead of reaching through a bundle object.
/// </remarks>
public sealed class WorkItemFetcher(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService)
{
    /// <summary>
    /// Fetches a work item by ID: cache-first, ADO fallback, best-effort cache warm.
    /// Returns an error string (not <c>null</c>) on failure; callers wrap it for their surface.
    /// </summary>
    public async Task<(WorkItem? Item, string? Error)> FetchWithFallbackAsync(int id, CancellationToken ct)
    {
        var item = await workItemRepo.GetByIdAsync(id, ct);
        if (item is not null) return (item, null);

        try { item = await adoService.FetchAsync(id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return (null, $"Work item #{id} not found in cache or ADO: {ex.Message}"); }

        try { await workItemRepo.SaveAsync(item, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return (item, null);
    }

    /// <summary>
    /// Fetches children for <paramref name="parentId"/>: cache-first, ADO fallback on empty cache,
    /// best-effort cache warm. ADO failures are swallowed;
    /// <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    public async Task<IReadOnlyList<WorkItem>> FetchChildrenWithFallbackAsync(int parentId, CancellationToken ct)
    {
        var children = await workItemRepo.GetChildrenAsync(parentId, ct);
        if (children.Count > 0) return children;

        try { children = await adoService.FetchChildrenAsync(parentId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return []; }

        if (children.Count > 0)
        {
            try { await workItemRepo.SaveBatchAsync(children, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }
        }

        return children;
    }
}
