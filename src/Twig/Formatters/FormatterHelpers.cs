using System.Text;
using Spectre.Console;
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
        => string.IsNullOrEmpty(state) ? "?" : state;

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

    private const int MaxDescriptionLines = 30;

    /// <summary>
    /// Converts an HTML string (typically from ADO description fields) to readable plain text.
    /// Preserves block-level structure (paragraphs, headings, list items), decodes common HTML
    /// entities, collapses blank lines, and truncates to <see cref="MaxDescriptionLines"/> lines.
    /// AOT-safe: uses <see cref="StringBuilder"/> only — no regex, no reflection.
    /// </summary>
    internal static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        // Pass 1: Insert structural markers before block-level opening tags
        var marked = InsertBlockMarkers(html);

        // Pass 2: Strip remaining HTML tags
        var stripped = StripAllTags(marked);

        // Pass 3: Decode named HTML entities
        var decoded = DecodeHtmlEntities(stripped);

        // Pass 4: Collapse blank lines, trim each line, trim result
        var lines = NormalizeLines(decoded);

        // Pass 5: Truncate at MaxDescriptionLines
        return TruncateLines(lines);
    }

    /// <summary>
    /// Converts an HTML string (typically from ADO description fields) to Spectre.Console markup.
    /// The returned string already contains Spectre markup tags (e.g. <c>[bold]</c>, <c>[italic]</c>)
    /// and user text has been escaped via <see cref="Markup.Escape"/>.
    /// Callers should wrap the result in <c>new Markup(result)</c> and must <b>not</b> call
    /// <see cref="Markup.Escape"/> on the returned value.
    /// AOT-safe: single-pass state machine using <see cref="StringBuilder"/> — no regex, no reflection.
    /// </summary>
    internal static string HtmlToSpectreMarkup(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var result = new StringBuilder(html.Length);
        var textBuffer = new StringBuilder();
        var i = 0;

        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                var tagEnd = html.IndexOf('>', i);
                if (tagEnd < 0)
                {
                    // Unclosed '<' — treat remaining as literal text
                    textBuffer.Append(html, i, html.Length - i);
                    break;
                }

                // Flush accumulated text through Markup.Escape
                FlushTextBuffer(textBuffer, result);

                var tagContent = html.AsSpan(i + 1, tagEnd - i - 1);
                var isClosing = tagContent.Length > 0 && tagContent[0] == '/';
                var tagName = isClosing ? tagContent[1..] : tagContent;

                // Strip attributes at first space
                var spaceIdx = tagName.IndexOf(' ');
                if (spaceIdx >= 0)
                    tagName = tagName[..spaceIdx];

                EmitSpectreTag(result, tagName, isClosing);
                i = tagEnd + 1;
            }
            else if (html[i] == '&')
            {
                if (TryDecodeEntity(html, i, out var decoded, out var consumed))
                {
                    textBuffer.Append(decoded);
                    i += consumed;
                }
                else
                {
                    textBuffer.Append('&');
                    i++;
                }
            }
            else
            {
                textBuffer.Append(html[i]);
                i++;
            }
        }

        // Flush remaining text
        FlushTextBuffer(textBuffer, result);

        var lines = NormalizeLines(result.ToString());
        return TruncateLines(lines);
    }

    private static void FlushTextBuffer(StringBuilder textBuffer, StringBuilder result)
    {
        if (textBuffer.Length > 0)
        {
            result.Append(Markup.Escape(textBuffer.ToString()));
            textBuffer.Clear();
        }
    }

    private static void EmitSpectreTag(StringBuilder result, ReadOnlySpan<char> tagName, bool isClosing)
    {
        if (tagName.Equals("b", StringComparison.OrdinalIgnoreCase)
            || tagName.Equals("strong", StringComparison.OrdinalIgnoreCase))
        {
            result.Append(isClosing ? "[/]" : "[bold]");
        }
        else if (tagName.Equals("em", StringComparison.OrdinalIgnoreCase)
                 || tagName.Equals("i", StringComparison.OrdinalIgnoreCase))
        {
            result.Append(isClosing ? "[/]" : "[italic]");
        }
        else if (tagName.Equals("code", StringComparison.OrdinalIgnoreCase))
        {
            result.Append(isClosing ? "[/]" : "[dim]");
        }
        else if (tagName.Length == 2 && (tagName[0] is 'h' or 'H') && tagName[1] is >= '1' and <= '6')
        {
            result.Append(isClosing ? "[/]" : "\n[bold]");
        }
        else if (tagName.Equals("li", StringComparison.OrdinalIgnoreCase))
        {
            if (!isClosing)
                result.Append("\n• ");
        }
        else if (IsBreakElement(tagName)
                 || tagName.Equals("p", StringComparison.OrdinalIgnoreCase)
                 || tagName.Equals("div", StringComparison.OrdinalIgnoreCase)
                 || tagName.Equals("pre", StringComparison.OrdinalIgnoreCase))
        {
            if (!isClosing)
                result.Append('\n');
        }
        // else: unknown tag — strip silently
    }

    private static string InsertBlockMarkers(string html)
    {
        var sb = new StringBuilder(html.Length + 64);
        var i = 0;

        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                var tagEnd = html.IndexOf('>', i);
                if (tagEnd < 0)
                {
                    sb.Append(html, i, html.Length - i);
                    break;
                }

                var tagContent = html.AsSpan(i + 1, tagEnd - i - 1);
                // Trim leading '/' for closing tags — we only care about opening tags
                var isClosing = tagContent.Length > 0 && tagContent[0] == '/';
                var tagName = isClosing ? tagContent[1..] : tagContent;

                // Strip attributes: take up to first space
                var spaceIdx = tagName.IndexOf(' ');
                if (spaceIdx >= 0)
                    tagName = tagName[..spaceIdx];

                if (!isClosing && IsBlockElement(tagName))
                    sb.Append(tagName.Equals("li", StringComparison.OrdinalIgnoreCase) ? "\n• " : "\n");
                else if (!isClosing && IsBreakElement(tagName))
                    sb.Append('\n');

                // Append the original tag (will be stripped in pass 2)
                sb.Append(html, i, tagEnd - i + 1);
                i = tagEnd + 1;
            }
            else
            {
                sb.Append(html[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    private static bool IsBlockElement(ReadOnlySpan<char> tag)
        => tag.Equals("p", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("li", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("div", StringComparison.OrdinalIgnoreCase)
        || (tag.Length == 2 && (tag[0] is 'h' or 'H') && tag[1] is >= '1' and <= '6');

    private static bool IsBreakElement(ReadOnlySpan<char> tag)
    {
        if (!tag.StartsWith("br", StringComparison.OrdinalIgnoreCase)) return false;
        for (var j = 2; j < tag.Length; j++)
            if (tag[j] != '/' && tag[j] != ' ') return false;
        return true;
    }

    private static string StripAllTags(string input)
    {
        var result = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            if (input[i] != '<') { result.Append(input[i++]); continue; }
            var end = input.IndexOf('>', i);
            if (end < 0) { result.Append(input, i, input.Length - i); break; }
            i = end + 1;
        }
        return result.ToString();
    }

    private static string DecodeHtmlEntities(string input)
    {
        var sb = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            if (input[i] == '&')
            {
                if (TryDecodeEntity(input, i, out var decoded, out var consumed))
                {
                    sb.Append(decoded);
                    i += consumed;
                    continue;
                }
            }
            sb.Append(input[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool TryDecodeEntity(string input, int start, out char decoded, out int consumed)
    {
        decoded = default;
        consumed = 0;

        var semi = input.IndexOf(';', start + 1);
        if (semi < 0 || semi - start > 10) return false;

        var entity = input.AsSpan(start, semi - start + 1);
        if (entity.Equals("&amp;", StringComparison.OrdinalIgnoreCase))
        { decoded = '&'; consumed = entity.Length; return true; }
        if (entity.Equals("&lt;", StringComparison.OrdinalIgnoreCase))
        { decoded = '<'; consumed = entity.Length; return true; }
        if (entity.Equals("&gt;", StringComparison.OrdinalIgnoreCase))
        { decoded = '>'; consumed = entity.Length; return true; }
        if (entity.Equals("&quot;", StringComparison.OrdinalIgnoreCase))
        { decoded = '"'; consumed = entity.Length; return true; }
        if (entity.Equals("&nbsp;", StringComparison.OrdinalIgnoreCase))
        { decoded = ' '; consumed = entity.Length; return true; }

        return false;
    }

    private static List<string> NormalizeLines(string text)
    {
        var rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var result = new List<string>(rawLines.Length);
        var lastWasBlank = false;
        foreach (var raw in rawLines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (!lastWasBlank)
                    result.Add(line);
                lastWasBlank = true;
            }
            else
            {
                result.Add(line);
                lastWasBlank = false;
            }
        }

        while (result.Count > 0 && result[0].Length == 0)
            result.RemoveAt(0);
        while (result.Count > 0 && result[^1].Length == 0)
            result.RemoveAt(result.Count - 1);

        return result;
    }

    private static string TruncateLines(List<string> lines)
    {
        if (lines.Count <= MaxDescriptionLines)
            return string.Join('\n', lines);

        var remaining = lines.Count - MaxDescriptionLines;
        var truncated = lines.GetRange(0, MaxDescriptionLines);
        truncated.Add($"(+{remaining} more lines)");
        return string.Join('\n', truncated);
    }

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
            return trimmed;
        return trimmed[..(maxLength - 1)] + "…";
    }
}
