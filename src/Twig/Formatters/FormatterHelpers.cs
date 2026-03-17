using Twig.Domain.Enums;
using Twig.Domain.Services;

namespace Twig.Formatters;

/// <summary>
/// Shared formatting utilities used by multiple <see cref="IOutputFormatter"/> implementations.
/// </summary>
internal static class FormatterHelpers
{
    /// <summary>
    /// Maps a work item state string to a single-character shorthand code.
    /// </summary>
    internal static string GetShorthand(string state)
    {
        if (string.IsNullOrEmpty(state))
            return "?";

        return StateCategoryResolver.Resolve(state, null) switch
        {
            StateCategory.Proposed => "p",
            StateCategory.InProgress => "c",
            StateCategory.Resolved => "s",
            StateCategory.Completed => "d",
            StateCategory.Removed => "x",
            _ => state[..1].ToLowerInvariant(),
        };
    }
}
