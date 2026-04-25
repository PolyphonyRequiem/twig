using Twig.Domain.Common;
using Twig.Domain.Enums;

namespace Twig.Domain.ValueObjects;

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
    /// Resolves a user-supplied state input to a full state name using unambiguous prefix matching.
    /// Exact match (case-insensitive) wins; otherwise the input must be an unambiguous prefix of exactly one state.
    /// </summary>
    public static Result<string> ResolveByName(string input, IReadOnlyList<StateEntry> states)
    {
        // Exact match first
        for (var i = 0; i < states.Count; i++)
        {
            if (string.Equals(states[i].Name, input, StringComparison.OrdinalIgnoreCase))
                return Result.Ok(states[i].Name);
        }

        // Prefix match
        var matches = new List<string>();
        for (var i = 0; i < states.Count; i++)
        {
            if (states[i].Name.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                matches.Add(states[i].Name);
        }

        if (matches.Count == 1)
            return Result.Ok(matches[0]);

        if (matches.Count > 1)
        {
            var options = string.Join(", ", matches);
            return Result.Fail<string>(
                $"Ambiguous state '{input}'. Matches: {options}");
        }

        var validStates = string.Join(", ", states.Select(s => s.Name));
        return Result.Fail<string>(
            $"Unknown state '{input}'. Valid states: {validStates}");
    }
}
