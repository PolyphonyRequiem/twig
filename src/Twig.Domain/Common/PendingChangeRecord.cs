namespace Twig.Domain.Common;

/// <summary>
/// Represents a recorded pending change for a work item.
/// </summary>
public sealed record PendingChangeRecord(
    int WorkItemId,
    string ChangeType,
    string? FieldName,
    string? OldValue,
    string? NewValue);
