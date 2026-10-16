using System.Globalization;
using Shouldly;
using Twig.Browser;
using Xunit;

namespace Twig.Cli.Tests.Browser;

public sealed class BrowserTerminalTextTests
{
    [Fact]
    public async Task DetailContentAnchorKeepsTheSameParagraphWhenNativeHtmlRewraps()
    {
        var item = new Twig.Domain.Aggregates.WorkItem
        {
            Id = 42,
            Type = Twig.Domain.ValueObjects.WorkItemType.Task,
            Title = "Retained detail",
            AssignedTo = new string('A', 20),
            AreaPath = Twig.Domain.ValueObjects.AreaPath.Parse(new string('B', 60)).Value,
            IterationPath = Twig.Domain.ValueObjects.IterationPath.Parse(new string('C', 60)).Value,
        };
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>No publication.</p>" + string.Concat(Enumerable.Range(0, 24).Select(i =>
                $"<p>Paragraph {i}: cached native HTML field tail; safe literal text, café é 中文 👩‍💻 🇨🇦 with wrapped content retained.</p>")),
        });
        var snapshot = new BrowserDetailSnapshot(item, null, [], [], null, [], [], null, 15,
            new Twig.Rendering.SpectreTheme(new Twig.Infrastructure.Config.DisplayConfig()));
        var current = await snapshot.RenderRowsAsync(78);
        var anchor = current.Single(row => row.Text.Contains("Paragraph 19:", StringComparison.Ordinal));

        foreach (var width in new[] { 43, 118, 78 })
        {
            var reflowed = await snapshot.RenderRowsAsync(width);
            var restored = reflowed.Last(row => row.Source == anchor.Source && row.Offset <= anchor.Offset);
            restored.Text.ShouldContain("Paragraph 19:");
            var plain = BrowserTerminalText.Rows((await snapshot.RenderAsync(width, ansi: true)).Ansi, width);
            reflowed.Select(row => StripSgr(row.Text)).ShouldBe(plain.Select(StripSgr));
            reflowed.ShouldAllBe(row => !row.Text.Contains("twig-source", StringComparison.Ordinal));
            anchor = restored;
        }
    }

    [Fact]
    public async Task LiteralBoxDrawingRowsRemainDistinctSourcePositions()
    {
        var item = NativeBrowserFixture.Item(42, "Literal diagram");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<pre>┌──┐\n│  │\n└──┘</pre><p>After diagram.</p>",
        });
        var snapshot = new BrowserDetailSnapshot(item, null, [], [], null, [], [], null, 15,
            new Twig.Rendering.SpectreTheme(new Twig.Infrastructure.Config.DisplayConfig()));
        var original = await snapshot.RenderRowsAsync(78);
        var anchor = original.Single(row => row.Text.Contains("┌──┐", StringComparison.Ordinal));
        foreach (var width in new[] { 43, 118, 78 })
        {
            var rows = await snapshot.RenderRowsAsync(width);
            var restored = rows.Last(row => row.Source == anchor.Source && row.Offset <= anchor.Offset);
            restored.Text.ShouldContain("┌──┐");
            restored.Text.ShouldNotContain("After diagram.");
            anchor = restored;
        }
    }

    [Fact]
    public async Task WrappedFieldLabelsDoNotAdvanceValueSourcePositions()
    {
        var item = NativeBrowserFixture.Item(42, "Wrapped label");
        const string key = "Custom.LongValue";
        var value = string.Join(" ", Enumerable.Range(0, 80).Select(i => $"VALUE{i:00}"));
        item.ImportFields(new Dictionary<string, string?> { [key] = value });
        var snapshot = new BrowserDetailSnapshot(item, null, [],
            [new(key, "Long field display label wrapping across multiple narrow rows", "string", false)],
            null, [], [], null, 15, new Twig.Rendering.SpectreTheme(new Twig.Infrastructure.Config.DisplayConfig()));
        foreach (var width in new[] { 43, 118 })
        {
            var rows = await snapshot.RenderRowsAsync(width);
            foreach (var row in rows.Where(row => row.Source == key))
            {
                var match = System.Text.RegularExpressions.Regex.Match(StripSgr(row.Text), @"VALUE\d{2}");
                match.Success.ShouldBeTrue();
                var sourceIndex = value.IndexOf(match.Value, StringComparison.Ordinal);
                value[..sourceIndex].Count(character => !char.IsWhiteSpace(character)).ShouldBe(row.Offset);
            }
        }
    }

    [Fact]
    public async Task EditingOverlayKeepsRichItemContentAndDoesNotMutateTheReadObservation()
    {
        var item = NativeBrowserFixture.Item(42, "Original title");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<h2>Readable section</h2><p>Complete original description tail.</p>",
        });
        var snapshot = new BrowserDetailSnapshot(item, null, [], [], null, [], [], null, 15,
            new Twig.Rendering.SpectreTheme(new Twig.Infrastructure.Config.DisplayConfig()));
        var original = await snapshot.RenderRowsAsync(78);
        var baseline = new Twig.Domain.Services.WorkItemMapper().ToSnapshot(item);
        var editing = await snapshot.RenderEditingRowsAsync(78, baseline, new Dictionary<string, string?>
        {
            ["System.Title"] = "Draft [red] title ▏",
            ["System.AssignedTo"] = "New assignee",
            ["System.State"] = "Draft state",
        });
        var visible = string.Join("\n", editing.Select(row => BrowserTerminalText.PlainText(row.Text)));
        visible.ShouldContain("Draft [red] title ▏");
        visible.ShouldContain("New assignee");
        visible.ShouldContain("Draft state");
        visible.ShouldContain("Readable section");
        visible.ShouldContain("Complete original description tail.");
        visible.ShouldNotContain("<h2>");
        editing.ShouldContain(row => row.Source == "System.Title" && row.Text.Contains("Draft", StringComparison.Ordinal));
        (await snapshot.RenderRowsAsync(78)).ShouldBe(original);
    }

    private static string StripSgr(string value)
        => System.Text.RegularExpressions.Regex.Replace(value, @"\x1b\[[0-9;]*m", "");

    [Fact]
    public void WordWrappingPreservesEverySpaceAndCompleteGrapheme()
    {
        const string text = "one  two é 👩‍💻 中 final";
        var rows = BrowserTerminalText.Wrap(text, 6);
        string.Concat(rows.Select(row => row.Text)).ShouldBe(text);
        rows[0].Text.ShouldBe("one  ");
        rows.ShouldContain(row => row.Text.Contains("👩‍💻", StringComparison.Ordinal));
        rows.ShouldContain(row => row.Text.Contains("é", StringComparison.Ordinal));
        rows.ShouldAllBe(row => BrowserTerminalText.CellWidth(row.Text) <= 6);
        for (var i = 0; i < rows.Count; i++)
            text[rows[i].Offset..].ShouldStartWith(rows[i].Text);
    }

    [Fact]
    public void OneColumnWrappingPreservesUnfittableGraphemesForReflow()
    {
        var rows = BrowserTerminalText.Wrap("👩‍💻é中z", 1);
        rows.Select(row => row.Text).ShouldBe(new[] { "👩‍💻", "é", "中", "z" });
        rows.Select(row => row.Offset).ShouldBe(new[] { 0, "👩‍💻".Length, "👩‍💻é".Length, "👩‍💻é中".Length });
        string.Concat(rows.Select(row => row.Text)).ShouldBe("👩‍💻é中z");
    }

    [Fact]
    public void BelTerminatedHyperlinkRetainsOnlyItsVisibleLabel()
    {
        const string text = "before \x1b]8;id=114;https://example.test/#114\aReview\x1b]8;;\a after";

        BrowserTerminalText.Rows(text, 100).ShouldBe(new[] { "before Review after" });
        BrowserTerminalText.Clip(text, 100).ShouldBe("before Review after");
        BrowserTerminalText.Clip(text, 9).ShouldBe("before Re");
    }

    [Fact]
    public void StTerminatedHyperlinkPreservesSgrAndCompleteGraphemesWhenClipped()
    {
        const string text = "\x1b[31m\x1b]8;id=114;https://example.test/#114\x1b\\é👩‍💻z\x1b]8;;\x1b\\\nnext\x1b[0m";

        BrowserTerminalText.Rows(text, 100).ShouldBe(new[] { "\x1b[31mé👩‍💻z", "\x1b[31mnext\x1b[0m" });
        BrowserTerminalText.Clip(text, 2).ShouldBe("\x1b[31mé");
        var completeWidth = OperatingSystem.IsWindows() ? 7 : 3;
        BrowserTerminalText.Clip(text, completeWidth - 1).ShouldBe("\x1b[31mé");
        BrowserTerminalText.Clip(text, completeWidth).ShouldBe("\x1b[31mé👩‍💻");
    }

    [Fact]
    public void OtherTerminalControlStringsAreConsumedThroughSt()
    {
        const string text = "A\x1bPdcs\a\x1b[31mpayload\x1b\\B\x1bXsos\x1b\\C\x1b^pm\x1b\\D\x1b_apc\x1b\\E";

        BrowserTerminalText.Rows(text, 100).ShouldBe(new[] { "ABCDE" });
        BrowserTerminalText.Clip(text, 100).ShouldBe("ABCDE");
    }

    [Fact]
    public void UnterminatedControlStringsConsumeTheRemainingPayload()
    {
        const string hyperlink = "label\x1b]8;id=114;https://example.test/\nnot a row";
        const string deviceControl = "label\x1bPpayload\a\nnot a row\x1b";

        BrowserTerminalText.Rows(hyperlink, 100).ShouldBe(new[] { "label" });
        BrowserTerminalText.Clip(hyperlink, 100).ShouldBe("label");
        BrowserTerminalText.Rows(deviceControl, 100).ShouldBe(new[] { "label" });
        BrowserTerminalText.Clip(deviceControl, 100).ShouldBe("label");
    }

    [Theory]
    [InlineData("é", 2, 1)]
    [InlineData("👩‍💻", 5, 2)]
    [InlineData("🇨🇦", 4, 2)]
    public void MeasuredGraphemeBudgetClipsOnlyAtCompleteGraphemeBoundaries(string grapheme, int windowsCells, int unixCells)
    {
        var cells = OperatingSystem.IsWindows() ? windowsCells : unixCells;
        var styled = "\x1b[31m" + grapheme + "TAIL";

        BrowserTerminalText.CellWidth(grapheme).ShouldBe(cells);
        BrowserTerminalText.Clip(styled, cells - 1).ShouldBe("\x1b[31m");
        BrowserTerminalText.Clip(styled, cells).ShouldBe("\x1b[31m" + grapheme);
    }

    [Fact]
    public void WrappedUnicodeTitleSurvivesPaintingWithoutExceedingThePlatformCellBudget()
    {
        const string title = "· #-14 Task [To Do] owed seed  BENCH_FIXTURE_14e10111 — café é 中文 👩‍💻 🇨🇦 wrapped title continuation preserved full title preserved full title preserved full title preserved full title preserved full title preserved full title preserved full title BENCH_TITLE_TAIL_14e10111";
        var rows = BrowserTerminalText.Wrap(title, 120);

        string.Concat(rows.Select(row => row.Text)).ShouldBe(title);
        foreach (var row in rows)
        {
            ObservedPlatformCells(row.Text).ShouldBeLessThanOrEqualTo(120);
            BrowserTerminalText.Clip(row.Text, 120).ShouldBe(row.Text);
        }
    }

    [Fact]
    public void NativeStyledRowsReflowBeforePaintingWithoutDroppingTitleTextOrPanelPrefix()
    {
        const string content = "é👩‍💻🇨🇦TAIL";
        var rows = BrowserTerminalText.Rows("\x1b[31m│ " + content + "\x1b[0m", 12);
        var visible = rows.Select(row => System.Text.RegularExpressions.Regex.Replace(row, @"\x1b\[[0-9;]*m", "")).ToArray();

        string.Concat(visible.Select(row => row[2..])).ShouldBe(content);
        foreach (var row in visible)
        {
            row.ShouldStartWith("│ ");
            ObservedPlatformCells(row).ShouldBeLessThanOrEqualTo(12);
        }
        rows.ShouldAllBe(row => row.StartsWith("\x1b[31m│ ", StringComparison.Ordinal));
        rows.ShouldAllBe(row => BrowserTerminalText.Clip(row, 12) == row);
    }

    private static int ObservedPlatformCells(string text)
    {
        // Independent cursor-advance observations, not BrowserTerminalText's width calculation.
        var windows = OperatingSystem.IsWindows();
        var elements = StringInfo.GetTextElementEnumerator(text);
        var cells = 0;
        while (elements.MoveNext())
        {
            cells += elements.GetTextElement() switch
            {
                "é" => windows ? 2 : 1,
                "👩‍💻" => windows ? 5 : 2,
                "🇨🇦" => windows ? 4 : 2,
                "中" or "文" => 2,
                _ => 1,
            };
        }
        return cells;
    }
}
