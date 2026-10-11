using System.Globalization;
using System.Text;
using Spectre.Console;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.TuiAotPrototype1143;

internal sealed record FixtureItem(int Id, int? ParentId, string Type, string State, string Title);
internal sealed record ScreenLine(string Key, int Offset, string Text, int ItemId, bool Bench);
internal sealed record TextChunk(int Offset, string Text);

internal static class Fixtures
{
    internal static readonly FixtureItem[] Items = Enumerable.Range(1, 28).Select(i => new FixtureItem(
        i, i is >= 2 and <= 5 ? 1 : i is >= 7 and <= 10 ? 6 : null,
        i % 2 == 0 ? "Capability" : "Investigation", i % 3 == 0 ? "Exploring" : "Queued",
        i switch
        {
            1 => "A very long captured Bench root title — é 中 👩‍💻 🇳🇿 — wrapping must retain every grapheme and the TITLE_TAIL_1 sentinel",
            6 => "Second foldable root with narrow-width children and following-row anchor retention TITLE_TAIL_6",
            _ => $"Fixture row {i}: é 中 👩‍💻 🇳🇿 — complete title, never a cached-data or authority claim TITLE_TAIL_{i}"
        })).ToArray();

    internal static string Html(int itemId) => "<h2>Native inert HTML fixture</h2><p><strong>Full body</strong>, <em>emphasis</em>, " +
        "<a href='https://example.invalid/read-only'>inert link</a>, and code <code>[literal]</code>.</p>" +
        "<ul><li>First item</li><li>Second item keeps its tail LIST_TAIL</li></ul>" +
        "<pre>    indented code\n    code tail CODE_TAIL</pre>" +
        "<table><caption>Native table</caption><tr><th>Field</th><th>Complete value</th></tr>" +
        "<tr><td>Payload</td><td>A wide value with CJK 中 and a tail TABLE_TAIL</td></tr></table>" +
        string.Concat(Enumerable.Range(1, 24).Select(i => $"<p>Paragraph {i} for item {itemId}: loading height changes must not retarget following rows. é 中.</p>")) +
        "<script>NEVER_RENDER_ACTIVE</script><p>End of full description HTML_TAIL_" + itemId + "</p>";
}

// This deliberately bounded width approximation is NOT a Unicode terminal conformance table.
// Extended graphemes are indivisible; ambiguous/font-specific terminal widths remain a qualification gate.
internal static class CellText
{
    internal static int Width(string grapheme)
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

    internal static IEnumerable<TextChunk> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        var enumerator = StringInfo.GetTextElementEnumerator(text.Replace("\r", ""));
        var line = new StringBuilder();
        var used = 0;
        var offset = 0;
        var start = 0;
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (element == "\n")
            {
                yield return new TextChunk(start, line.ToString());
                line.Clear(); used = 0; start = ++offset;
                continue;
            }
            var cells = Width(element);
            if (used > 0 && used + cells > width)
            {
                yield return new TextChunk(start, line.ToString());
                line.Clear(); used = 0; start = offset;
            }
            // A 2-cell grapheme cannot fit in a one-cell viewport; explicit placeholder, not splitting.
            line.Append(cells > width ? "�" : element);
            used += Math.Min(cells, width);
            offset++;
        }
        yield return new TextChunk(start, line.ToString());
    }

    internal static string Clip(string text, int width) => Wrap(text, width).First().Text;
}

internal static class NativeDetail
{
    internal static IReadOnlyList<(string Key, string Text)> Body(FixtureItem item, int width, string html)
    {
        var output = new List<(string, string)> { ("title", RichHtmlRenderer.SafeText(item.Title)), ("meta", $"#{item.Id} {item.Type} · {item.State} · fixture-only cached observation") };
        var index = 0;
        // Parse once against a stable semantic width. Host table cells are always stacked and
        // rewrapped below, keeping block IDs stable across resize instead of changing node topology.
        // Render(), not ToMarkup(): includes every native Text/Markup/Table variant.
        _ = width;
        foreach (var node in RichHtmlRenderer.Render(html, 10000))
        {
            var key = "html:" + index++;
            switch (node)
            {
                case RenderNode.Text text: output.Add((key, text.Content)); break;
                case RenderNode.Markup markup: output.Add((key, Markup.Remove(markup.Content))); break;
                case RenderNode.Table table:
                    // Retain every emitted table cell with labels; custom host layout is intentionally plain.
                    if (table.Caption is not null) output.Add((key + ":caption", table.Caption));
                    for (var r = 0; r < table.Rows.Count; r++)
                        foreach (var column in table.Columns)
                            if (table.Rows[r].Cells.TryGetValue(column.Key, out var value))
                                output.Add(($"{key}:row{r}:col{column.Key}", column.DisplayName + ": " + value.DisplayText));
                    break;
                default: throw new InvalidOperationException("Native HTML emitted an unsupported presentation variant: " + node.GetType().Name);
            }
        }
        return output;
    }
}
