using Shouldly;
using Twig.Browser;
using Xunit;

namespace Twig.Cli.Tests.Browser;

public sealed class BrowserTerminalTextTests
{
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
    public void OneColumnViewportReplacesUnfittableGraphemeWithoutSplittingItOrLosingTheTail()
    {
        var rows = BrowserTerminalText.Wrap("👩‍💻é中z", 1);
        rows.Select(row => row.Text).ShouldBe(new[] { "�", "é", "�", "z" });
        rows.Select(row => row.Offset).ShouldBe(new[] { 0, "👩‍💻".Length, "👩‍💻é".Length, "👩‍💻é中".Length });
    }

    [Fact]
    public void BelTerminatedHyperlinkRetainsOnlyItsVisibleLabel()
    {
        const string text = "before \x1b]8;id=114;https://example.test/#114\aReview\x1b]8;;\a after";

        BrowserTerminalText.Rows(text).ShouldBe(new[] { "before Review after" });
        BrowserTerminalText.Clip(text, 100).ShouldBe("before Review after");
        BrowserTerminalText.Clip(text, 9).ShouldBe("before Re");
    }

    [Fact]
    public void StTerminatedHyperlinkPreservesSgrAndCompleteGraphemesWhenClipped()
    {
        const string text = "\x1b[31m\x1b]8;id=114;https://example.test/#114\x1b\\é👩‍💻z\x1b]8;;\x1b\\\nnext\x1b[0m";

        BrowserTerminalText.Rows(text).ShouldBe(new[] { "\x1b[31mé👩‍💻z", "\x1b[31mnext\x1b[0m" });
        BrowserTerminalText.Clip(text, 2).ShouldBe("\x1b[31mé");
        BrowserTerminalText.Clip(text, 3).ShouldBe("\x1b[31mé👩‍💻");
    }

    [Fact]
    public void OtherTerminalControlStringsAreConsumedThroughSt()
    {
        const string text = "A\x1bPdcs\a\x1b[31mpayload\x1b\\B\x1bXsos\x1b\\C\x1b^pm\x1b\\D\x1b_apc\x1b\\E";

        BrowserTerminalText.Rows(text).ShouldBe(new[] { "ABCDE" });
        BrowserTerminalText.Clip(text, 100).ShouldBe("ABCDE");
    }

    [Fact]
    public void UnterminatedControlStringsConsumeTheRemainingPayload()
    {
        const string hyperlink = "label\x1b]8;id=114;https://example.test/\nnot a row";
        const string deviceControl = "label\x1bPpayload\a\nnot a row\x1b";

        BrowserTerminalText.Rows(hyperlink).ShouldBe(new[] { "label" });
        BrowserTerminalText.Clip(hyperlink, 100).ShouldBe("label");
        BrowserTerminalText.Rows(deviceControl).ShouldBe(new[] { "label" });
        BrowserTerminalText.Clip(deviceControl, 100).ShouldBe("label");
    }
}
