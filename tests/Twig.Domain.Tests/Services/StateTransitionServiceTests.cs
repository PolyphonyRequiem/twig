using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class StateTransitionServiceTests
{
    private static StateEntry[] ToStateEntries(params string[] names) =>
        names.Select(n => new StateEntry(n, StateCategory.Unknown, null)).ToArray();

    private static ProcessTypeRecord MakeRecord(string typeName, string[] states, string[] childTypes) =>
        new()
        {
            TypeName = typeName,
            States = ToStateEntries(states),
            ValidChildTypes = childTypes,
        };

    // ═══════════════════════════════════════════════════════════════
    //  Basic-style
    // ═══════════════════════════════════════════════════════════════

    private static ProcessConfiguration BuildBasicConfig() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", new[] { "To Do", "Doing", "Done" }, new[] { "Issue" }),
            MakeRecord("Issue", new[] { "To Do", "Doing", "Done" }, new[] { "Task" }),
            MakeRecord("Task", new[] { "To Do", "Doing", "Done" }, Array.Empty<string>()),
        });

    [Fact]
    public void Basic_Forward_IsAllowed_NoConfirmation()
    {
        var config = BuildBasicConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.Issue, "To Do", "Doing");

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeFalse();
        result.RequiresReason.ShouldBeFalse();
    }

    [Fact]
    public void Basic_Backward_RequiresConfirmation()
    {
        var config = BuildBasicConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.Issue, "Doing", "To Do");

        result.Kind.ShouldBe(TransitionKind.Backward);
        result.IsAllowed.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeTrue();
        result.RequiresReason.ShouldBeFalse();
    }

    [Fact]
    public void Basic_Invalid_UnknownStates()
    {
        var config = BuildBasicConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.Issue, "To Do", "Nonexistent");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
        result.RequiresConfirmation.ShouldBeFalse();
        result.RequiresReason.ShouldBeFalse();
    }

    [Fact]
    public void Basic_Invalid_UnknownType()
    {
        var config = BuildBasicConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.UserStory, "To Do", "Doing");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Agile-style
    // ═══════════════════════════════════════════════════════════════

    private static ProcessConfiguration BuildAgileConfig() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", new[] { "New", "Active", "Closed", "Removed" }, new[] { "Feature" }),
            MakeRecord("Feature", new[] { "New", "Active", "Closed", "Removed" }, new[] { "User Story", "Bug" }),
            MakeRecord("User Story", new[] { "New", "Active", "Resolved", "Closed", "Removed" }, new[] { "Task" }),
            MakeRecord("Bug", new[] { "New", "Active", "Resolved", "Closed" }, new[] { "Task" }),
            MakeRecord("Task", new[] { "New", "Active", "Closed", "Removed" }, Array.Empty<string>()),
        });

    [Theory]
    [InlineData("New", "Active")]
    [InlineData("Active", "Resolved")]
    [InlineData("Resolved", "Closed")]
    public void Agile_UserStory_Forward(string from, string to)
    {
        var config = BuildAgileConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.UserStory, from, to);

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Active", "New")]
    [InlineData("Resolved", "Active")]
    [InlineData("Closed", "Resolved")]
    public void Agile_UserStory_Backward(string from, string to)
    {
        var config = BuildAgileConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.UserStory, from, to);

        result.Kind.ShouldBe(TransitionKind.Backward);
        result.IsAllowed.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeTrue();
        result.RequiresReason.ShouldBeFalse();
    }

    [Theory]
    [InlineData("New", "Removed")]
    [InlineData("Active", "Removed")]
    [InlineData("Resolved", "Removed")]
    [InlineData("Closed", "Removed")]
    public void Agile_UserStory_Cut(string from, string to)
    {
        var config = BuildAgileConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.UserStory, from, to);

        result.Kind.ShouldBe(TransitionKind.Cut);
        result.IsAllowed.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeTrue();
        result.RequiresReason.ShouldBeTrue();
    }

    [Fact]
    public void Agile_Invalid_SameState()
    {
        var config = BuildAgileConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.UserStory, "Active", "Active");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scrum-style
    // ═══════════════════════════════════════════════════════════════

    private static ProcessConfiguration BuildScrumConfig() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", new[] { "New", "In Progress", "Done", "Removed" }, new[] { "Feature" }),
            MakeRecord("Feature", new[] { "New", "In Progress", "Done", "Removed" }, new[] { "Product Backlog Item", "Bug" }),
            MakeRecord("Product Backlog Item", new[] { "New", "Approved", "Committed", "Done", "Removed" }, new[] { "Task" }),
            MakeRecord("Bug", new[] { "New", "Approved", "Committed", "Done", "Removed" }, new[] { "Task" }),
            MakeRecord("Task", new[] { "To Do", "In Progress", "Done", "Removed" }, Array.Empty<string>()),
        });

    [Theory]
    [InlineData("New", "Approved")]
    [InlineData("Approved", "Committed")]
    [InlineData("Committed", "Done")]
    public void Scrum_PBI_Forward(string from, string to)
    {
        var config = BuildScrumConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.ProductBacklogItem, from, to);

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Committed", "Approved")]
    [InlineData("Approved", "New")]
    public void Scrum_PBI_Backward(string from, string to)
    {
        var config = BuildScrumConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.ProductBacklogItem, from, to);

        result.Kind.ShouldBe(TransitionKind.Backward);
        result.IsAllowed.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeTrue();
    }

    [Theory]
    [InlineData("New", "Removed")]
    [InlineData("Committed", "Removed")]
    public void Scrum_PBI_Cut(string from, string to)
    {
        var config = BuildScrumConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.ProductBacklogItem, from, to);

        result.Kind.ShouldBe(TransitionKind.Cut);
        result.IsAllowed.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeTrue();
        result.RequiresReason.ShouldBeTrue();
    }

    [Fact]
    public void Scrum_Invalid_UnknownTransition()
    {
        var config = BuildScrumConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.ProductBacklogItem, "New", "Bogus");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  CMMI-style
    // ═══════════════════════════════════════════════════════════════

    private static ProcessConfiguration BuildCmmiConfig() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, new[] { "Feature" }),
            MakeRecord("Feature", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, new[] { "Requirement" }),
            MakeRecord("Requirement", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, new[] { "Task" }),
            MakeRecord("Bug", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, new[] { "Task" }),
            MakeRecord("Task", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, Array.Empty<string>()),
        });

    [Theory]
    [InlineData("Proposed", "Active")]
    [InlineData("Active", "Resolved")]
    [InlineData("Resolved", "Closed")]
    public void CMMI_Requirement_Forward(string from, string to)
    {
        var config = BuildCmmiConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.Requirement, from, to);

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Active", "Proposed")]
    [InlineData("Resolved", "Active")]
    public void CMMI_Requirement_Backward(string from, string to)
    {
        var config = BuildCmmiConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.Requirement, from, to);

        result.Kind.ShouldBe(TransitionKind.Backward);
        result.IsAllowed.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Proposed", "Removed")]
    [InlineData("Active", "Removed")]
    [InlineData("Resolved", "Removed")]
    [InlineData("Closed", "Removed")]
    public void CMMI_Requirement_Cut(string from, string to)
    {
        var config = BuildCmmiConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.Requirement, from, to);

        result.Kind.ShouldBe(TransitionKind.Cut);
        result.IsAllowed.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeTrue();
        result.RequiresReason.ShouldBeTrue();
    }

    [Fact]
    public void CMMI_Invalid_UnknownType()
    {
        var config = BuildCmmiConfig();
        var result = StateTransitionService.Evaluate(config, WorkItemType.ProductBacklogItem, "Proposed", "Active");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }
}
