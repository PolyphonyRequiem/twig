using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Resolves a work item state name to its <see cref="StateCategory"/>.
/// Prefers authoritative entries from ADO; falls back to hardcoded heuristics.
/// </summary>
public static class StateCategoryResolver
{
    /// <summary>
    /// Resolves the category for <paramref name="state"/> by searching <paramref name="entries"/>
    /// for a name match (case-insensitive). Falls back to <see cref="FallbackCategory"/> when
    /// no entries are provided or no match is found.
    /// </summary>
    public static StateCategory Resolve(string? state, IReadOnlyList<StateEntry>? entries)
    {
        if (entries is not null)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].Name, state, StringComparison.OrdinalIgnoreCase))
                    return entries[i].Category;
            }
        }

        return FallbackCategory(state);
    }

    /// <summary>
    /// Hardcoded fallback mapping from state name to category.
    /// Covers the union of all known ADO process template state names.
    /// </summary>
    internal static StateCategory FallbackCategory(string? state)
    {
        if (string.IsNullOrEmpty(state))
            return StateCategory.Unknown;

        return state.ToLowerInvariant() switch
        {
            "new" or "to do" or "proposed" => StateCategory.Proposed,
            "active" or "doing" or "committed" or "in progress" or "approved" => StateCategory.InProgress,
            "resolved" => StateCategory.Resolved,
            "closed" or "done" => StateCategory.Completed,
            "removed" => StateCategory.Removed,
            _ => StateCategory.Unknown
        };
    }

    /// <summary>
    /// Parses an ADO category string (e.g. "Proposed", "InProgress") into a <see cref="StateCategory"/>.
    /// Returns <see cref="StateCategory.Unknown"/> for null or unrecognized values.
    /// </summary>
    public static StateCategory ParseCategory(string? category)
    {
        return category switch
        {
            "Proposed" => StateCategory.Proposed,
            "InProgress" => StateCategory.InProgress,
            "Resolved" => StateCategory.Resolved,
            "Completed" => StateCategory.Completed,
            "Removed" => StateCategory.Removed,
            _ => StateCategory.Unknown
        };
    }
}
