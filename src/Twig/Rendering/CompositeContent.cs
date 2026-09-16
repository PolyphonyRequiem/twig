using Spectre.Console;
using Spectre.Console.Rendering;
using Twig.RenderTree;

namespace Twig.Rendering;

// Provider-owned layout primitives: no proposal semantics, raw ANSI or terminal assumptions.
internal sealed class GuidedContent(IRenderable content, string first, string rest) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(1, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var gutter = Math.Max(first.Length, rest.Length);
        var i = 0;
        foreach (var line in Segment.SplitLines(content.Render(options, Math.Max(1, maxWidth - gutter))))
        {
            yield return new Segment(i++ == 0 ? first : rest);
            foreach (var segment in line) yield return segment;
            yield return Segment.LineBreak;
        }
    }
}

/// <summary>Label and value columns with a divider on EVERY physical line, even after wrapping.</summary>
internal sealed class FieldBlockContent(RenderNode.FieldBlock block, bool unicode) : IRenderable
{
    internal static readonly Color Surface = new(37, 44, 57);
    private static readonly Style Ink = new(new Color(235, 239, 245), Surface);
    public Measurement Measure(RenderOptions options, int maxWidth) => new(1, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        // Unknown terminal background is irrelevant inside this block: both ink and surface
        // are ours. Outside it the terminal/theme stays untouched. Monochrome retains structure.
        var width = Math.Max(2, Math.Min(maxWidth, 120));
        // Spectre cannot wrap a two-cell glyph into a one-cell column. On tiny
        // terminals, stack instead of handing it an impossible wrapping request.
        if (width < 7)
        {
            foreach (var field in block.Fields)
                foreach (var text in new[] { field.Key, field.Value.DisplayText })
                {
                    foreach (var segment in ((IRenderable)new Text(text, Ink)).Render(options, width))
                        yield return segment;
                    yield return Segment.LineBreak;
                }
            yield break;
        }
        var padding = width < 9 ? 0 : 1;
        var overhead = 3 + 2 * padding;
        var measured = block.Fields.Select(f => Segment.CellCount([new Segment(f.Key)])).DefaultIfEmpty(2).Max();
        var labels = Math.Clamp(Math.Max(block.LabelWidth, measured), 2, Math.Max(2, (width - overhead) / 3));
        var values = Math.Max(2, width - labels - overhead);
        foreach (var field in block.Fields)
        {
            var left = Segment.SplitLines(((IRenderable)new Text(field.Key, Ink)).Render(options, labels)).ToList();
            var paragraph = new Paragraph();
            if (field.Value.Spans is { } spans && string.Concat(spans.Select(s => s.Text)) == field.Value.DisplayText)
                foreach (var span in spans)
                {
                    var foreground = span.Role switch
                    {
                        RenderTextRole.Before => new Color(255, 165, 0),
                        RenderTextRole.After => new Color(85, 170, 255),
                        _ => Ink.Foreground,
                    };
                    paragraph.Append(span.Text, SpectreTheme.OnSurface(foreground, Surface));
                }
            else paragraph.Append(field.Value.DisplayText, Ink);
            var right = Segment.SplitLines(((IRenderable)paragraph).Render(options, values)).ToList();
            for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
            {
                yield return new Segment(new string(' ', padding), Ink);
                var label = i < left.Count ? left[i] : [];
                foreach (var segment in label) yield return segment;
                yield return new Segment(new string(' ', Math.Max(0, labels - Segment.CellCount(label))) + (unicode ? " │ " : " | "), Ink);
                var value = i < right.Count ? right[i] : [];
                foreach (var segment in value) yield return segment;
                yield return new Segment(new string(' ', Math.Max(0, values - Segment.CellCount(value) + padding)), Ink);
                yield return Segment.LineBreak;
            }
        }
    }
}
