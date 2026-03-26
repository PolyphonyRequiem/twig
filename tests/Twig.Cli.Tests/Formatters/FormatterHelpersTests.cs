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
