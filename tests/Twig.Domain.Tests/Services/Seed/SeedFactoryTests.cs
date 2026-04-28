using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Seed;

public class SeedFactoryTests
{
    private static SeedFactory CreateFactory()
    {
        var counter = new SeedIdCounter();
        return new SeedFactory(counter);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Valid parent/child — Agile-style
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_Agile_TaskUnderUserStory_Succeeds()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Agile();
        var parent = new WorkItemBuilder(10, "Parent 10").AsUserStory().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

        var result = factory.Create("New task", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Task);
        result.Value.Title.ShouldBe("New task");
        result.Value.IsSeed.ShouldBeTrue();
        result.Value.ParentId.ShouldBe(10);
    }

    [Fact]
    public void Create_Agile_FeatureUnderEpic_Succeeds()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Agile();
        var parent = new WorkItemBuilder(5, "Parent 5").AsEpic().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

        var result = factory.Create("New feature", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Feature);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Valid parent/child — Scrum-style
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_Scrum_TaskUnderPBI_Succeeds()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Scrum();
        var parent = new WorkItemBuilder(20, "Parent 20").AsProductBacklogItem().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

        var result = factory.Create("PBI child task", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Task);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Valid parent/child — Basic-style
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_Basic_IssueUnderEpic_Succeeds()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Basic();
        var parent = new WorkItemBuilder(1, "Parent 1").AsEpic().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

        var result = factory.Create("New issue", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Issue);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Valid parent/child — CMMI-style
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_CMMI_RequirementUnderFeature_Succeeds()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Cmmi();
        var parent = new WorkItemBuilder(3, "Parent 3").AsFeature().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

        var result = factory.Create("New requirement", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Requirement);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Invalid parent/child
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_Agile_EpicUnderTask_Fails()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Agile();
        var parent = new WorkItemBuilder(10, "Parent 10").AsTask().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

