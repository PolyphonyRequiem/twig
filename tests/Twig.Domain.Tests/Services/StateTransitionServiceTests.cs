using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Services;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class StateTransitionServiceTests
{
    private static StateEntry[] ToStateEntries(params string[] names) =>
        names.Select(n => new StateEntry(n, StateCategory.Unknown, null)).ToArray();

    // ═══════════════════════════════════════════════════════════════
    //  Basic-style
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Basic_Forward_IsAllowed()
    {
        var config = ProcessConfigBuilder.Basic();
        var result = StateTransitionService.Evaluate(config, WorkItemType.Issue, "To Do", "Doing");

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void Basic_OrdinalBackward_IsForward()
    {
        var config = ProcessConfigBuilder.Basic();
        var result = StateTransitionService.Evaluate(config, WorkItemType.Issue, "Doing", "To Do");

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void Basic_Invalid_UnknownStates()
    {
        var config = ProcessConfigBuilder.Basic();
        var result = StateTransitionService.Evaluate(config, WorkItemType.Issue, "To Do", "Nonexistent");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Basic_Invalid_UnknownType()
    {
        var config = ProcessConfigBuilder.Basic();
        var result = StateTransitionService.Evaluate(config, WorkItemType.UserStory, "To Do", "Doing");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Agile-style
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("New", "Active")]
    [InlineData("Active", "Resolved")]
    [InlineData("Resolved", "Closed")]
    public void Agile_UserStory_Forward(string from, string to)
    {
        var config = ProcessConfigBuilder.Agile();
        var result = StateTransitionService.Evaluate(config, WorkItemType.UserStory, from, to);

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Active", "New")]
    [InlineData("Resolved", "Active")]
    [InlineData("Closed", "Resolved")]
    public void Agile_UserStory_OrdinalBackward_IsForward(string from, string to)
    {
        var config = ProcessConfigBuilder.Agile();
        var result = StateTransitionService.Evaluate(config, WorkItemType.UserStory, from, to);

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("New", "Removed")]
    [InlineData("Active", "Removed")]
    [InlineData("Resolved", "Removed")]
    [InlineData("Closed", "Removed")]
    public void Agile_UserStory_Cut(string from, string to)
    {
        var config = ProcessConfigBuilder.Agile();
        var result = StateTransitionService.Evaluate(config, WorkItemType.UserStory, from, to);

        result.Kind.ShouldBe(TransitionKind.Cut);
        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void Agile_Invalid_SameState()
    {
        var config = ProcessConfigBuilder.Agile();
        var result = StateTransitionService.Evaluate(config, WorkItemType.UserStory, "Active", "Active");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scrum-style
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("New", "Approved")]
    [InlineData("Approved", "Committed")]
    [InlineData("Committed", "Done")]
    public void Scrum_PBI_Forward(string from, string to)
    {
        var config = ProcessConfigBuilder.Scrum();
        var result = StateTransitionService.Evaluate(config, WorkItemType.ProductBacklogItem, from, to);

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Committed", "Approved")]
    [InlineData("Approved", "New")]
    public void Scrum_PBI_OrdinalBackward_IsForward(string from, string to)
    {
        var config = ProcessConfigBuilder.Scrum();
        var result = StateTransitionService.Evaluate(config, WorkItemType.ProductBacklogItem, from, to);

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("New", "Removed")]
    [InlineData("Committed", "Removed")]
    public void Scrum_PBI_Cut(string from, string to)
    {
        var config = ProcessConfigBuilder.Scrum();
        var result = StateTransitionService.Evaluate(config, WorkItemType.ProductBacklogItem, from, to);

        result.Kind.ShouldBe(TransitionKind.Cut);
        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void Scrum_Invalid_UnknownTransition()
    {
        var config = ProcessConfigBuilder.Scrum();
        var result = StateTransitionService.Evaluate(config, WorkItemType.ProductBacklogItem, "New", "Bogus");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  CMMI-style
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Proposed", "Active")]
    [InlineData("Active", "Resolved")]
    [InlineData("Resolved", "Closed")]
    public void CMMI_Requirement_Forward(string from, string to)
    {
        var config = ProcessConfigBuilder.Cmmi();
        var result = StateTransitionService.Evaluate(config, WorkItemType.Requirement, from, to);

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Active", "Proposed")]
    [InlineData("Resolved", "Active")]
    public void CMMI_Requirement_OrdinalBackward_IsForward(string from, string to)
    {
        var config = ProcessConfigBuilder.Cmmi();
        var result = StateTransitionService.Evaluate(config, WorkItemType.Requirement, from, to);

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Proposed", "Removed")]
    [InlineData("Active", "Removed")]
    [InlineData("Resolved", "Removed")]
    [InlineData("Closed", "Removed")]
    public void CMMI_Requirement_Cut(string from, string to)
    {
        var config = ProcessConfigBuilder.Cmmi();
        var result = StateTransitionService.Evaluate(config, WorkItemType.Requirement, from, to);

        result.Kind.ShouldBe(TransitionKind.Cut);
        result.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void CMMI_Invalid_UnknownType()
    {
        var config = ProcessConfigBuilder.Cmmi();
        var result = StateTransitionService.Evaluate(config, WorkItemType.ProductBacklogItem, "Proposed", "Active");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-004 Task 3: Unknown type — "type not configured"
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Evaluate_UnknownType_ReturnsNotAllowed_WithNoneKind()
    {
        // When WorkItemType is not in config, Evaluate should return IsAllowed=false
        // with Kind=None, distinguishing "type not configured" from "transition blocked".
        var config = ProcessConfigBuilder.Basic();
        var customType = WorkItemType.Parse("CustomWorkItemType").Value;

        var result = StateTransitionService.Evaluate(config, customType, "To Do", "Doing");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_EmptyConfig_AnyType_ReturnsNotAllowed()
    {
        var config = ProcessConfiguration.FromRecords(Array.Empty<ProcessTypeRecord>());

        var result = StateTransitionService.Evaluate(config, WorkItemType.Bug, "New", "Active");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_AllRecordsMalformed_AnyType_ReturnsNotAllowed()
    {
        // Config built from all-malformed records is empty — transitions should be not-allowed.
        var config = ProcessConfiguration.FromRecords(new[]
        {
            new ProcessTypeRecord { TypeName = "", States = ToStateEntries("New", "Done") },
            new ProcessTypeRecord { TypeName = null!, States = ToStateEntries("Open", "Closed") },
        });

        var result = StateTransitionService.Evaluate(config, WorkItemType.Bug, "New", "Active");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-004 Task 4: Unknown state — "state not in config"
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Evaluate_UnknownFromState_ReturnsNotAllowed()
    {
        // Valid type but fromState not in config → transition not found → IsAllowed=false.
        var config = ProcessConfigBuilder.Basic();

        var result = StateTransitionService.Evaluate(config, WorkItemType.Issue, "NonexistentState", "Doing");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_UnknownToState_ReturnsNotAllowed()
    {
        var config = ProcessConfigBuilder.Basic();

        var result = StateTransitionService.Evaluate(config, WorkItemType.Issue, "To Do", "NonexistentState");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_BothStatesUnknown_ReturnsNotAllowed()
    {
        var config = ProcessConfigBuilder.Basic();

        var result = StateTransitionService.Evaluate(config, WorkItemType.Issue, "FakeFrom", "FakeTo");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_CaseSensitiveStateLookup_ReturnsNotAllowed_ForWrongCase()
    {
        // State transitions are stored by exact name from config.
        // "to do" (lowercase) doesn't match "To Do" (title case).
        var config = ProcessConfigBuilder.Basic();

        var result = StateTransitionService.Evaluate(config, WorkItemType.Issue, "to do", "doing");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }
}
