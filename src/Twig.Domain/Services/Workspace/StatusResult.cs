using Twig.Domain.Aggregates;
using Twig.Domain.Common;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Discriminated union representing the outcome of a status query.
/// Makes invalid states unrepresentable via exhaustive subtypes.
/// </summary>
public abstract record StatusResult
{
    private StatusResult() { }

    /// <summary>No active work item context is configured.</summary>
    public sealed record NoContext : StatusResult;

    /// <summary>An active item is set but the work item could not be retrieved.</summary>
    public sealed record Unreachable(
        int ActiveId,
        int UnreachableId,
        string Reason) : StatusResult;

    /// <summary>Active item was resolved successfully with its associated data.</summary>
    public sealed record Success(
        WorkItem Item,
        IReadOnlyList<PendingChangeRecord> PendingChanges,
        IReadOnlyList<WorkItem> Seeds) : StatusResult;
}
