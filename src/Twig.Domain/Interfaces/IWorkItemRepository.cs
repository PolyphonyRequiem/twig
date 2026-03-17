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
    Task<IReadOnlyList<WorkItem>> GetChildrenAsync(int parentId, CancellationToken ct = default);
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
}
