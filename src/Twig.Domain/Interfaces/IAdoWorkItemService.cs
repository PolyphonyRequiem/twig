using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Contract for interacting with the Azure DevOps REST API for work items.
/// Implemented in Infrastructure (ADO REST client).
/// </summary>
public interface IAdoWorkItemService
{
    Task<WorkItem> FetchAsync(int id, CancellationToken ct = default);
    Task<(WorkItem Item, IReadOnlyList<WorkItemLink> Links)> FetchWithLinksAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> FetchChildrenAsync(int parentId, CancellationToken ct = default);
    Task<int> PatchAsync(int id, IReadOnlyList<FieldChange> changes, int expectedRevision, CancellationToken ct = default);
    Task<int> CreateAsync(WorkItem seed, CancellationToken ct = default);
    Task AddCommentAsync(int id, string text, CancellationToken ct = default);
    Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, CancellationToken ct = default);
}
