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
    Task<int> CreateAsync(CreateWorkItemRequest request, CancellationToken ct = default);

    /// <summary>
    /// Asks ADO whether a create stamped with <paramref name="idempotencyTag"/> already landed,
    /// returning its id or <see langword="null"/> (wayfinder 0015, from 0001 §4).
    /// </summary>
    /// <remarks>
    /// This is the recovery half of the intent record. ADO documents no idempotency key for
    /// creates, so an ambiguous outcome — a timeout, a 429 mid-flight, a dropped connection —
    /// cannot be distinguished from a failure, and a blind retry duplicates the work item
    /// (PolyphonyRequiem/twig#270). Querying for the stamped tag is the documented-safe way to
    /// settle the question before retrying.
    /// </remarks>
    Task<int?> FindByIdempotencyTagAsync(string idempotencyTag, CancellationToken ct = default);
    Task AddCommentAsync(int id, string text, CancellationToken ct = default);
    Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, CancellationToken ct = default);
    Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, int top, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, CancellationToken ct = default);
    Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default);
    Task RemoveLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default);

    /// <summary>
    /// Adds an artifact link (ArtifactLink for vstfs:// URIs, Hyperlink for http/https URLs)
    /// to the specified work item. Fetches the current revision internally for optimistic concurrency.
    /// Returns <c>true</c> if the link already existed (HTTP 409), <c>false</c> if newly created.
    /// </summary>
    Task<bool> AddArtifactLinkAsync(int workItemId, string url, string? name = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes a work item by ID (sends it to the recycle bin).
    /// Implementations should treat HTTP 404 as idempotent (item already deleted).
    /// </summary>
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Fetches the complete revision history for a work item from the ADO Work Item Updates API
    /// (twig#241). Read-only: no workspace, cache, context, or pending-change mutation.
    /// </summary>
    /// <remarks>
    /// Complete-or-error: the implementation traverses every ADO page internally and a failure on
    /// any page fails the whole operation. A partial timeline is never reported as success.
    /// Relation-target enrichment is best-effort and must never affect
    /// <see cref="WorkItemHistory.Complete"/>.
    /// </remarks>
    Task<WorkItemHistory> FetchHistoryAsync(
        int id,
        WorkItemHistoryOptions options,
        CancellationToken ct = default);
}
