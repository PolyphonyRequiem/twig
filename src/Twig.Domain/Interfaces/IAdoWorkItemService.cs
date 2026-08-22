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

    /// <summary>
    /// Fetches a SET of work items together with the non-hierarchy edges among them (ADO #154).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>This costs no additional ADO requests over <see cref="FetchBatchAsync"/>.</b> The
    /// batch URL already carries <c>$expand=relations</c>, so the relations were always on the
    /// wire; the batch path simply mapped them away. This overload keeps them.
    /// </para>
    /// <para>
    /// Returned links carry every edge sourced from a fetched item, including edges whose target
    /// lies outside the requested set — a consumer discovering what to fetch next needs exactly
    /// those, and filtering them here would make that impossible.
    /// </para>
    /// </remarks>
    Task<(IReadOnlyList<WorkItem> Items, IReadOnlyList<WorkItemLink> Links)> FetchBatchWithLinksAsync(
        IReadOnlyList<int> ids,
        CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> FetchChildrenAsync(int parentId, CancellationToken ct = default);
    Task<int> PatchAsync(int id, IReadOnlyList<FieldChange> changes, int expectedRevision, CancellationToken ct = default);
    Task<int> CreateAsync(CreateWorkItemRequest request, CancellationToken ct = default);

    /// <summary>
    /// Asks ADO whether a create twig issued already landed, returning its id or
    /// <see langword="null"/> (wayfinder 0015, from 0001 §4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the recovery half of the intent record. ADO documents no idempotency key for
    /// creates, so an ambiguous outcome — a timeout, a 429 mid-flight, a dropped connection —
    /// cannot be distinguished from a failure, and a blind retry duplicates the work item
    /// (PolyphonyRequiem/twig#270).
    /// </para>
    /// <para>
    /// The match is a single constant tag (<see cref="ValueObjects.PublishIntent.IntentTag"/>)
    /// to narrow, then the intent's own title, type and <c>RecordedAt</c> to identify. A
    /// per-create unique tag would be a stronger key but mints one new project-wide tag per
    /// published item forever, which ADO's unique-tag cap and 0001 §1's "twig owns only the
    /// pending set" both rule out.
    /// </para>
    /// <para>
    /// Takes the <see cref="ValueObjects.PublishIntent"/> whole rather than three unpacked
    /// primitives: the caller is already holding one, and the three fields are only meaningful
    /// together — <c>RecordedAt</c> is a valid fence *because* it belongs to the same intent
    /// that produced the title and type.
    /// </para>
    /// </remarks>
    Task<int?> FindPublishedIntentAsync(
        ValueObjects.PublishIntent intent,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the in-flight publish tag from a work item once the publish is recorded locally,
    /// so the tag marks only what is actually in flight (wayfinder 0015).
    /// </summary>
    /// <remarks>
    /// Best-effort by contract — callers must treat failure as non-fatal. The publish has
    /// already succeeded by this point and a stale tag is cosmetic, so throwing here would turn
    /// a successful publish into a reported failure. Implementations must preserve other tags.
    /// </remarks>
    Task ClearIntentTagAsync(int id, CancellationToken ct = default);

    Task AddCommentAsync(int id, string text, CancellationToken ct = default);
    Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, CancellationToken ct = default);
    Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, int top, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, CancellationToken ct = default);
    Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default);

    /// <summary>
    /// Adds a link and records <paramref name="comment"/> in the relation's
    /// <c>attributes.comment</c> field (AB#620).
    /// </summary>
    /// <remarks>
    /// A distinctly NAMED method rather than an overload of <see cref="AddLinkAsync"/>, and the
    /// reason is mechanical rather than stylistic: <c>AddLinkAsync</c> is shipped public API
    /// whose last parameter is optional, and the public-API analyzers reject both ways of
    /// extending it — RS0026 forbids a second overload that also has optional parameters, and
    /// RS0027 forbids the shipped one keeping its optional parameter while a longer overload
    /// exists. Widening the shipped signature in place would instead delete a shipped symbol.
    ///
    /// <para>
    /// The comment is the reason for the relationship, which for a symmetric
    /// <c>System.LinkTypes.Related</c> edge is the valuable half — an unexplained related link
    /// is an assertion with no argument. ADO carries it on the relation itself, so it must be
    /// set at creation; a create-then-comment pair would write a work item COMMENT instead,
    /// which is a different thing in a different place.
    /// </para>
    ///
    /// <para>
    /// A null or whitespace <paramref name="comment"/> must emit no <c>attributes</c> object at
    /// all, so this sends byte-identical JSON to <see cref="AddLinkAsync"/> in that case. That
    /// is what lets callers route every add through here without changing existing requests.
    /// </para>
    /// </remarks>
    Task AddLinkWithCommentAsync(int sourceId, int targetId, string adoLinkType, string? comment, CancellationToken ct = default);

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
