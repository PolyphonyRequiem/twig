using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using Twig.Domain.ValueObjects;
using Twig.Rendering;

namespace Twig.Browser;

/// <summary>Pure physical-row projection of an admitted Bench and its retained native theme.</summary>
internal static class BrowserBenchPresentation
{
    internal static IReadOnlyList<BrowserSourceRow> Rows(BrowserBenchObservation observation, BrowserBenchItem item,
        string prefix, int width, bool selected, bool table)
    {
        width = Math.Clamp(width, 1, 10000);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = InteractionSupport.No,
        });
        console.Profile.Width = width;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.TrueColor;
        console.Profile.Capabilities.Links = false;
        console.Profile.Capabilities.Interactive = false;
        console.Profile.Capabilities.Unicode = true;
        var projection = new ItemRows(observation.Theme, item, prefix, width, selected, table);
        console.Write(projection);
        var physical = writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new BrowserSourceRow[projection.Offsets.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = new("item", projection.Offsets[i], physical[i]);
        return result;
    }

    private sealed record StyledRun(int Offset, string Text, Style Style);
    private sealed record CellLine(int Offset, string Text, IReadOnlyList<Segment> Segments);

    private sealed class ItemRows(SpectreTheme theme, BrowserBenchItem item, string prefix, int width,
        bool selected, bool table) : IRenderable
    {
        private static readonly Color Selection = new(22, 50, 79);
        private Dictionary<Style, Style>? _surfaceStyles;
        internal List<int> Offsets { get; } = [];

        public Measurement Measure(RenderOptions options, int maxWidth) => new(width, width);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            Offsets.Clear();
            var type = RichHtmlRenderer.SafeText(item.Type);
            var parsedType = WorkItemType.Parse(type);
            var typeMarkup = parsedType.IsSuccess
                ? theme.FormatTypeBadge(parsedType.Value) + " " + Markup.Escape(type)
                : "[grey]○ uncached[/]";
            var state = RichHtmlRenderer.SafeText(item.State);
            var stateMarkup = state.Length == 0 ? "[grey]—[/]" : theme.FormatReviewState(state);
            var identity = "[bold]#" + item.Id.ToString(CultureInfo.InvariantCulture) + "[/]";
            var title = Markup.Escape(RichHtmlRenderer.SafeText(item.Title));
            var indicators = Indicators();
            var heading = identity + " " + typeMarkup + " " + stateMarkup + "  ";
            if (table && width >= 48)
            {
                // Match the browser's ID / TYPE / STATE / TITLE headings. Long custom
                // names wrap within their column rather than silently losing metadata.
                var cells = new[]
                {
                    Wrap((selected ? "› " : "  ") + identity, 12, options), Wrap(typeMarkup, 12, options),
                    Wrap(stateMarkup, 12, options), Wrap(title + indicators, width - 39, options),
                };
                var headingLength = Markup.Remove(heading).Length;
                for (var row = 0; row < cells.Max(c => c.Count); row++)
                {
                    var titleLines = cells[3];
                    // Offsets use the same logical label as Tree, so a view switch
                    // keeps a wrapped title anchor rather than treating it as metadata.
                    Offsets.Add(row == 0 ? 0 : headingLength + (row < titleLines.Count ? titleLines[row].Offset :
                        titleLines[^1].Offset + titleLines[^1].Text.Length));
                    for (var column = 0; column < cells.Length; column++)
                    {
                        var cellWidth = column == 3 ? width - 39 : 12;
                        var line = row < cells[column].Count ? cells[column][row] : null;
                        if (line is not null)
                            foreach (var segment in line.Segments) yield return Ink(segment);
                        var padding = Math.Max(0, cellWidth - BrowserTerminalText.CellWidth(line?.Text ?? ""));
                        if (column < cells.Length - 1) padding++;
                        if (padding > 0) yield return Ink(new Segment(new string(' ', padding)));
                    }
                    yield return Segment.LineBreak;
                }
                yield break;
            }

            var first = BrowserTerminalText.Clip(RichHtmlRenderer.SafeText(prefix), Math.Max(0, width - 2));
            var gutter = BrowserTerminalText.CellWidth(first);
            var continuation = new string(' ', gutter);
            var lines = Wrap(heading + title + indicators,
                Math.Max(1, width - gutter), options);
            foreach (var line in lines)
            {
                Offsets.Add(line.Offset);
                yield return Ink(new Segment(line == lines[0] ? first : continuation, new Style(Color.Grey)));
                foreach (var segment in line.Segments) yield return Ink(segment);
                var padding = Math.Max(0, width - gutter - BrowserTerminalText.CellWidth(line.Text));
                if (padding > 0) yield return Ink(new Segment(new string(' ', padding)));
                yield return Segment.LineBreak;
            }
        }

        private string Indicators()
        {
            var tags = new StringBuilder();
            if (item.Seed) tags.Append("  ").Append(theme.FormatSeedIndicator()).Append(" seed");
            if (item.Owed) tags.Append("  [yellow]● owed[/]");
            if (item.SinglePin) tags.Append("  [aqua]◆ single pin[/]");
            if (item.TreePin) tags.Append("  [aqua]↳ subtree pin[/]");
            var owners = item.SubtreeOwners.Where(id => id != item.Id).ToArray();
            if (owners.Length > 0)
                tags.Append("  [grey]↳ inherited ").Append(string.Join(", ", owners.Select(id =>
                    "#" + id.ToString(CultureInfo.InvariantCulture)))).Append("[/]");
            if (!item.Member) tags.Append("  [grey]↑ ancestor[/]");
            return tags.ToString();
        }

        private Segment Ink(Segment segment)
        {
            _surfaceStyles ??= [];
            if (!_surfaceStyles.TryGetValue(segment.Style, out var style))
            {
                var foreground = segment.Style.Foreground == Color.Default ? new Color(177, 217, 239) : segment.Style.Foreground;
                var background = selected ? Selection : BrowserTerminalFrame.ContentBackground;
                if (selected) foreground = SpectreTheme.OnSurface(foreground, background).Foreground;
                style = new Style(foreground, background, selected ? (segment.Style.Decoration & ~Decoration.Dim) | Decoration.Bold : segment.Style.Decoration);
                _surfaceStyles.Add(segment.Style, style);
            }
            return new Segment(segment.Text, style);
        }

        private static IReadOnlyList<CellLine> Wrap(string markup, int cellWidth, RenderOptions options)
        {
            var text = new StringBuilder();
            var runs = new List<StyledRun>();
            foreach (var segment in ((IRenderable)new Markup(markup)).Render(options, int.MaxValue))
            {
                if (segment.IsControlCode) continue;
                var value = segment.IsLineBreak ? "\n" : segment.Text;
                runs.Add(new(text.Length, value, segment.Style));
                text.Append(value);
            }
            var lines = new List<CellLine>();
            foreach (var chunk in BrowserTerminalText.Wrap(text.ToString(), cellWidth))
            {
                var segments = new List<Segment>();
                var end = chunk.Offset + chunk.Text.Length;
                foreach (var run in runs)
                {
                    if (run.Offset >= end) break;
                    var start = Math.Max(chunk.Offset, run.Offset);
                    var stop = Math.Min(end, run.Offset + run.Text.Length);
                    if (stop > start) segments.Add(new Segment(run.Text.Substring(start - run.Offset, stop - start), run.Style));
                }
                lines.Add(new(chunk.Offset, chunk.Text, segments));
            }
            return lines;
        }
    }
}
