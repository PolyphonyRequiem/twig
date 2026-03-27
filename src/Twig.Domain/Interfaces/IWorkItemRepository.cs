using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Repository contract for persisting and querying work items from the local cache.
/// Implemented in Infrastructure (SQLite).
/// </summary>
public interface IWorkItemRepository
{
    Task<WorkItem?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetChildrenAsync(int parentId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetRootItemsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetByIterationAsync(IterationPath iterationPath, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetByIterationAndAssigneeAsync(IterationPath iterationPath, string assignee, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetParentChainAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> FindByPatternAsync(string pattern, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetDirtyItemsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> GetSeedsAsync(CancellationToken ct = default);
    Task<bool> ExistsByIdAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<int>> GetOrphanParentIdsAsync(CancellationToken ct = default);
    Task SaveAsync(WorkItem workItem, CancellationToken ct = default);
    Task SaveBatchAsync(IEnumerable<WorkItem> workItems, CancellationToken ct = default);

    /// <summary>
    /// Deletes all cached work items whose IDs are NOT in <paramref name="keepIds"/>.
    /// Used by working-set eviction to bound cache size after context switches.
    /// </summary>
    Task EvictExceptAsync(IReadOnlySet<int> keepIds, CancellationToken ct = default);

    /// <summary>
    /// Deletes a single work item by its ID.
    /// </summary>
    Task DeleteByIdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Updates all work items whose parent_id equals <paramref name="oldParentId"/>
    /// to reference <paramref name="newParentId"/> instead. Used during seed publish.
    /// </summary>
    Task RemapParentIdAsync(int oldParentId, int newParentId, CancellationToken ct = default);

    /// <summary>
    /// Returns the smallest seed ID in the cache, or null if no seeds exist.
    /// Used to initialize <see cref="WorkItem.InitializeSeedCounter"/> on startup.
    /// </summary>
    Task<int?> GetMinSeedIdAsync(CancellationToken ct = default);
}