        // Task has no allowed children
        var result = factory.Create("Bad seed", parent, config);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("does not allow child items");
    }

    [Fact]
    public void Create_Agile_InvalidTypeOverride_Fails()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Agile();
        var parent = new WorkItemBuilder(10, "Parent 10").AsFeature().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

        // Feature allows UserStory and Bug, not Epic
        var result = factory.Create("Bad seed", parent, config, WorkItemType.Epic);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("not an allowed child");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Explicit type override
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_ExplicitTypeOverride_Succeeds()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Agile();
        var parent = new WorkItemBuilder(10, "Parent 10").AsFeature().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

        // Feature allows UserStory and Bug — override to Bug
        var result = factory.Create("Bug seed", parent, config, WorkItemType.Bug);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Bug);
    }

    // ═══════════════════════════════════════════════════════════════
    //  No parent — requires explicit type
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_NoParent_NoTypeOverride_Fails()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Agile();

        var result = factory.Create("Orphan seed", null, config);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Explicit type is required");
    }

    [Fact]
    public void Create_NoParent_WithTypeOverride_Succeeds()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Agile();

        var result = factory.Create("Orphan epic", null, config, WorkItemType.Epic);

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
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Agile();
        var parent = new WorkItemBuilder(10, "Parent story").AsUserStory().WithAreaPath(@"Project\TeamA").WithIterationPath(@"Project\Sprint1").Build();

        var result = factory.Create("Child task", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.AreaPath.Value.ShouldBe("Project\\TeamA");
        result.Value.IterationPath.Value.ShouldBe("Project\\Sprint1");
    }

    [Fact]
    public void Create_NoParent_DoesNotInheritPaths()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Agile();

        var result = factory.Create("Orphan", null, config, WorkItemType.Epic);

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
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Agile();

        var result = factory.Create(title!, null, config, WorkItemType.Epic);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("title cannot be empty");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Auto-assign
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_WithAssignedTo_SetsAssignedToOnSeed()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Basic();
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().WithAreaPath("Twig").WithIterationPath("Twig").Build();

        var result = factory.Create("New issue", parent, config, assignedTo: "Daniel Green");

        result.IsSuccess.ShouldBeTrue();
        result.Value.AssignedTo.ShouldBe("Daniel Green");
    }

    [Fact]
    public void Create_WithAssignedTo_SetsFieldForAdoPayload()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Basic();
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().WithAreaPath("Twig").WithIterationPath("Twig").Build();

        var result = factory.Create("New issue", parent, config, assignedTo: "Daniel Green");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Fields.ShouldContainKey("System.AssignedTo");
        result.Value.Fields["System.AssignedTo"].ShouldBe("Daniel Green");
    }

    [Fact]
    public void Create_WithNullAssignedTo_LeavesUnassigned()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Basic();
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().WithAreaPath("Twig").WithIterationPath("Twig").Build();

        var result = factory.Create("New issue", parent, config, assignedTo: null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.AssignedTo.ShouldBeNull();
        result.Value.Fields.ShouldNotContainKey("System.AssignedTo");
    }

    [Fact]
    public void Create_WithEmptyAssignedTo_LeavesUnassigned()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Basic();
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().WithAreaPath("Twig").WithIterationPath("Twig").Build();

        var result = factory.Create("New issue", parent, config, assignedTo: "  ");

        result.IsSuccess.ShouldBeTrue();
        result.Value.AssignedTo.ShouldBe("  ");
        result.Value.Fields.ShouldNotContainKey("System.AssignedTo");
    }

    [Fact]
    public void Create_NoParent_WithAssignedTo_SetsAssignedTo()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Basic();

        var result = factory.Create("Orphan epic", null, config, WorkItemType.Epic, assignedTo: "Daniel Green");

        result.IsSuccess.ShouldBeTrue();
        result.Value.AssignedTo.ShouldBe("Daniel Green");
        result.Value.Fields["System.AssignedTo"].ShouldBe("Daniel Green");
    }

    // ═══════════════════════════════════════════════════════════════
    //  CreateUnparented
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CreateUnparented_ValidArgs_CreatesSeedWithNullParent()
    {
        var factory = CreateFactory();
        var area = AreaPath.Parse("Proj\\Area").Value;
        var iter = IterationPath.Parse("Proj\\Sprint1").Value;

        var result = factory.CreateUnparented("New Epic", WorkItemType.Epic, area, iter);

        result.IsSuccess.ShouldBeTrue();
        result.Value.IsSeed.ShouldBeTrue();
        result.Value.ParentId.ShouldBeNull();
        result.Value.Type.ShouldBe(WorkItemType.Epic);
        result.Value.Title.ShouldBe("New Epic");
    }

    [Fact]
    public void CreateUnparented_SetsAreaAndIteration()
    {
        var factory = CreateFactory();
        var area = AreaPath.Parse("Proj\\Area").Value;
        var iter = IterationPath.Parse("Proj\\Sprint1").Value;

        var result = factory.CreateUnparented("My Item", WorkItemType.Issue, area, iter);

        result.IsSuccess.ShouldBeTrue();
        result.Value.AreaPath.Value.ShouldBe("Proj\\Area");
        result.Value.IterationPath.Value.ShouldBe("Proj\\Sprint1");
    }

    [Fact]
    public void CreateUnparented_WithAssignedTo_SetsAssignment()
    {
        var factory = CreateFactory();
        var area = AreaPath.Parse("Proj").Value;
        var iter = IterationPath.Parse("Proj").Value;

        var result = factory.CreateUnparented("Epic", WorkItemType.Epic, area, iter, "Daniel Green");

        result.IsSuccess.ShouldBeTrue();
        result.Value.AssignedTo.ShouldBe("Daniel Green");
        result.Value.Fields["System.AssignedTo"].ShouldBe("Daniel Green");
    }

    [Fact]
    public void CreateUnparented_WithParentId_SetsParentId()
    {
        var factory = CreateFactory();
        var area = AreaPath.Parse("Proj\\Area").Value;
        var iter = IterationPath.Parse("Proj\\Sprint1").Value;

        var result = factory.CreateUnparented("Child Task", WorkItemType.Task, area, iter, parentId: 42);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ParentId.ShouldBe(42);
    }

    [Fact]
    public void CreateUnparented_EmptyTitle_Fails()
    {
        var factory = CreateFactory();
        var area = AreaPath.Parse("Proj").Value;
        var iter = IterationPath.Parse("Proj").Value;

        var result = factory.CreateUnparented("", WorkItemType.Epic, area, iter);

        result.IsSuccess.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Seed ID generation via ISeedIdCounter
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_SeedIdsAreNegativeAndDecrementing()
    {
        var factory = CreateFactory();
        var config = ProcessConfigBuilder.Basic();
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().WithAreaPath("Twig").WithIterationPath("Twig").Build();

        var result1 = factory.Create("Seed 1", parent, config);
        var result2 = factory.Create("Seed 2", parent, config);

        result1.IsSuccess.ShouldBeTrue();
        result2.IsSuccess.ShouldBeTrue();
        result1.Value.Id.ShouldBeLessThan(0);
        result2.Value.Id.ShouldBeLessThan(result1.Value.Id);
    }

    [Fact]
    public void InitializeSeedCounter_SetsCounterBelowMinExistingId()
    {
        var factory = CreateFactory();
        factory.InitializeSeedCounter(-5);

        var config = ProcessConfigBuilder.Basic();
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().WithAreaPath("Twig").WithIterationPath("Twig").Build();

        var result = factory.Create("After init", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Id.ShouldBeLessThan(-5);
    }

    [Fact]
    public void SeparateInstances_HaveIsolatedCounters()
    {
        var factory1 = CreateFactory();
        var factory2 = CreateFactory();
        var config = ProcessConfigBuilder.Basic();
        var parent = new WorkItemBuilder(1, "Parent").AsEpic().WithAreaPath("Twig").WithIterationPath("Twig").Build();

        var result1 = factory1.Create("From factory 1", parent, config);
        var result2 = factory2.Create("From factory 2", parent, config);

        result1.IsSuccess.ShouldBeTrue();
        result2.IsSuccess.ShouldBeTrue();
        // Both start from 0 and decrement, so both get -1
        result1.Value.Id.ShouldBe(result2.Value.Id);
    }
}
