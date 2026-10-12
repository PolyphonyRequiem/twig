using System.Globalization;
using System.Text;

namespace Twig.Browser;

/// <summary>
/// Viewport-only projection of native Show output. SGR is retained, terminal actions
/// are not. Graphemes remain indivisible; ambiguous/font-specific widths remain a
/// terminal qualification gate, as in the bounded Windows 1143 evidence slice.
/// </summary>
internal sealed record BrowserTextChunk(int Offset, string Text);

internal static class BrowserTerminalText
{
    internal static int CellWidth(string text)
    {
        var width = 0;
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '\x1b') { i = EscapeEnd(text, i); continue; }
            var element = StringInfo.GetNextTextElement(text, i);
            width += Width(element); i += element.Length;
        }
        return width;
    }

    internal static IReadOnlyList<BrowserTextChunk> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        var elements = new List<(int Offset, string Text, int Cells)>();
        for (var i = 0; i < text.Length;)
        {
            var element = StringInfo.GetNextTextElement(text, i);
            elements.Add((i, element, element == "\t" ? 1 : Width(element)));
            i += element.Length;
        }
        var rows = new List<BrowserTextChunk>();
        for (var start = 0; start < elements.Count;)
        {
            var end = start;
            var used = 0;
            var boundary = -1;
            while (end < elements.Count)
            {
                var element = elements[end];
                if (element.Text is "\n" or "\r\n") break;
                if (used + element.Cells > width && end > start) break;
                used += Math.Min(element.Cells, width);
                end++;
                if (string.IsNullOrWhiteSpace(element.Text)) boundary = end;
                if (used >= width) break;
            }
            // Word boundaries are preferred without trimming or losing any whitespace.
            if (end < elements.Count && elements[end].Text is not ("\n" or "\r\n") && boundary > start)
                end = boundary;
            var line = new StringBuilder();
            for (var j = start; j < end; j++)
                line.Append(elements[j].Text == "\t" ? " " : elements[j].Text);
            rows.Add(new(elements[start].Offset, line.ToString()));
            if (end < elements.Count && elements[end].Text is "\n" or "\r\n") end++;
            start = Math.Max(start + 1, end);
        }
        if (elements.Count == 0 || text.EndsWith('\n')) rows.Add(new(text.Length, string.Empty));
        return rows;
    }

    internal static IReadOnlyList<string> Rows(string text, int width)
    {
        width = Math.Max(1, width);
        var rows = new List<string>();
        var row = new StringBuilder();
        var style = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '\x1b')
            {
                var end = EscapeEnd(text, i);
                var escape = text.AsSpan(i, end - i);
                if (IsSgr(escape))
                {
                    row.Append(escape);
                    if (escape.SequenceEqual("\x1b[0m") || escape.SequenceEqual("\x1b[m")) style.Clear();
                    else style.Append(escape);
                }
                i = end;
            }
            else if (text[i] == '\n')
            {
                AddRows(rows, row.ToString(), width);
                row.Clear().Append(style);
                i++;
            }
            else
            {
                if (!char.IsControl(text[i]) || text[i] == '\t') row.Append(text[i]);
                i++;
            }
        }
        if (row.Length > 0) AddRows(rows, row.ToString(), width);
        return rows;
    }

    private static void AddRows(List<string> rows, string text, int width)
    {
        // Spectre's logical glyph widths can under-budget native Windows cells.
        // Reflow before frame clipping, preserving both content and active SGR.
        if (!OperatingSystem.IsWindows() || CellWidth(text) <= width)
        {
            rows.Add(text);
            return;
        }
        var visible = 0;
        while (visible < text.Length && text[visible] == '\x1b') visible = EscapeEnd(text, visible);
        var prefix = text.AsSpan(visible).StartsWith("│ ", StringComparison.Ordinal) ? "│ " : "";
        var row = new StringBuilder();
        var style = new StringBuilder();
        var cells = 0;
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '\x1b')
            {
                var end = EscapeEnd(text, i);
                var escape = text.AsSpan(i, end - i);
                row.Append(escape);
                if (escape.SequenceEqual("\x1b[0m") || escape.SequenceEqual("\x1b[m")) style.Clear();
                else style.Append(escape);
                i = end;
                continue;
            }
            var element = StringInfo.GetNextTextElement(text, i);
            var next = Width(element);
            if (cells + next > width && cells > prefix.Length)
            {
                rows.Add(row.ToString());
                row.Clear().Append(style).Append(prefix);
                cells = prefix.Length;
            }
            row.Append(element);
            cells += next;
            i += element.Length;
        }
        rows.Add(row.ToString());
    }

    internal static string Clip(string text, int width)
    {
        var result = new StringBuilder();
        var used = 0;
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '\x1b')
            {
                var end = EscapeEnd(text, i);
                var escape = text.AsSpan(i, end - i);
                if (IsSgr(escape)) result.Append(escape);
                i = end;
                continue;
            }
            if (text[i] == '\t')
            {
                var spaces = Math.Min(8 - used % 8, width - used);
                result.Append(' ', spaces);
                used += spaces;
                i++;
                continue;
            }
            if (char.IsControl(text[i])) { i++; continue; }
            var element = StringInfo.GetNextTextElement(text, i);
            var cells = Width(element);
            if (used + cells > width) break;
            result.Append(element);
            used += cells;
            i += element.Length;
        }
        return result.ToString();
    }

    private static int EscapeEnd(string text, int start)
    {
        if (start + 1 >= text.Length) return text.Length;
        var kind = text[start + 1];
        var end = start + 2;
        if (kind is ']' or 'P' or 'X' or '^' or '_')
        {
            while (end < text.Length)
            {
                if (kind == ']' && text[end] == '\a') return end + 1;
                if (text[end] == '\x1b' && end + 1 < text.Length && text[end + 1] == '\\') return end + 2;
                end++;
            }
            return text.Length;
        }
        if (kind != '[') return end;
        while (end < text.Length && text[end] is not (>= '@' and <= '~')) end++;
        return Math.Min(end + 1, text.Length);
    }

    private static bool IsSgr(ReadOnlySpan<char> escape)
        => escape.Length >= 3 && escape[1] == '[' && escape[^1] == 'm';

    private static int Width(string grapheme)
    {
        var windows = OperatingSystem.IsWindows();
        if (!windows && (grapheme.Contains('\u200d') || grapheme.Contains('\ufe0f'))) return 2;
        var width = 0;
        foreach (var rune in grapheme.EnumerateRunes())
        {
            if (!windows && Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
            var v = rune.Value;
            var wide = v is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or
                >= 0xf900 and <= 0xfaff or >= 0xfe10 and <= 0xfe6f or >= 0xff00 and <= 0xff60 or >= 0x1f000 and <= 0x1faff or >= 0x20000 and <= 0x3ffff;
            // ConPTY advances for each rune, including combining marks and joiners.
            // Keep the grapheme atomic; only its console-cell budget differs.
            width = windows ? width + (wide ? 2 : 1) : Math.Max(width, wide ? 2 : 1);
        }
        return width;
    }
}
