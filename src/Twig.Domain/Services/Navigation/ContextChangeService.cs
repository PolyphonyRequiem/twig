using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;

namespace Twig.Domain.Services.Navigation;

/// <summary>
/// Orchestrates working set extension when the active context changes.
/// Additively hydrates the cache with the parent chain (up to root),
/// 2 levels of children, and 1 level of related links around the target item.
/// All errors are swallowed — extension failures must never fail the calling command.
/// </summary>
public sealed class ContextChangeService(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    SyncCoordinator syncCoordinator,
    ProtectedCacheWriter protectedCacheWriter,
    IWorkItemLinkRepository? linkRepo = null)
{
    /// <summary>
    /// Extends the working set around the given item.
    /// Additive only — never removes existing cached items.
    /// </summary>
    public async Task ExtendWorkingSetAsync(int itemId, CancellationToken ct = default)
    {
        try
        {
            await HydrateParentChainAsync(itemId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        try
        {
            await HydrateChildrenAsync(itemId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        if (linkRepo is not null)
        {
            try
            {
                await syncCoordinator.SyncLinksAsync(itemId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Iteratively walks the parent chain from <paramref name="itemId"/> up to root.
    /// Parents already in cache are read locally; missing parents are fetched from ADO.
    /// </summary>
    private async Task HydrateParentChainAsync(int itemId, CancellationToken ct)
    {
        var currentId = itemId;
        var visited = new HashSet<int>();

        while (visited.Add(currentId))
        {
            var item = await workItemRepo.GetByIdAsync(currentId, ct);
            if (item is null)
            {
                var fetched = await adoService.FetchAsync(currentId, ct);
                await protectedCacheWriter.SaveProtectedAsync(fetched, ct);
                item = fetched;
            }

            if (item.ParentId is not (> 0 and var parentId))
                break;

            currentId = parentId;
        }
    }

    /// <summary>
    /// Fetches children to 2 levels using <see cref="SyncCoordinator.SyncChildrenAsync"/>.
    /// Level-2 children are fetched in parallel.
    /// </summary>
    private async Task HydrateChildrenAsync(int itemId, CancellationToken ct)
    {
        await syncCoordinator.SyncChildrenAsync(itemId, ct);

        var children = await workItemRepo.GetChildrenAsync(itemId, ct);
        if (children.Count > 0)
        {
            await Task.WhenAll(children.Select(c => syncCoordinator.SyncChildrenAsync(c.Id, ct)));
        }
    }
}
