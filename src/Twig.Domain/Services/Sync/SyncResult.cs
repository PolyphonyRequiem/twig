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
/// Discriminated union representing the outcome of a sync operation.
/// Commands pattern-match on this to decide display behavior.
/// </summary>
public union SyncResult(UpToDate, Updated, SyncFailed, Skipped, PartiallyUpdated);
