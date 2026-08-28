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

    /// <summary>
    /// Returns when <paramref name="workItemId"/>'s edge set was last read from ADO and written
    /// to the cache, or <c>null</c> when it never has been (AB#831).
    /// </summary>
    /// <remarks>
    /// 🔴 This is the answer to a question <see cref="GetLinksAsync"/> cannot express. An empty
    /// result from that read means either "this item has no edges" or "nobody has ever fetched
    /// this item's edges", and the two are byte-identical — which is how two agent sessions
    /// concluded a work item had no blocking graph when its edges existed. Pair every cache-only
    /// edge read with this call: an empty list plus a non-null timestamp is a VERIFIED empty edge
    /// set; an empty list plus <c>null</c> is no answer at all.
    /// </remarks>
    Task<DateTimeOffset?> GetLinksVerifiedAtAsync(int workItemId, CancellationToken ct = default);

    /// <summary>
    /// The plural form of <see cref="GetLinksVerifiedAtAsync"/>, to pair with
    /// <see cref="GetLinksForSetAsync"/> in one query. Ids that have never had their edge set
    /// read are ABSENT from the returned map rather than present with a null value — a set read
    /// must not need one call per id to learn which of its members it can trust.
    /// </summary>
    Task<IReadOnlyDictionary<int, DateTimeOffset>> GetLinksVerifiedAtForSetAsync(IReadOnlyList<int> workItemIds, CancellationToken ct = default);

    /// <summary>
    /// Replaces <paramref name="workItemId"/>'s whole edge set and stamps it as verified as of
    /// now, so a later read can tell an empty edge set apart from an unfetched one (AB#831).
    /// Both writes land in one transaction: a stamp without its edges, or edges without their
    /// stamp, would each be a lie of exactly the kind this method exists to stop telling.
    /// </summary>
    Task SaveLinksAsync(int workItemId, IReadOnlyList<WorkItemLink> links, CancellationToken ct = default);

    /// <summary>
    /// Replaces the edge sets of MANY sources and stamps them all verified, in ONE transaction.
    /// Every key is written, including keys whose value is empty.
    /// </summary>
    /// <remarks>
    /// 🔴 This exists for latency, and the number is not small. The cache runs
    /// <c>journal_mode=WAL</c> with <c>synchronous</c> left at SQLite's default of <c>FULL</c>,
    /// so every commit fsyncs. Calling <see cref="SaveLinksAsync"/> once per item across a
    /// 163-item refresh measured <b>370-700 ms</b> on a real disk; the same writes in one
    /// transaction measured <b>~3 ms</b>. (A benchmark run under <c>/tmp</c> will not show this
    /// — tmpfs absorbs the fsync and reports single-digit milliseconds either way.)
    /// <para>
    /// One transaction for the whole set is also the better contract: a refresh's edge snapshot
    /// lands whole or not at all, rather than leaving a half-updated graph behind a failure.
    /// </para>
    /// </remarks>
    Task SaveLinksForSourcesAsync(
        IReadOnlyDictionary<int, IReadOnlyList<WorkItemLink>> linksBySource,
        CancellationToken ct = default);
}
