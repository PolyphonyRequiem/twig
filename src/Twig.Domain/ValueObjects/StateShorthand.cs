using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Enums;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Maps single-character shorthand codes to full ADO state names.
/// Resolution uses <see cref="StateCategory"/> from <see cref="StateEntry"/> data,
/// matching the first state in the ordered list that has the target category.
/// </summary>
public static class StateShorthand
{
    private static readonly IReadOnlyDictionary<char, StateCategory> CategoryMap =
        new Dictionary<char, StateCategory>
        {
            ['p'] = StateCategory.Proposed,
            ['c'] = StateCategory.InProgress,
            ['s'] = StateCategory.Resolved,
            ['d'] = StateCategory.Completed,
            ['x'] = StateCategory.Removed,
        };

    /// <summary>
    /// Resolves a shorthand character code to the full state name using the given state entries.
    /// </summary>
    public static Result<string> Resolve(char code, IReadOnlyList<StateEntry> states)
    {
        if (!CategoryMap.TryGetValue(code, out var targetCategory))
            return Result.Fail<string>(
                $"Invalid shorthand code: '{code}'. Valid codes are: p, c, s, d, x.");

        for (var i = 0; i < states.Count; i++)
        {
            if (states[i].Category == targetCategory)
                return Result.Ok(states[i].Name);
        }

        return Result.Fail<string>(
            $"No state with category '{targetCategory}' found for this work item type.");
    }
}
