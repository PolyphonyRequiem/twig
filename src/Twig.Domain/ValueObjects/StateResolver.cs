using Twig.Domain.Common;
using Twig.Domain.Enums;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Classifies how a state input was resolved, so callers can adapt their messaging.
/// </summary>
public enum ResolutionKind
{
    /// <summary>Input matched a state name exactly (case-insensitive).</summary>
    ExactState,

    /// <summary>Input matched an ADO state-category name (e.g. <c>InProgress</c>).</summary>
    Category,

    /// <summary>Input was an unambiguous prefix of a state name.</summary>
    PrefixState,
}

/// <summary>
/// Result of resolving a user-supplied state input: the canonical state name plus how
/// it was resolved.
/// </summary>
public readonly record struct StateResolution(string ResolvedName, ResolutionKind Kind);

/// <summary>
/// Resolves state names from <see cref="StateEntry"/> lists by <see cref="StateCategory"/>.
/// </summary>
public static class StateResolver
{
    /// <summary>
    /// Finds the first state matching the given category.
    /// </summary>
    public static Result<string> ResolveByCategory(StateCategory category, IReadOnlyList<StateEntry> states)
    {
        for (var i = 0; i < states.Count; i++)
        {
            if (states[i].Category == category)
                return Result.Ok(states[i].Name);
        }

        return Result.Fail<string>(
            $"No state with category '{category}' found for this work item type.");
    }

    /// <summary>
    /// Resolves a user-supplied state input to a full state name plus the kind of match used.
    /// Precedence (case-insensitive):
    /// <list type="number">
    ///   <item>Exact match against a state name (always wins; preserves backward compatibility
    ///         when a category name happens to also be a state name, e.g. <c>Resolved</c>).</item>
    ///   <item>Exact match against an ADO state-category name
    ///         (<c>Proposed</c>, <c>InProgress</c>, <c>Resolved</c>, <c>Completed</c>, <c>Removed</c>);
    ///         resolves to the first state with that category.</item>
    ///   <item>Unambiguous prefix match against a state name.</item>
    /// </list>
    /// </summary>
    public static Result<StateResolution> ResolveByName(string input, IReadOnlyList<StateEntry> states)
    {
        // 1. Exact state name (case-insensitive) — wins over category to preserve backward compat.
        for (var i = 0; i < states.Count; i++)
        {
            if (string.Equals(states[i].Name, input, StringComparison.OrdinalIgnoreCase))
                return Result.Ok(new StateResolution(states[i].Name, ResolutionKind.ExactState));
        }

        // 2. Category name (case-insensitive). Only the 5 known ADO categories are recognized;
        //    Unknown is never a valid input even if a state happens to be named "Unknown".
        var category = ParseCategoryName(input);
        if (category is not null)
        {
            var byCategory = ResolveByCategory(category.Value, states);
            if (byCategory.IsSuccess)
                return Result.Ok(new StateResolution(byCategory.Value, ResolutionKind.Category));
            // Category recognized but no matching state on this type — fall through to
            // prefix match (won't find anything) and then to "Unknown state" with the
            // type's valid states listed.
        }

        // 3. Unambiguous prefix match.
        var matches = new List<string>();
        for (var i = 0; i < states.Count; i++)
        {
            if (states[i].Name.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                matches.Add(states[i].Name);
        }

        if (matches.Count == 1)
            return Result.Ok(new StateResolution(matches[0], ResolutionKind.PrefixState));

        if (matches.Count > 1)
        {
            var options = string.Join(", ", matches);
            return Result.Fail<StateResolution>(
                $"Ambiguous state '{input}'. Matches: {options}");
        }

        var validStates = string.Join(", ", states.Select(s => s.Name));
        return Result.Fail<StateResolution>(
            $"Unknown state '{input}'. Valid states: {validStates}");
    }

    /// <summary>
    /// Parses a user-typed category input (e.g. "InProgress", "in progress", "in-progress",
    /// "INPROGRESS") into a <see cref="StateCategory"/>. Whitespace, hyphens, underscores,
    /// and casing are ignored. Returns <c>null</c> when the input does not match one of the
    /// five known ADO categories — callers fall through to other resolution tiers.
    /// </summary>
    private static StateCategory? ParseCategoryName(string input)
    {
        var compact = new string(input.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_').ToArray());
        return compact.ToLowerInvariant() switch
        {
            "proposed" => StateCategory.Proposed,
            "inprogress" => StateCategory.InProgress,
            "resolved" => StateCategory.Resolved,
            "completed" => StateCategory.Completed,
            "removed" => StateCategory.Removed,
            _ => null
        };
    }
}
