using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Process;

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

    /// <summary>
    /// AB#369 — REVERSED. This arm previously asserted the opposite, that a casing mismatch
    /// makes a transition "not allowed".
    /// </summary>
    /// <remarks>
    /// It was characterization, not a requirement: it arrived in a bulk EPIC-004 commit
    /// ("35 tests across 5 files"), its comment only restated what the code did ("stored by
    /// exact name from config"), and no spec or doc asks for case-sensitive state matching.
    ///
    /// It was also actively harmful. ADO returns state names with inconsistent casing — the
    /// process definition says "To do" while individual work items store "To Do", both
    /// observable on one board — so the behaviour this pinned made `twig state Done` reject a
    /// legal transition with "Transition from 'To Do' to 'Done' is not allowed." That message
    /// names a real ADO concept, so it reads as a board rule the user must satisfy rather
    /// than a twig defect, and it cost a real debugging detour on AB#79.
    ///
    /// Every other state comparison in the codebase is already OrdinalIgnoreCase
    /// (StateTransitionWorkflow.Validate/ExecuteAsync, StateResolver.ResolveByName), so this
    /// was the lone inconsistency rather than a deliberate design.
    /// </remarks>
    [Fact]
    public void Evaluate_IsCaseInsensitive_OnStateNames()
    {
        var config = ProcessConfigBuilder.Basic();

        var result = StateTransitionService.Evaluate(config, WorkItemType.Issue, "to do", "doing");

        result.Kind.ShouldBe(TransitionKind.Forward);
        result.IsAllowed.ShouldBeTrue(
            "a casing difference between the process definition and an item's stored state is "
            + "not a process rule violation (AB#369)");
    }

    /// <summary>
    /// The reversal above must not have made the guard permissive. A genuinely unknown state
    /// is still rejected, so "not allowed" keeps meaning something.
    /// </summary>
    [Fact]
    public void Evaluate_StillRejectsAGenuinelyUnknownState_AfterTheCasingFix()
    {
        var config = ProcessConfigBuilder.Basic();

        var result = StateTransitionService.Evaluate(config, WorkItemType.Issue, "to do", "shipped");

        result.Kind.ShouldBe(TransitionKind.None);
        result.IsAllowed.ShouldBeFalse();
    }
}
