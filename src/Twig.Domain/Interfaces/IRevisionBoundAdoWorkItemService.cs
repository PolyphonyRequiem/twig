namespace Twig.Domain.Interfaces;

/// <summary>
/// Strict optimistic-concurrency primitives used by declarative plan apply. Segregated from
/// <see cref="IAdoWorkItemService"/> so consumers that never do revision-bound writes — TUI
/// reads, seed discovery, history lookups — do not depend on the whole PATCH surface, and so
/// this contract can evolve without touching the everyday work-item service.
/// </summary>
/// <remarks>
/// <para>
/// Every operation here sends the caller's expected revision as the <c>If-Match</c> header and
/// performs no internal refetch, retry, or rebase. A 412 server response surfaces as
/// <c>AdoConflictException</c>. The plan lifecycle is the intended consumer: it planned
/// against a specific revision and any drift MUST fail rather than silently succeed against a
/// newer state.
/// </para>
/// <para>
/// Implementations MAY be the same concrete type that implements
/// <see cref="IAdoWorkItemService"/>; this interface is a segregation contract, not an
/// alternative wire path.
/// </para>
/// </remarks>
public interface IRevisionBoundAdoWorkItemService
{
    /// <summary>
    /// Adds a relation link on <paramref name="sourceId"/> using strict optimistic concurrency
    /// against <paramref name="expectedRevision"/> — the exact revision is sent as the
    /// <c>If-Match</c> header and no internal refetch, retry, or rebase is performed. A 412
    /// server response surfaces as <c>AdoConflictException</c>. Returns the new work-item
    /// revision after the PATCH.
    /// </summary>
    Task<int> AddLinkAtRevisionAsync(
        int sourceId,
        string relationType,
        int targetId,
        int expectedRevision,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a relation link on <paramref name="sourceId"/> using strict optimistic
    /// concurrency against <paramref name="expectedRevision"/>. The implementation MAY fetch
    /// the current relation list solely to locate the JSON Patch index of the target relation;
    /// if the fetched revision differs from <paramref name="expectedRevision"/> the operation
    /// MUST fail with <c>AdoConflictException</c> before issuing the PATCH, and the PATCH MUST
    /// still carry <paramref name="expectedRevision"/> as <c>If-Match</c>. Returns the new
    /// work-item revision after the PATCH.
    /// </summary>
    /// <remarks>
    /// A missing relation at the expected revision is a semantic error, not a silent no-op:
    /// the caller's planned state is invalid and the operation MUST throw.
    /// </remarks>
    Task<int> RemoveLinkAtRevisionAsync(
        int sourceId,
        string relationType,
        int targetId,
        int expectedRevision,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a work item using strict optimistic concurrency against
    /// <paramref name="expectedRevision"/> — sent as the <c>If-Match</c> header with no
    /// internal refetch or retry. HTTP 404 is treated as idempotent success (already deleted).
    /// A 412 response surfaces as <c>AdoConflictException</c>.
    /// </summary>
    Task DeleteAtRevisionAsync(int id, int expectedRevision, CancellationToken ct = default);
}
