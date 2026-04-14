using Twig.Domain.Common;

namespace Twig.Domain.Services;

/// <summary>
/// Builds a concise one-line summary of pending changes for a dirty work item.
/// Returns <c>null</c> when there are no changes.
/// </summary>
/// <remarks>
/// <para>Output examples:</para>
/// <list type="bullet">
///   <item><c>null</c> — no changes</item>
///   <item><c>"local: Title changed"</c> — single field change</item>
///   <item><c>"local: Title changed, State → Doing"</c> — field + state change</item>
///   <item><c>"local: 3 field changes, 1 note"</c> — many changes (aggregated)</item>
/// </list>
/// </remarks>
public static class DirtyStateSummary
{
    private const int DetailThreshold = 2;

    /// <summary>
    /// Builds a concise summary string from the given pending changes.
    /// </summary>
    /// <param name="changes">The list of pending change records for a work item.</param>
    /// <returns>
    /// A one-line summary prefixed with <c>"local: "</c>, or <c>null</c> when
    /// <paramref name="changes"/> is empty.
    /// </returns>
    public static string? Build(IReadOnlyList<PendingChangeRecord> changes)
    {
        if (changes.Count == 0)
            return null;

        var fieldChanges = changes.Where(c => !IsNote(c.ChangeType)).ToList();
        int noteCount = changes.Count - fieldChanges.Count;

        var parts = new List<string>();

        if (fieldChanges.Count > 0 && fieldChanges.Count <= DetailThreshold && noteCount == 0)
        {
            // Detailed mode: show each field/state change individually
            foreach (var change in fieldChanges)
            {
                parts.Add(FormatDetailedChange(change));
            }
        }
        else
        {
            // Aggregated mode: show counts
            if (fieldChanges.Count > 0)
            {
                var label = fieldChanges.Count == 1 ? "field change" : "field changes";
                parts.Add($"{fieldChanges.Count} {label}");
            }

            if (noteCount > 0)
            {
                var label = noteCount == 1 ? "note" : "notes";
                parts.Add($"{noteCount} {label}");
            }
        }

        return $"local: {string.Join(", ", parts)}";
    }

    private static string FormatDetailedChange(PendingChangeRecord change)
    {
        if (IsState(change.ChangeType) && change.NewValue is not null)
        {
            return change.OldValue is not null
                ? $"State {change.OldValue} → {change.NewValue}"
                : $"State → {change.NewValue}";
        }

        var name = SimplifyFieldName(change.FieldName);
        return $"{name} changed";
    }

    /// <summary>
    /// Extracts the short name from a fully-qualified field reference name.
    /// <c>"System.Title"</c> becomes <c>"Title"</c>.
    /// </summary>
    private static string SimplifyFieldName(string? fieldName)
    {
        if (string.IsNullOrEmpty(fieldName))
            return "Field";

        var lastDot = fieldName.LastIndexOf('.');
        return lastDot >= 0 ? fieldName[(lastDot + 1)..] : fieldName;
    }

    private static bool IsNote(string changeType) =>
        string.Equals(changeType, "note", StringComparison.OrdinalIgnoreCase);

    private static bool IsState(string changeType) =>
        string.Equals(changeType, "state", StringComparison.OrdinalIgnoreCase);
}
