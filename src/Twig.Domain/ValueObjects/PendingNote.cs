namespace Twig.Domain.ValueObjects;

/// <summary>
/// Represents an uncommitted comment/note to attach to a work item.
/// </summary>
public readonly record struct PendingNote(string Text, DateTimeOffset CreatedAt, bool IsHtml);
