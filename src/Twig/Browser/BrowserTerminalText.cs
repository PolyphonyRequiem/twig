using System.Globalization;
using System.Text;

namespace Twig.Browser;

/// <summary>
/// Viewport-only projection of native Show output. SGR is retained, terminal actions
/// are not. Graphemes remain indivisible; ambiguous/font-specific widths remain a
/// terminal qualification gate, as in the bounded Windows 1143 evidence slice.
/// </summary>
internal static class BrowserTerminalText
{
    internal static IReadOnlyList<string> Rows(string text)
    {
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
                rows.Add(row.ToString());
                row.Clear().Append(style);
                i++;
            }
            else
            {
                if (!char.IsControl(text[i]) || text[i] == '\t') row.Append(text[i]);
                i++;
            }
        }
        if (row.Length > 0) rows.Add(row.ToString());
        return rows;
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
        if (text[start + 1] != '[') return start + 2;
        var end = start + 2;
        while (end < text.Length && text[end] is not (>= '@' and <= '~')) end++;
        return Math.Min(end + 1, text.Length);
    }

    private static bool IsSgr(ReadOnlySpan<char> escape)
        => escape.Length >= 3 && escape[1] == '[' && escape[^1] == 'm';

    private static int Width(string grapheme)
    {
        if (grapheme.Contains('\u200d') || grapheme.Contains('\ufe0f')) return 2;
        var width = 0;
        foreach (var rune in grapheme.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
            var v = rune.Value;
            var wide = v is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or
                >= 0xf900 and <= 0xfaff or >= 0xfe10 and <= 0xfe6f or >= 0xff00 and <= 0xff60 or >= 0x1f000 and <= 0x1faff or >= 0x20000 and <= 0x3ffff;
            width = Math.Max(width, wide ? 2 : 1);
        }
        return width;
    }
}
