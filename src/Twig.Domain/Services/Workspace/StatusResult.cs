using Twig.Domain.Aggregates;
using Twig.Domain.Common;

namespace Twig.Domain.Services.Workspace;

/// <summary>No active work item context is configured.</summary>
public sealed record StatusNoContext;

/// <summary>An active item is set but the work item could not be retrieved.</summary>
public sealed record StatusUnreachable(
    int ActiveId,
    int UnreachableId,
    string Reason);

/// <summary>Active item was resolved successfully with its associated data.</summary>
public sealed record StatusSuccess(
    WorkItem Item,
    IReadOnlyList<PendingChangeRecord> PendingChanges,
    IReadOnlyList<WorkItem> Seeds);

/// <summary>
/// Discriminated union representing the outcome of a status query.
/// Makes invalid states unrepresentable via exhaustive subtypes.
/// </summary>
public union StatusResult(StatusNoContext, StatusUnreachable, StatusSuccess);
