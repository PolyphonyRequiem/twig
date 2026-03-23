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
}
