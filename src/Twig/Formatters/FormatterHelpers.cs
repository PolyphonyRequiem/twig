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

    /// <summary>
    /// Builds a text-based progress bar: <c>[████░░░░░░] 4/10</c>.
    /// Returns empty string when <paramref name="total"/> is 0.
    /// When <paramref name="done"/> ≥ <paramref name="total"/>, the bar is all-filled and wrapped in green ANSI
    /// (unless <paramref name="useAnsi"/> is <c>false</c>, which returns the plain bar for Spectre markup paths).
    /// </summary>
    internal static string BuildProgressBar(int done, int total, int width = 20, bool useAnsi = true)
    {
        if (total <= 0)
            return "";

        // Cap width to a reasonable maximum to avoid overflow on wide terminals
        width = Math.Clamp(width, 1, 50);

        if (done < 0) done = 0;
        var complete = done >= total;
        if (done > total) done = total;

        var filled = (int)Math.Round((double)done / total * width);
        // Ensure filled doesn't exceed width
        filled = Math.Min(filled, width);
        var empty = width - filled;

        var bar = $"[{new string('█', filled)}{new string('░', empty)}] {done}/{total}";

        // Green when complete (ANSI path only — Spectre callers use useAnsi: false and apply markup separately)
        if (complete && useAnsi)
            return $"\x1b[32m{bar}\x1b[0m";

        return bar;
    }

    /// <summary>Returns <c>true</c> when <paramref name="done"/> ≥ <paramref name="total"/> and total &gt; 0.</summary>
    internal static bool IsProgressComplete(int done, int total)
        => total > 0 && done >= total;

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
            return trimmed;
        return trimmed[..(maxLength - 1)] + "…";
    }
}
