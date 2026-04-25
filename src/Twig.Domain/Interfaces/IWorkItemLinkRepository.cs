using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Repository contract for persisting and querying non-hierarchy work item links.
/// Implemented in Infrastructure (SQLite).
/// </summary>
public interface IWorkItemLinkRepository
{
    Task<IReadOnlyList<WorkItemLink>> GetLinksAsync(int workItemId, CancellationToken ct = default);
    Task SaveLinksAsync(int workItemId, IReadOnlyList<WorkItemLink> links, CancellationToken ct = default);
}
