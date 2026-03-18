namespace Twig.Domain.Services;

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
}
