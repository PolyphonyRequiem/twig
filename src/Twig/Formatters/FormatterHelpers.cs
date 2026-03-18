namespace Twig.Formatters;

/// <summary>
/// Shared formatting utilities used by multiple <see cref="IOutputFormatter"/> implementations.
/// </summary>
internal static class FormatterHelpers
{
    /// <summary>
    /// Returns a compact display label for a work item state.
    /// Uses the full state name — no shorthand encoding.
    /// </summary>
    internal static string GetStateLabel(string state)
    {
        if (string.IsNullOrEmpty(state))
            return "?";

        return state;
    }
}
