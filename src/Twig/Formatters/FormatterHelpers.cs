using Twig.Domain.Aggregates;

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

    /// <summary>
    /// Formats a dynamic field value for human-readable output based on its data type.
    /// </summary>
    internal static string FormatFieldValue(string? value, string dataType, int maxWidth = 20)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return dataType.ToLowerInvariant() switch
        {
            "html" => Truncate(Rendering.SpectreRenderer.StripHtmlTags(value), maxWidth),
            "treepath" => GetLastTreePathSegment(value),
            "datetime" => FormatRelativeDate(value),
            _ => Truncate(value, maxWidth),
        };
    }

    /// <summary>
    /// Formats a dynamic field value for JSON output (no truncation, ISO 8601 dates).
    /// </summary>
    internal static string? FormatFieldValueForJson(string? value, string dataType)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return dataType.ToLowerInvariant() switch
        {
            "html" => Rendering.SpectreRenderer.StripHtmlTags(value),
            _ => value,
        };
    }

    private static string GetLastTreePathSegment(string path)
    {
        var lastSlash = path.LastIndexOf('\\');
        return lastSlash >= 0 && lastSlash < path.Length - 1
            ? path[(lastSlash + 1)..]
            : path;
    }

    private static string FormatRelativeDate(string dateStr)
    {
        if (!DateTimeOffset.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var date))
            return Truncate(dateStr, 20);

        var elapsed = DateTimeOffset.UtcNow - date;
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 30) return $"{(int)elapsed.TotalDays}d ago";
        return $"{(int)(elapsed.TotalDays / 30)}mo ago";
    }

    /// <summary>
    /// Returns a formatted effort/points display string for a work item, or null if no effort field is found.
    /// Detects process-agnostic effort fields by suffix: StoryPoints (Agile), Effort (Scrum), Size (CMMI).
    /// </summary>
    internal static string? GetEffortDisplay(WorkItem item)
    {
        foreach (var key in item.Fields.Keys)
        {
            if (key.EndsWith("StoryPoints", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith("Effort", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith("Size", StringComparison.OrdinalIgnoreCase))
            {
                if (item.Fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    return $"({value} pts)";
            }
        }

        return null;
    }

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
            return trimmed;
        return trimmed[..(maxLength - 1)] + "…";
    }
}
