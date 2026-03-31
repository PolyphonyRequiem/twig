using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public class FormatterHelpersTests
{
    // ── GetStateLabel — returns the state name directly ─────────────

    [Theory]
    [InlineData("New", "New")]
    [InlineData("Active", "Active")]
    [InlineData("Resolved", "Resolved")]
    [InlineData("Closed", "Closed")]
    [InlineData("Done", "Done")]
    [InlineData("Removed", "Removed")]
    [InlineData("Custom State", "Custom State")]
    public void GetStateLabel_ReturnsStateName(string state, string expected)
    {
        FormatterHelpers.GetStateLabel(state).ShouldBe(expected);
    }

    [Fact]
    public void GetStateLabel_EmptyString_ReturnsQuestionMark()
    {
        FormatterHelpers.GetStateLabel("").ShouldBe("?");
    }

    // ── GetEffortDisplay (EPIC-007 E2-T7/E2-T10) ───────────────────

    [Fact]
    public void GetEffortDisplay_StoryPoints_ReturnsPts()
    {
        var item = CreateItemWithField("Microsoft.VSTS.Scheduling.StoryPoints", "5");
        FormatterHelpers.GetEffortDisplay(item).ShouldBe("(5 pts)");
    }

    [Fact]
    public void GetEffortDisplay_Effort_ReturnsPts()
    {
        var item = CreateItemWithField("Microsoft.VSTS.Scheduling.Effort", "8");
        FormatterHelpers.GetEffortDisplay(item).ShouldBe("(8 pts)");
    }

    [Fact]
    public void GetEffortDisplay_Size_ReturnsPts()
    {
        var item = CreateItemWithField("Microsoft.VSTS.Scheduling.Size", "13");
        FormatterHelpers.GetEffortDisplay(item).ShouldBe("(13 pts)");
    }

    [Fact]
    public void GetEffortDisplay_NoEffortField_ReturnsNull()
    {
        var item = CreateItemWithField("Microsoft.VSTS.Common.Priority", "2");
        FormatterHelpers.GetEffortDisplay(item).ShouldBeNull();
    }

    [Fact]
    public void GetEffortDisplay_EmptyFields_ReturnsNull()
    {
        var item = new WorkItem
        {
            Id = 1, Type = WorkItemType.Task, Title = "No Fields", State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        FormatterHelpers.GetEffortDisplay(item).ShouldBeNull();
    }

    [Fact]
    public void GetEffortDisplay_WhitespaceValue_ReturnsNull()
    {
        var item = CreateItemWithField("Microsoft.VSTS.Scheduling.StoryPoints", "  ");
        FormatterHelpers.GetEffortDisplay(item).ShouldBeNull();
    }

    [Fact]
    public void GetEffortDisplay_CaseInsensitiveSuffix()
    {
        var item = CreateItemWithField("Custom.Field.STORYPOINTS", "3");
        FormatterHelpers.GetEffortDisplay(item).ShouldBe("(3 pts)");
    }

    private static WorkItem CreateItemWithField(string key, string? value)
    {
        var item = new WorkItem
        {
            Id = 1, Type = WorkItemType.Task, Title = "Test", State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        item.ImportFields(new Dictionary<string, string?> { [key] = value });
        return item;
    }

    // ── HtmlToPlainText ────────────────────────────────────────────

    [Fact]
    public void HtmlToPlainText_NullInput_ReturnsEmpty()
    {
        FormatterHelpers.HtmlToPlainText(null).ShouldBe(string.Empty);
    }

    [Fact]
    public void HtmlToPlainText_EmptyString_ReturnsEmpty()
    {
        FormatterHelpers.HtmlToPlainText("").ShouldBe(string.Empty);
    }

    [Fact]
    public void HtmlToPlainText_WhitespaceOnly_ReturnsEmpty()
    {
        FormatterHelpers.HtmlToPlainText("   ").ShouldBe(string.Empty);
    }

    [Fact]
    public void HtmlToPlainText_PlainText_PassesThrough()
    {
        FormatterHelpers.HtmlToPlainText("Hello world").ShouldBe("Hello world");
    }

    [Fact]
    public void HtmlToPlainText_ParagraphTags_InsertNewlines()
    {
        var html = "<p>First paragraph</p><p>Second paragraph</p>";
        var result = FormatterHelpers.HtmlToPlainText(html);
        result.ShouldContain("First paragraph");
        result.ShouldContain("Second paragraph");
        result.Split('\n').Length.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void HtmlToPlainText_DivTags_InsertNewlines()
    {
        var html = "<div>Block one</div><div>Block two</div>";
        var result = FormatterHelpers.HtmlToPlainText(html);
        result.ShouldContain("Block one");
        result.ShouldContain("Block two");
        result.Split('\n').Count(l => l.Length > 0).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void HtmlToPlainText_BrTags_InsertNewlines()
    {
        var html = "Line one<br>Line two<br/>Line three";
        var result = FormatterHelpers.HtmlToPlainText(html);
        var lines = result.Split('\n').Where(l => l.Length > 0).ToArray();
        lines.Length.ShouldBe(3);
        lines[0].ShouldBe("Line one");
        lines[1].ShouldBe("Line two");
        lines[2].ShouldBe("Line three");
    }

    [Fact]
    public void HtmlToPlainText_HeadingTags_InsertNewlines()
    {
        var html = "<h1>Title</h1><p>Body text</p><h2>Subtitle</h2>";
        var result = FormatterHelpers.HtmlToPlainText(html);
        result.ShouldContain("Title");
        result.ShouldContain("Subtitle");
    }

    [Fact]
    public void HtmlToPlainText_ListItems_GetBulletPrefix()
    {
        var html = "<ul><li>Item one</li><li>Item two</li><li>Item three</li></ul>";
        var result = FormatterHelpers.HtmlToPlainText(html);
        var lines = result.Split('\n').Where(l => l.Length > 0).ToArray();
        lines.ShouldContain(l => l.StartsWith("• Item one"));
        lines.ShouldContain(l => l.StartsWith("• Item two"));
        lines.ShouldContain(l => l.StartsWith("• Item three"));
    }

    [Fact]
    public void HtmlToPlainText_DecodesNamedEntities()
    {
        var html = "A &amp; B &lt; C &gt; D &quot;E&quot; F&nbsp;G";
        var result = FormatterHelpers.HtmlToPlainText(html);
        result.ShouldBe("A & B < C > D \"E\" F G");
    }

    [Fact]
    public void HtmlToPlainText_CollapsesBlankLines()
    {
        var html = "<p>First</p><p></p><p></p><p></p><p>Second</p>";
        var result = FormatterHelpers.HtmlToPlainText(html);
        result.ShouldNotContain("\n\n\n");
    }

    [Fact]
    public void HtmlToPlainText_TrimsLeadingTrailingBlanks()
    {
        var html = "<p></p><p>Content</p><p></p>";
        var result = FormatterHelpers.HtmlToPlainText(html);
        result.ShouldNotStartWith("\n");
        result.ShouldNotEndWith("\n");
        result.ShouldContain("Content");
    }

    [Fact]
    public void HtmlToPlainText_TruncatesAtMaxLines()
    {
        var result = FormatterHelpers.HtmlToPlainText(string.Concat(Enumerable.Range(1, 35).Select(i => $"<p>Line {i}</p>")));
        result.ShouldContain("(+5 more lines)");
        result.ShouldContain("Line 1");
        result.ShouldContain("Line 30");
        result.ShouldNotContain("Line 31");
    }

    [Fact]
    public void HtmlToPlainText_ExactlyMaxLines_NoTruncation()
    {
        var result = FormatterHelpers.HtmlToPlainText(string.Concat(Enumerable.Range(1, 30).Select(i => $"<p>Line {i}</p>")));
        result.ShouldNotContain("more lines");
        result.ShouldContain("Line 30");
    }

    [Fact]
    public void HtmlToPlainText_StripsTags_PreservesContent()
    {
        var html = "<b>Bold</b> and <i>italic</i> and <span class=\"foo\">styled</span>";
        var result = FormatterHelpers.HtmlToPlainText(html);
        result.ShouldBe("Bold and italic and styled");
    }

    [Fact]
    public void HtmlToPlainText_MixedContent_RealWorldAdoHtml()
    {
        var html = "<div><p>As a developer, I want to see full descriptions.</p>" +
                   "<h2>Acceptance Criteria</h2>" +
                   "<ul><li>Multi-line rendering</li><li>HTML converted to text</li></ul>" +
                   "<p>See &lt;design doc&gt; for details &amp; notes.</p></div>";
        var result = FormatterHelpers.HtmlToPlainText(html);
        result.ShouldContain("As a developer");
        result.ShouldContain("Acceptance Criteria");
        result.ShouldContain("• Multi-line rendering");
        result.ShouldContain("• HTML converted to text");
        result.ShouldContain("See <design doc> for details & notes.");
    }

    [Fact]
    public void HtmlToPlainText_UnclosedTag_TreatsAsLiteral()
    {
        // An unclosed '<' with no matching '>' is flushed as literal (consistent with StripHtmlTags)
        var html = "Price < 100";
        var result = FormatterHelpers.HtmlToPlainText(html);
        result.ShouldContain("Price < 100");
    }

    [Fact]
    public void HtmlToPlainText_CaseInsensitiveTags()
    {
        var html = "<P>Upper case</P><BR><DIV>Also block</DIV>";
        var result = FormatterHelpers.HtmlToPlainText(html);
        result.ShouldContain("Upper case");
        result.ShouldContain("Also block");
    }

    // ── BuildProgressBar (EPIC-004 ITEM-020) ────────────────────────

    [Fact]
    public void BuildProgressBar_ZeroTotal_ReturnsEmpty()
    {
        FormatterHelpers.BuildProgressBar(0, 0).ShouldBe("");
    }

    [Fact]
    public void BuildProgressBar_ZeroDone_AllEmpty()
    {
        var result = FormatterHelpers.BuildProgressBar(0, 5, width: 10);
        result.ShouldContain("[░░░░░░░░░░]");
        result.ShouldContain("0/5");
        result.ShouldNotContain("█");
    }

    [Fact]
    public void BuildProgressBar_PartialProgress_MixedBlocks()
    {
        var result = FormatterHelpers.BuildProgressBar(3, 5, width: 10);
        result.ShouldContain("█");
        result.ShouldContain("░");
        result.ShouldContain("3/5");
    }

    [Fact]
    public void BuildProgressBar_AllDone_AllFilled_Green()
    {
        var result = FormatterHelpers.BuildProgressBar(5, 5, width: 10);
        result.ShouldContain("[██████████]");
        result.ShouldContain("5/5");
        // Green ANSI escape wrapping
        result.ShouldContain("\x1b[32m");
        result.ShouldContain("\x1b[0m");
    }

    [Fact]
    public void BuildProgressBar_LargeNumbers_AllDone_Green()
    {
        var result = FormatterHelpers.BuildProgressBar(100, 100, width: 20);
        result.ShouldContain("100/100");
        result.ShouldContain("\x1b[32m");
    }

    [Fact]
    public void BuildProgressBar_DoneExceedsTotal_ClampedToTotal()
    {
        var result = FormatterHelpers.BuildProgressBar(10, 5, width: 10);
        result.ShouldContain("[██████████]");
        result.ShouldContain("5/5");
        result.ShouldContain("\x1b[32m"); // Green because complete
    }

    [Fact]
    public void BuildProgressBar_NegativeDone_ClampedToZero()
    {
        var result = FormatterHelpers.BuildProgressBar(-3, 5, width: 10);
        result.ShouldContain("0/5");
        result.ShouldNotContain("█");
    }

    [Fact]
    public void BuildProgressBar_NegativeTotal_ReturnsEmpty()
    {
        FormatterHelpers.BuildProgressBar(3, -1).ShouldBe("");
    }

    [Fact]
    public void BuildProgressBar_FourOfSix_CorrectFormat()
    {
        // AC-006: [████░░] 4/6 format
        var result = FormatterHelpers.BuildProgressBar(4, 6, width: 6);
        result.ShouldContain("█");
        result.ShouldContain("░");
        result.ShouldContain("4/6");
    }

    [Fact]
    public void BuildProgressBar_UseAnsiFalse_Complete_NoAnsiCodes()
    {
        var result = FormatterHelpers.BuildProgressBar(5, 5, width: 10, useAnsi: false);
        result.ShouldContain("[██████████]");
        result.ShouldContain("5/5");
        // No ANSI codes when useAnsi is false
        result.ShouldNotContain("\x1b");
    }

    [Fact]
    public void BuildProgressBar_UseAnsiFalse_Incomplete_SameAsDefault()
    {
        var withAnsi = FormatterHelpers.BuildProgressBar(3, 5, width: 10, useAnsi: true);
        var withoutAnsi = FormatterHelpers.BuildProgressBar(3, 5, width: 10, useAnsi: false);
        // Incomplete bars don't have ANSI either way
        withAnsi.ShouldBe(withoutAnsi);
    }

    // ── IsProgressComplete ───────────────────────────────────────────

    [Fact]
    public void IsProgressComplete_DoneEqualsTotal_ReturnsTrue()
    {
        FormatterHelpers.IsProgressComplete(5, 5).ShouldBeTrue();
    }

    [Fact]
    public void IsProgressComplete_DoneExceedsTotal_ReturnsTrue()
    {
        FormatterHelpers.IsProgressComplete(7, 5).ShouldBeTrue();
    }

    [Fact]
    public void IsProgressComplete_DoneLessThanTotal_ReturnsFalse()
    {
        FormatterHelpers.IsProgressComplete(3, 5).ShouldBeFalse();
    }

    [Fact]
    public void IsProgressComplete_ZeroTotal_ReturnsFalse()
    {
        FormatterHelpers.IsProgressComplete(0, 0).ShouldBeFalse();
    }
}
