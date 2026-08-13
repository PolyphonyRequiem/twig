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

    private static StateEntry[] ToStateEntriesWithCategories(params (string Name, StateCategory Category)[] entries) =>
        entries.Select(e => new StateEntry(e.Name, e.Category, null)).ToArray();

    private static ProcessTypeRecord MakeRecord(string typeName, string[] states, string[] childTypes) =>
        new()
        {
            TypeName = typeName,
            States = ToStateEntries(states),
            ValidChildTypes = childTypes,
        };

    private static ProcessTypeRecord MakeRecord(string typeName, StateEntry[] stateEntries, string[] childTypes) =>
        new()
        {
            TypeName = typeName,
            States = stateEntries,
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
    public void BasicStyle_OrdinalBackwardTransition_IsForward()
    {
        var config = BuildBasicStyle();
        config.GetTransitionKind(WorkItemType.Issue, "Doing", "To Do").ShouldBe(TransitionKind.Forward);
        config.GetTransitionKind(WorkItemType.Issue, "Done", "Doing").ShouldBe(TransitionKind.Forward);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Agile-style type hierarchy
    // ═══════════════════════════════════════════════════════════════

    private static ProcessConfiguration BuildAgileStyle() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", ToStateEntriesWithCategories(
                ("New", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), new[] { "Feature" }),
            MakeRecord("Feature", ToStateEntriesWithCategories(
                ("New", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), new[] { "User Story", "Bug" }),
            MakeRecord("User Story", ToStateEntriesWithCategories(
                ("New", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed),
                ("Removed", StateCategory.Removed)), new[] { "Task" }),
            MakeRecord("Bug", ToStateEntriesWithCategories(
                ("New", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed)), new[] { "Task" }),
            MakeRecord("Task", ToStateEntriesWithCategories(
                ("New", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), Array.Empty<string>()),
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
    public void AgileStyle_OrdinalBackwardTransition_IsForward()
    {
        var config = BuildAgileStyle();
        config.GetTransitionKind(WorkItemType.UserStory, "Active", "New").ShouldBe(TransitionKind.Forward);
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
            MakeRecord("Epic", ToStateEntriesWithCategories(
                ("New", StateCategory.Proposed), ("In Progress", StateCategory.InProgress),
                ("Done", StateCategory.Completed), ("Removed", StateCategory.Removed)), new[] { "Feature" }),
            MakeRecord("Feature", ToStateEntriesWithCategories(
                ("New", StateCategory.Proposed), ("In Progress", StateCategory.InProgress),
                ("Done", StateCategory.Completed), ("Removed", StateCategory.Removed)), new[] { "Product Backlog Item", "Bug" }),
            MakeRecord("Product Backlog Item", ToStateEntriesWithCategories(
                ("New", StateCategory.Proposed), ("Approved", StateCategory.Proposed),
                ("Committed", StateCategory.InProgress), ("Done", StateCategory.Completed),
                ("Removed", StateCategory.Removed)), new[] { "Task" }),
            MakeRecord("Bug", ToStateEntriesWithCategories(
                ("New", StateCategory.Proposed), ("Approved", StateCategory.Proposed),
                ("Committed", StateCategory.InProgress), ("Done", StateCategory.Completed),
                ("Removed", StateCategory.Removed)), new[] { "Task" }),
            MakeRecord("Task", ToStateEntriesWithCategories(
                ("To Do", StateCategory.Proposed), ("In Progress", StateCategory.InProgress),
                ("Done", StateCategory.Completed), ("Removed", StateCategory.Removed)), Array.Empty<string>()),
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
            MakeRecord("Epic", ToStateEntriesWithCategories(
                ("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed),
                ("Removed", StateCategory.Removed)), new[] { "Feature" }),
            MakeRecord("Feature", ToStateEntriesWithCategories(
                ("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed),
                ("Removed", StateCategory.Removed)), new[] { "Requirement" }),
            MakeRecord("Requirement", ToStateEntriesWithCategories(
                ("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed),
                ("Removed", StateCategory.Removed)), new[] { "Task" }),
            MakeRecord("Bug", ToStateEntriesWithCategories(
                ("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed),
                ("Removed", StateCategory.Removed)), new[] { "Task" }),
            MakeRecord("Task", ToStateEntriesWithCategories(
                ("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed),
                ("Removed", StateCategory.Removed)), Array.Empty<string>()),
            MakeRecord("Change Request", ToStateEntriesWithCategories(
                ("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed),
                ("Removed", StateCategory.Removed)), Array.Empty<string>()),
            MakeRecord("Review", ToStateEntriesWithCategories(
                ("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed),
                ("Removed", StateCategory.Removed)), Array.Empty<string>()),
            MakeRecord("Risk", ToStateEntriesWithCategories(
                ("Proposed", StateCategory.Proposed), ("Active", StateCategory.InProgress),
                ("Resolved", StateCategory.Resolved), ("Closed", StateCategory.Completed),
                ("Removed", StateCategory.Removed)), Array.Empty<string>()),
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
    public void CmmiStyle_OrdinalBackwardTransition_IsForward()
    {
        var config = BuildCmmiStyle();
        config.GetTransitionKind(WorkItemType.Requirement, "Active", "Proposed").ShouldBe(TransitionKind.Forward);
        config.GetTransitionKind(WorkItemType.Requirement, "Resolved", "Active").ShouldBe(TransitionKind.Forward);
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

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-004 Task 1: All records malformed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FromRecords_AllNullTypeNames_ReturnsEmptyConfig()
    {
        var config = ProcessConfiguration.FromRecords(new[]
        {
            new ProcessTypeRecord { TypeName = null!, States = ToStateEntries("New", "Done") },
            new ProcessTypeRecord { TypeName = null!, States = ToStateEntries("Open", "Closed") },
        });

        config.ShouldNotBeNull();
        config.TypeConfigs.ShouldBeEmpty();
    }

    [Fact]
    public void FromRecords_AllEmptyTypeNames_ReturnsEmptyConfig()
    {
        var config = ProcessConfiguration.FromRecords(new[]
        {
            new ProcessTypeRecord { TypeName = "", States = ToStateEntries("New", "Done") },
            new ProcessTypeRecord { TypeName = "", States = ToStateEntries("Open", "Closed") },
            new ProcessTypeRecord { TypeName = "   ", States = ToStateEntries("Active", "Resolved") },
        });

        config.ShouldNotBeNull();
        config.TypeConfigs.ShouldBeEmpty();
    }

    [Fact]
    public void FromRecords_MixOfMalformedAndValid_OnlyKeepsValid()
    {
        var config = ProcessConfiguration.FromRecords(new[]
        {
            new ProcessTypeRecord { TypeName = null!, States = ToStateEntries("New", "Done") },
            new ProcessTypeRecord { TypeName = "", States = ToStateEntries("New", "Done") },
            new ProcessTypeRecord { TypeName = "NoStates", States = Array.Empty<StateEntry>() },
            MakeRecord("Bug", new[] { "New", "Active", "Closed" }, Array.Empty<string>()),
        });

        config.TypeConfigs.Count.ShouldBe(1);
        config.TypeConfigs.Keys.ShouldContain(WorkItemType.Bug);
    }

    [Fact]
    public void EmptyConfig_GetTransitionKind_ReturnsNull()
    {
        var config = ProcessConfiguration.FromRecords(Array.Empty<ProcessTypeRecord>());
        config.GetTransitionKind(WorkItemType.Bug, "New", "Active").ShouldBeNull();
    }

    [Fact]
    public void EmptyConfig_GetAllowedChildTypes_ReturnsEmpty()
    {
        var config = ProcessConfiguration.FromRecords(Array.Empty<ProcessTypeRecord>());
        config.GetAllowedChildTypes(WorkItemType.Bug).ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-004 Task 2: Unknown (custom) work item type
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FromRecords_CustomWorkItemType_IsIncluded()
    {
        // Custom types not in the known set should be accepted by WorkItemType.Parse
        // and included in the ProcessConfiguration — this supports ADO custom process templates.
        var config = ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("CustomWorkItemType", new[] { "Draft", "InReview", "Published" }, Array.Empty<string>()),
        });

        var customType = WorkItemType.Parse("CustomWorkItemType").Value;
        config.TypeConfigs.ShouldContainKey(customType);
        config.TypeConfigs[customType].States.ShouldBe(new[] { "Draft", "InReview", "Published" });
    }

    [Fact]
    public void FromRecords_CustomType_TransitionRulesGenerated()
    {
        var config = ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("CustomWorkItemType", new[] { "Draft", "InReview", "Published" }, Array.Empty<string>()),
        });

        var customType = WorkItemType.Parse("CustomWorkItemType").Value;
        config.GetTransitionKind(customType, "Draft", "InReview").ShouldBe(TransitionKind.Forward);
        config.GetTransitionKind(customType, "InReview", "Draft").ShouldBe(TransitionKind.Forward);
    }

    [Fact]
    public void FromRecords_CustomType_WithChildren()
    {
        var config = ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("CustomParent", new[] { "Open", "Closed" }, new[] { "CustomChild" }),
            MakeRecord("CustomChild", new[] { "Open", "Closed" }, Array.Empty<string>()),
        });

        var parentType = WorkItemType.Parse("CustomParent").Value;
        var childType = WorkItemType.Parse("CustomChild").Value;
        config.GetAllowedChildTypes(parentType).ShouldContain(childType);
    }

    // ═══════════════════════════════════════════════════════════════
    //  AB#2116: StateCategory.Removed drives Cut classification
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NonRemovedStateName_WithRemovedCategory_IsClassifiedAsCut()
    {
        // A custom process may use a state name other than "Removed"
        // (e.g. "Cancelled") but categorize it as StateCategory.Removed.
        // The transition to that state must still be classified as Cut.
        var config = ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", ToStateEntriesWithCategories(
                ("Open", StateCategory.Proposed),
                ("Active", StateCategory.InProgress),
                ("Cancelled", StateCategory.Removed)), Array.Empty<string>()),
        });

        config.GetTransitionKind(WorkItemType.Epic, "Open", "Cancelled").ShouldBe(TransitionKind.Cut);
        config.GetTransitionKind(WorkItemType.Epic, "Active", "Cancelled").ShouldBe(TransitionKind.Cut);
        // Transitions between non-removed states remain Forward.
        config.GetTransitionKind(WorkItemType.Epic, "Open", "Active").ShouldBe(TransitionKind.Forward);
    }

    // ═══════════════════════════════════════════════════════════════
    //  AB#369 — state and type lookups must be case-INSENSITIVE
    //
    //  ADO returns state names with inconsistent casing: the process definition says
    //  "To do", individual work items store "To Do", and both are observable on the same
    //  board at once. TransitionRules was keyed by a (string, string) ValueTuple, whose
    //  DEFAULT comparer is ordinal and case-sensitive, so GetTransitionKind missed and
    //  returned null — which Evaluate maps to IsAllowed = false, reported to the user as
    //  "Transition from 'To Do' to 'Done' is not allowed."
    //
    //  That message names a real ADO concept, so it reads as a board rule the user must
    //  satisfy rather than a twig defect. It cost a real debugging detour on AB#79.
    // ═══════════════════════════════════════════════════════════════

    private static ProcessConfiguration BuildLowercaseDefinition() =>
        ProcessConfiguration.FromRecords(new[]
        {
            // Exactly what the Twig board's Feature process returns: lowercase 'd'.
            MakeRecord("Feature", new[] { "To do", "Doing", "Done" }, Array.Empty<string>()),
        });

    /// <summary>
    /// The reported repro, at the unit level: the process defines "To do", the item stores
    /// "To Do", and the move to Done is legal.
    /// </summary>
    [Fact]
    public void GetTransitionKind_ResolvesWhenStoredStateCasingDiffersFromDefinition()
    {
        var config = BuildLowercaseDefinition();

        config.GetTransitionKind(WorkItemType.Feature, "To Do", "Done")
            .ShouldBe(TransitionKind.Forward,
                "the process defines 'To do' and the item stores 'To Do' — a casing difference "
                + "is not a process rule violation (AB#369)");
    }

    [Theory]
    [InlineData("To Do", "Done")]
    [InlineData("TO DO", "DONE")]
    [InlineData("to do", "done")]
    [InlineData("To do", "Done")]   // exact case — the arm that passed before the fix
    public void GetTransitionKind_IgnoresCasingOnBothEnds(string from, string to)
    {
        BuildLowercaseDefinition()
            .GetTransitionKind(WorkItemType.Feature, from, to)
            .ShouldBe(TransitionKind.Forward);
    }

    /// <summary>
    /// The guard must not become vacuous. A comparer that accepted everything would satisfy
    /// every arm above, so an unknown state must still return null.
    /// </summary>
    [Theory]
    [InlineData("To do", "Shipped")]      // unknown target
    [InlineData("Archived", "Done")]      // unknown source
    [InlineData("To do", "To do")]        // no self-transition rule is generated
    public void GetTransitionKind_StillReturnsNullForAStateThatDoesNotExist(string from, string to)
    {
        BuildLowercaseDefinition()
            .GetTransitionKind(WorkItemType.Feature, from, to)
            .ShouldBeNull("case-insensitivity must not make the lookup accept anything");
    }

    /// <summary>
    /// Cut classification must survive the comparer change — a removed-category target is
    /// still a Cut when reached under different casing.
    /// </summary>
    [Fact]
    public void GetTransitionKind_PreservesCutClassificationAcrossCasing()
    {
        var config = ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord(
                "Epic",
                ToStateEntriesWithCategories(
                    ("Open", StateCategory.Proposed),
                    ("Active", StateCategory.InProgress),
                    ("Cancelled", StateCategory.Removed)),
                Array.Empty<string>()),
        });

        config.GetTransitionKind(WorkItemType.Epic, "OPEN", "cancelled").ShouldBe(TransitionKind.Cut);
        config.GetTransitionKind(WorkItemType.Epic, "open", "ACTIVE").ShouldBe(TransitionKind.Forward);
    }

    /// <summary>
    /// The same defect one level up, found by auditing rather than reported.
    /// <c>WorkItemType.Parse</c> normalises casing for well-known types only and explicitly
    /// preserves it for CUSTOM ones, so a custom type stored and looked up under different
    /// casing missed <c>TypeConfigs</c> entirely — surfacing as ProcessConfigNotFound.
    /// </summary>
    [Fact]
    public void GetTransitionKind_ResolvesACustomTypeUnderDifferentCasing()
    {
        var config = ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Wayfinder Task", new[] { "To do", "Done" }, Array.Empty<string>()),
        });

        var lookedUpDifferently = WorkItemType.Parse("wayfinder task").Value;

        config.GetTransitionKind(lookedUpDifferently, "To do", "Done")
            .ShouldBe(TransitionKind.Forward,
                "custom types keep their original casing, so TypeConfigs must not be "
                + "case-sensitive either (AB#369)");
    }

    [Fact]
    public void TypeConfigs_LookupIsCaseInsensitiveForCustomTypes()
    {
        var config = ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Wayfinder Task", new[] { "To do", "Done" }, Array.Empty<string>()),
        });

        config.TypeConfigs.ContainsKey(WorkItemType.Parse("WAYFINDER TASK").Value).ShouldBeTrue();
        config.TypeConfigs.ContainsKey(WorkItemType.Parse("Some Other Type").Value).ShouldBeFalse(
            "an unknown type must still miss — the comparer must not accept anything");
    }
}
