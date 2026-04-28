using Twig.Domain.Aggregates;
using Twig.Domain.Common;

namespace Twig.Domain.Services.Workspace;

/// <summary>Result of a status snapshot operation.</summary>
public sealed class StatusSnapshot
{
    public bool HasContext { get; init; }
    public int ActiveId { get; init; }
    public WorkItem? Item { get; init; }
    public IReadOnlyList<PendingChangeRecord> PendingChanges { get; init; } = [];
    public IReadOnlyList<WorkItem> Seeds { get; init; } = [];

    // Error state
    public int? UnreachableId { get; init; }
    public string? UnreachableReason { get; init; }

    public bool IsSuccess => HasContext && Item is not null;

    public static StatusSnapshot NoContext() => new() { HasContext = false };

    public static StatusSnapshot Unreachable(int activeId, int? errorId, string? reason) => new()
    {
        HasContext = true,
        ActiveId = activeId,
        UnreachableId = errorId ?? activeId,
        UnreachableReason = reason,
    };
}
