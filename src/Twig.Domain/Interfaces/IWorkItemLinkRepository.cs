using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Repository contract for persisting and querying non-hierarchy work item links.
/// Implemented in Infrastructure (SQLite).
/// </summary>
public interface IWorkItemLinkRepository
{
    Task<IReadOnlyList<WorkItemLink>> GetLinksAsync(int workItemId, CancellationToken ct = default);

    /// <summary>
    /// Returns every edge sourced from any id in <paramref name="workItemIds"/>, in one query
    /// (ADO #154). A link is an edge between two items, so it belongs to the SET rather than to
    /// a member of it, and a set-reading consumer must be able to ask for the whole set's edges
    /// without issuing one call per id.
    /// </summary>
    /// <remarks>
    /// Served by a single <c>WHERE source_id IN (…)</c> against the existing
    /// <c>idx_work_item_links_source</c> index. Duplicate ids in the input do not duplicate
    /// rows in the output. An empty input returns an empty list without touching the store.
    /// </remarks>
    Task<IReadOnlyList<WorkItemLink>> GetLinksForSetAsync(IReadOnlyList<int> workItemIds, CancellationToken ct = default);

    Task SaveLinksAsync(int workItemId, IReadOnlyList<WorkItemLink> links, CancellationToken ct = default);
}
