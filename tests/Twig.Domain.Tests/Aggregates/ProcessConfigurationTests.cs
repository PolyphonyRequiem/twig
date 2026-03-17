using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Aggregates;

public class ProcessConfigurationTests
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
    //  Basic-style type hierarchy
    // ═══════════════════════════════════════════════════════════════

    private static ProcessConfiguration BuildBasicStyle() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", new[] { "To Do", "Doing", "Done" }, new[] { "Issue" }),
            MakeRecord("Issue", new[] { "To Do", "Doing", "Done" }, new[] { "Task" }),
            MakeRecord("Task", new[] { "To Do", "Doing", "Done" }, Array.Empty<string>()),
        });

    [Fact]
    public void BasicStyle_HasExpectedTypes()
    {
        var config = BuildBasicStyle();
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Epic);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Issue);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Task);
    }

    [Theory]
    [InlineData("Epic")]
    [InlineData("Issue")]
    [InlineData("Task")]
    public void BasicStyle_AllTypes_HaveThreeStates(string typeName)
    {
        var config = BuildBasicStyle();
        var wit = WorkItemType.Parse(typeName).Value;
        var states = config.TypeConfigs[wit].States;
        states.Count.ShouldBe(3);
        states.ShouldBe(new[] { "To Do", "Doing", "Done" });
    }

    [Fact]
    public void BasicStyle_Epic_ChildTypes()
    {
        var config = BuildBasicStyle();
        config.GetAllowedChildTypes(WorkItemType.Epic).ShouldBe(new[] { WorkItemType.Issue });
    }

    [Fact]
    public void BasicStyle_Issue_ChildTypes()
    {
        var config = BuildBasicStyle();
        config.GetAllowedChildTypes(WorkItemType.Issue).ShouldBe(new[] { WorkItemType.Task });
    }

    [Fact]
    public void BasicStyle_Task_NoChildren()
    {
        var config = BuildBasicStyle();
        config.GetAllowedChildTypes(WorkItemType.Task).ShouldBeEmpty();
    }

    [Fact]
    public void BasicStyle_ForwardTransition()
    {
        var config = BuildBasicStyle();
        config.GetTransitionKind(WorkItemType.Issue, "To Do", "Doing").ShouldBe(TransitionKind.Forward);
        config.GetTransitionKind(WorkItemType.Issue, "Doing", "Done").ShouldBe(TransitionKind.Forward);
    }

    [Fact]
    public void BasicStyle_BackwardTransition()
    {
        var config = BuildBasicStyle();
        config.GetTransitionKind(WorkItemType.Issue, "Doing", "To Do").ShouldBe(TransitionKind.Backward);
        config.GetTransitionKind(WorkItemType.Issue, "Done", "Doing").ShouldBe(TransitionKind.Backward);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Agile-style type hierarchy
    // ═══════════════════════════════════════════════════════════════

    private static ProcessConfiguration BuildAgileStyle() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", new[] { "New", "Active", "Closed", "Removed" }, new[] { "Feature" }),
            MakeRecord("Feature", new[] { "New", "Active", "Closed", "Removed" }, new[] { "User Story", "Bug" }),
            MakeRecord("User Story", new[] { "New", "Active", "Resolved", "Closed", "Removed" }, new[] { "Task" }),
            MakeRecord("Bug", new[] { "New", "Active", "Resolved", "Closed" }, new[] { "Task" }),
            MakeRecord("Task", new[] { "New", "Active", "Closed", "Removed" }, Array.Empty<string>()),
        });

    [Fact]
    public void AgileStyle_HasExpectedTypes()
    {
        var config = BuildAgileStyle();
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Epic);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Feature);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.UserStory);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Bug);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Task);
    }

    [Fact]
    public void AgileStyle_UserStory_States()
    {
        var config = BuildAgileStyle();
        config.TypeConfigs[WorkItemType.UserStory].States
            .ShouldBe(new[] { "New", "Active", "Resolved", "Closed", "Removed" });
    }

    [Fact]
    public void AgileStyle_Bug_States()
    {
        var config = BuildAgileStyle();
        config.TypeConfigs[WorkItemType.Bug].States
            .ShouldBe(new[] { "New", "Active", "Resolved", "Closed" });
    }

    [Fact]
    public void AgileStyle_Feature_States()
    {
        var config = BuildAgileStyle();
        config.TypeConfigs[WorkItemType.Feature].States
            .ShouldBe(new[] { "New", "Active", "Closed", "Removed" });
    }

    [Fact]
    public void AgileStyle_Epic_States()
    {
        var config = BuildAgileStyle();
        config.TypeConfigs[WorkItemType.Epic].States
            .ShouldBe(new[] { "New", "Active", "Closed", "Removed" });
    }

    [Fact]
    public void AgileStyle_Task_States()
    {
        var config = BuildAgileStyle();
        config.TypeConfigs[WorkItemType.Task].States
            .ShouldBe(new[] { "New", "Active", "Closed", "Removed" });
    }

    [Fact]
    public void AgileStyle_Epic_ChildTypes()
    {
        var config = BuildAgileStyle();
        config.GetAllowedChildTypes(WorkItemType.Epic).ShouldBe(new[] { WorkItemType.Feature });
    }

    [Fact]
    public void AgileStyle_Feature_ChildTypes()
    {
        var config = BuildAgileStyle();
        config.GetAllowedChildTypes(WorkItemType.Feature)
            .ShouldBe(new[] { WorkItemType.UserStory, WorkItemType.Bug });
    }

    [Fact]
    public void AgileStyle_UserStory_ChildTypes()
    {
        var config = BuildAgileStyle();
        config.GetAllowedChildTypes(WorkItemType.UserStory).ShouldBe(new[] { WorkItemType.Task });
    }

    [Fact]
    public void AgileStyle_ForwardTransition()
    {
        var config = BuildAgileStyle();
        config.GetTransitionKind(WorkItemType.UserStory, "New", "Active").ShouldBe(TransitionKind.Forward);
        config.GetTransitionKind(WorkItemType.UserStory, "Active", "Resolved").ShouldBe(TransitionKind.Forward);
    }

    [Fact]
    public void AgileStyle_BackwardTransition()
    {
        var config = BuildAgileStyle();
        config.GetTransitionKind(WorkItemType.UserStory, "Active", "New").ShouldBe(TransitionKind.Backward);
    }

    [Fact]
    public void AgileStyle_CutTransition_ToRemoved()
    {
        var config = BuildAgileStyle();
        config.GetTransitionKind(WorkItemType.UserStory, "New", "Removed").ShouldBe(TransitionKind.Cut);
        config.GetTransitionKind(WorkItemType.UserStory, "Active", "Removed").ShouldBe(TransitionKind.Cut);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scrum-style type hierarchy
    // ═══════════════════════════════════════════════════════════════

    private static ProcessConfiguration BuildScrumStyle() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", new[] { "New", "In Progress", "Done", "Removed" }, new[] { "Feature" }),
            MakeRecord("Feature", new[] { "New", "In Progress", "Done", "Removed" }, new[] { "Product Backlog Item", "Bug" }),
            MakeRecord("Product Backlog Item", new[] { "New", "Approved", "Committed", "Done", "Removed" }, new[] { "Task" }),
            MakeRecord("Bug", new[] { "New", "Approved", "Committed", "Done", "Removed" }, new[] { "Task" }),
            MakeRecord("Task", new[] { "To Do", "In Progress", "Done", "Removed" }, Array.Empty<string>()),
        });

    [Fact]
    public void ScrumStyle_HasExpectedTypes()
    {
        var config = BuildScrumStyle();
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Epic);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Feature);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.ProductBacklogItem);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Bug);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Task);
    }

    [Fact]
    public void ScrumStyle_PBI_States()
    {
        var config = BuildScrumStyle();
        config.TypeConfigs[WorkItemType.ProductBacklogItem].States
            .ShouldBe(new[] { "New", "Approved", "Committed", "Done", "Removed" });
    }

    [Fact]
    public void ScrumStyle_Bug_States()
    {
        var config = BuildScrumStyle();
        config.TypeConfigs[WorkItemType.Bug].States
            .ShouldBe(new[] { "New", "Approved", "Committed", "Done", "Removed" });
    }

    [Fact]
    public void ScrumStyle_Feature_States()
    {
        var config = BuildScrumStyle();
        config.TypeConfigs[WorkItemType.Feature].States
            .ShouldBe(new[] { "New", "In Progress", "Done", "Removed" });
    }

    [Fact]
    public void ScrumStyle_Task_States()
    {
        var config = BuildScrumStyle();
        config.TypeConfigs[WorkItemType.Task].States
            .ShouldBe(new[] { "To Do", "In Progress", "Done", "Removed" });
    }

    [Fact]
    public void ScrumStyle_Feature_ChildTypes()
    {
        var config = BuildScrumStyle();
        config.GetAllowedChildTypes(WorkItemType.Feature)
            .ShouldBe(new[] { WorkItemType.ProductBacklogItem, WorkItemType.Bug });
    }

    [Fact]
    public void ScrumStyle_PBI_ForwardTransition()
    {
        var config = BuildScrumStyle();
        config.GetTransitionKind(WorkItemType.ProductBacklogItem, "New", "Approved").ShouldBe(TransitionKind.Forward);
        config.GetTransitionKind(WorkItemType.ProductBacklogItem, "Approved", "Committed").ShouldBe(TransitionKind.Forward);
        config.GetTransitionKind(WorkItemType.ProductBacklogItem, "Committed", "Done").ShouldBe(TransitionKind.Forward);
    }

    [Fact]
    public void ScrumStyle_PBI_CutTransition()
    {
        var config = BuildScrumStyle();
        config.GetTransitionKind(WorkItemType.ProductBacklogItem, "New", "Removed").ShouldBe(TransitionKind.Cut);
    }

    [Fact]
    public void ScrumStyle_Task_ForwardTransition()
    {
        var config = BuildScrumStyle();
        config.GetTransitionKind(WorkItemType.Task, "To Do", "In Progress").ShouldBe(TransitionKind.Forward);
        config.GetTransitionKind(WorkItemType.Task, "In Progress", "Done").ShouldBe(TransitionKind.Forward);
    }

    // ═══════════════════════════════════════════════════════════════
    //  CMMI-style type hierarchy
    // ═══════════════════════════════════════════════════════════════

    private static ProcessConfiguration BuildCmmiStyle() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, new[] { "Feature" }),
            MakeRecord("Feature", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, new[] { "Requirement" }),
            MakeRecord("Requirement", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, new[] { "Task" }),
            MakeRecord("Bug", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, new[] { "Task" }),
            MakeRecord("Task", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, Array.Empty<string>()),
            MakeRecord("Change Request", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, Array.Empty<string>()),
            MakeRecord("Review", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, Array.Empty<string>()),
            MakeRecord("Risk", new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" }, Array.Empty<string>()),
        });

    [Fact]
    public void CmmiStyle_HasExpectedTypes()
    {
        var config = BuildCmmiStyle();
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Epic);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Feature);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Requirement);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Bug);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Task);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.ChangeRequest);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Review);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Risk);
    }

    [Theory]
    [InlineData("Epic")]
    [InlineData("Feature")]
    [InlineData("Requirement")]
    [InlineData("Bug")]
    [InlineData("Task")]
    [InlineData("Change Request")]
    [InlineData("Review")]
    [InlineData("Risk")]
    public void CmmiStyle_AllTypes_HaveFiveStates(string typeName)
    {
        var config = BuildCmmiStyle();
        var wit = WorkItemType.Parse(typeName).Value;
        var states = config.TypeConfigs[wit].States;
        states.Count.ShouldBe(5);
        states.ShouldBe(new[] { "Proposed", "Active", "Resolved", "Closed", "Removed" });
    }

    [Fact]
    public void CmmiStyle_Feature_ChildTypes()
    {
        var config = BuildCmmiStyle();
        config.GetAllowedChildTypes(WorkItemType.Feature).ShouldBe(new[] { WorkItemType.Requirement });
    }

    [Fact]
    public void CmmiStyle_ForwardTransition()
    {
        var config = BuildCmmiStyle();
        config.GetTransitionKind(WorkItemType.Requirement, "Proposed", "Active").ShouldBe(TransitionKind.Forward);
        config.GetTransitionKind(WorkItemType.Requirement, "Active", "Resolved").ShouldBe(TransitionKind.Forward);
        config.GetTransitionKind(WorkItemType.Requirement, "Resolved", "Closed").ShouldBe(TransitionKind.Forward);
    }

    [Fact]
    public void CmmiStyle_BackwardTransition()
    {
        var config = BuildCmmiStyle();
        config.GetTransitionKind(WorkItemType.Requirement, "Active", "Proposed").ShouldBe(TransitionKind.Backward);
        config.GetTransitionKind(WorkItemType.Requirement, "Resolved", "Active").ShouldBe(TransitionKind.Backward);
    }

    [Fact]
    public void CmmiStyle_CutTransition()
    {
        var config = BuildCmmiStyle();
        config.GetTransitionKind(WorkItemType.Requirement, "Active", "Removed").ShouldBe(TransitionKind.Cut);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GetTransitionKind_UnknownType_ReturnsNull()
    {
        var config = BuildBasicStyle();
        config.GetTransitionKind(WorkItemType.UserStory, "To Do", "Done").ShouldBeNull();
    }

    [Fact]
    public void GetTransitionKind_UnknownStates_ReturnsNull()
    {
        var config = BuildBasicStyle();
        config.GetTransitionKind(WorkItemType.Issue, "Nonexistent", "Done").ShouldBeNull();
    }

    [Fact]
    public void GetAllowedChildTypes_UnknownType_ReturnsEmpty()
    {
        var config = BuildBasicStyle();
        config.GetAllowedChildTypes(WorkItemType.UserStory).ShouldBeEmpty();
    }

    [Fact]
    public void FromRecords_EmptyRecords_ReturnsEmptyConfig()
    {
        var config = ProcessConfiguration.FromRecords(Array.Empty<ProcessTypeRecord>());
        config.TypeConfigs.ShouldBeEmpty();
    }

    [Fact]
    public void FromRecords_SkipsEmptyTypeName()
    {
        var config = ProcessConfiguration.FromRecords(new[]
        {
            new ProcessTypeRecord
            {
                TypeName = "",
                States = ToStateEntries("New", "Done"),
            },
        });
        config.TypeConfigs.ShouldBeEmpty();
    }

    [Fact]
    public void FromRecords_SkipsRecordWithNoStates()
    {
        var config = ProcessConfiguration.FromRecords(new[]
        {
            new ProcessTypeRecord
            {
                TypeName = "EmptyType",
                States = Array.Empty<StateEntry>(),
            },
        });
        config.TypeConfigs.ShouldNotContainKey(WorkItemType.Parse("EmptyType").Value);
    }

    [Fact]
    public void FromRecords_MultiTypeRecords()
    {
        var config = ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", new[] { "New", "Active", "Done" }, new[] { "Feature" }),
            MakeRecord("Feature", new[] { "New", "Active", "Done" }, new[] { "User Story" }),
            MakeRecord("User Story", new[] { "New", "Active", "Done" }, Array.Empty<string>()),
        });

        config.TypeConfigs.Count.ShouldBe(3);
        config.GetAllowedChildTypes(WorkItemType.Epic).ShouldBe(new[] { WorkItemType.Feature });
        config.GetAllowedChildTypes(WorkItemType.Feature).ShouldBe(new[] { WorkItemType.UserStory });
        config.GetAllowedChildTypes(WorkItemType.UserStory).ShouldBeEmpty();
    }
}
