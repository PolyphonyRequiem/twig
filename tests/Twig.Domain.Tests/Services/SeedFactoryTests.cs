using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class SeedFactoryTests
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
    //  Valid parent/child — Agile-style
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

    [Fact]
    public void Create_Agile_TaskUnderUserStory_Succeeds()
    {
        var config = BuildAgileConfig();
        var parent = MakeParent(10, WorkItemType.UserStory);

        var result = SeedFactory.Create("New task", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Task);
        result.Value.Title.ShouldBe("New task");
        result.Value.IsSeed.ShouldBeTrue();
        result.Value.ParentId.ShouldBe(10);
    }

    [Fact]
    public void Create_Agile_FeatureUnderEpic_Succeeds()
    {
        var config = BuildAgileConfig();
        var parent = MakeParent(5, WorkItemType.Epic);

        var result = SeedFactory.Create("New feature", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Feature);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Valid parent/child — Scrum-style
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

    [Fact]
    public void Create_Scrum_TaskUnderPBI_Succeeds()
    {
        var config = BuildScrumConfig();
        var parent = MakeParent(20, WorkItemType.ProductBacklogItem);

        var result = SeedFactory.Create("PBI child task", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Task);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Valid parent/child — Basic-style
    // ═══════════════════════════════════════════════════════════════

    private static ProcessConfiguration BuildBasicConfig() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", new[] { "To Do", "Doing", "Done" }, new[] { "Issue" }),
            MakeRecord("Issue", new[] { "To Do", "Doing", "Done" }, new[] { "Task" }),
            MakeRecord("Task", new[] { "To Do", "Doing", "Done" }, Array.Empty<string>()),
        });

    [Fact]
    public void Create_Basic_IssueUnderEpic_Succeeds()
    {
        var config = BuildBasicConfig();
        var parent = MakeParent(1, WorkItemType.Epic);

        var result = SeedFactory.Create("New issue", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Issue);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Valid parent/child — CMMI-style
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

    [Fact]
    public void Create_CMMI_RequirementUnderFeature_Succeeds()
    {
        var config = BuildCmmiConfig();
        var parent = MakeParent(3, WorkItemType.Feature);

        var result = SeedFactory.Create("New requirement", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Requirement);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Invalid parent/child
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_Agile_EpicUnderTask_Fails()
    {
        var config = BuildAgileConfig();
        var parent = MakeParent(10, WorkItemType.Task);

        // Task has no allowed children
        var result = SeedFactory.Create("Bad seed", parent, config);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("does not allow child items");
    }

    [Fact]
    public void Create_Agile_InvalidTypeOverride_Fails()
    {
        var config = BuildAgileConfig();
        var parent = MakeParent(10, WorkItemType.Feature);

        // Feature allows UserStory and Bug, not Epic
        var result = SeedFactory.Create("Bad seed", parent, config, WorkItemType.Epic);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("not an allowed child");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Explicit type override
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_ExplicitTypeOverride_Succeeds()
    {
        var config = BuildAgileConfig();
        var parent = MakeParent(10, WorkItemType.Feature);

        // Feature allows UserStory and Bug — override to Bug
        var result = SeedFactory.Create("Bug seed", parent, config, WorkItemType.Bug);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Bug);
    }

    // ═══════════════════════════════════════════════════════════════
    //  No parent — requires explicit type
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_NoParent_NoTypeOverride_Fails()
    {
        var config = BuildAgileConfig();

        var result = SeedFactory.Create("Orphan seed", null, config);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Explicit type is required");
    }

    [Fact]
    public void Create_NoParent_WithTypeOverride_Succeeds()
    {
        var config = BuildAgileConfig();

        var result = SeedFactory.Create("Orphan epic", null, config, WorkItemType.Epic);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Epic);
        result.Value.ParentId.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Area/Iteration inheritance
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_InheritsAreaAndIterationFromParent()
    {
        var config = BuildAgileConfig();
        var parent = new WorkItem
        {
            Id = 10,
            Type = WorkItemType.UserStory,
            Title = "Parent story",
            State = "New",
            AreaPath = AreaPath.Parse("Project\\TeamA").Value,
            IterationPath = IterationPath.Parse("Project\\Sprint1").Value,
        };

        var result = SeedFactory.Create("Child task", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.AreaPath.Value.ShouldBe("Project\\TeamA");
        result.Value.IterationPath.Value.ShouldBe("Project\\Sprint1");
    }

    [Fact]
    public void Create_NoParent_DoesNotInheritPaths()
    {
        var config = BuildAgileConfig();

        var result = SeedFactory.Create("Orphan", null, config, WorkItemType.Epic);

        result.IsSuccess.ShouldBeTrue();
        // Default value of AreaPath/IterationPath — empty struct
        result.Value.AreaPath.Value.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty title
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_EmptyTitle_Fails(string? title)
    {
        var config = BuildAgileConfig();

        var result = SeedFactory.Create(title!, null, config, WorkItemType.Epic);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("title cannot be empty");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static WorkItem MakeParent(int id, WorkItemType type)
    {
        return new WorkItem
        {
            Id = id,
            Type = type,
            Title = $"Parent {id}",
            State = "New",
            AreaPath = AreaPath.Parse("Project\\Team").Value,
            IterationPath = IterationPath.Parse("Project\\Sprint1").Value,
        };
    }
}
