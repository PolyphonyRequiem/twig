namespace Twig.Domain.Services.Sync;

/// <summary>
/// Describes a single item that failed to sync.
/// </summary>
public sealed record SyncItemFailure(int Id, string Error);

/// <summary>All items are already current — nothing to sync.</summary>
public sealed record UpToDate;

/// <summary>Items were synced successfully.</summary>
public sealed record Updated(int ChangedCount);

/// <summary>Sync failed entirely.</summary>
public sealed record SyncFailed(string Reason);

/// <summary>Sync was skipped (e.g., no context).</summary>
public sealed record Skipped(string Reason);

/// <summary>
/// Some items were saved successfully while others failed during fetch.
/// </summary>
public sealed record PartiallyUpdated(int SavedCount, IReadOnlyList<SyncItemFailure> Failures);

/// <summary>
/// The cache holds the item but it is older than the configured staleness window.
/// <para>
/// Per wayfinder 0004 §3, staleness is an <b>outcome</b>, not a policy: a read reports this and
/// returns the cached data, and each surface decides what it means. It never triggers a fetch.
/// The rich CLI renders a hint, the script CLI gets a network-free contract, MCP may treat this
/// exactly as it treats <see cref="NotCached"/> and reach on its own judgement.
/// </para>
/// </summary>
/// <param name="LastSyncedAt">
/// When the item was last synced. <c>null</c> when the cache never recorded a sync for it.
/// </param>
public sealed record Stale(DateTimeOffset? LastSyncedAt);

/// <summary>
/// The item is not present in the local cache at all. Like <see cref="Stale"/>, this is an
/// outcome the surface interprets — a read does not silently fetch to make it go away
/// (0003 §4's silent-coercion rule).
/// </summary>
public sealed record NotCached(int Id);

/// <summary>
/// Discriminated union representing the outcome of a sync or cache-read operation.
/// Commands pattern-match on this to decide display behavior.
/// </summary>
public union SyncResult(UpToDate, Updated, SyncFailed, Skipped, PartiallyUpdated, Stale, NotCached);
