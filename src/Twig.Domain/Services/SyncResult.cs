namespace Twig.Domain.Services;

/// <summary>
/// Describes a single item that failed to sync.
/// </summary>
public sealed record SyncItemFailure(int Id, string Error);

/// <summary>
/// Discriminated union representing the outcome of a sync operation.
/// Commands pattern-match on this to decide display behavior.
/// </summary>
public abstract record SyncResult
{
    private SyncResult() { }

    public sealed record UpToDate : SyncResult;
    public sealed record Updated(int ChangedCount) : SyncResult;
    public sealed record Failed(string Reason) : SyncResult;
    public sealed record Skipped(string Reason) : SyncResult;

    /// <summary>
    /// Some items were saved successfully while others failed during fetch.
    /// </summary>
    public sealed record PartiallyUpdated(int SavedCount, IReadOnlyList<SyncItemFailure> Failures) : SyncResult;
}
