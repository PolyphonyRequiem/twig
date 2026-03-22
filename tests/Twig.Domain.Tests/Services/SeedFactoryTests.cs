using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class SeedFactoryTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Valid parent/child — Agile-style
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_Agile_TaskUnderUserStory_Succeeds()
    {
        var config = ProcessConfigBuilder.Agile();
        var parent = new WorkItemBuilder(10, "Parent 10").AsUserStory().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

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
        var config = ProcessConfigBuilder.Agile();
        var parent = new WorkItemBuilder(5, "Parent 5").AsEpic().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

        var result = SeedFactory.Create("New feature", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Feature);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Valid parent/child — Scrum-style
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_Scrum_TaskUnderPBI_Succeeds()
    {
        var config = ProcessConfigBuilder.Scrum();
        var parent = new WorkItemBuilder(20, "Parent 20").AsProductBacklogItem().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

        var result = SeedFactory.Create("PBI child task", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Task);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Valid parent/child — Basic-style
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_Basic_IssueUnderEpic_Succeeds()
    {
        var config = ProcessConfigBuilder.Basic();
        var parent = new WorkItemBuilder(1, "Parent 1").AsEpic().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

        var result = SeedFactory.Create("New issue", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Type.ShouldBe(WorkItemType.Issue);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Valid parent/child — CMMI-style
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_CMMI_RequirementUnderFeature_Succeeds()
    {
        var config = ProcessConfigBuilder.Cmmi();
        var parent = new WorkItemBuilder(3, "Parent 3").AsFeature().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

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
        var config = ProcessConfigBuilder.Agile();
        var parent = new WorkItemBuilder(10, "Parent 10").AsTask().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

        // Task has no allowed children
        var result = SeedFactory.Create("Bad seed", parent, config);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("does not allow child items");
    }

    [Fact]
    public void Create_Agile_InvalidTypeOverride_Fails()
    {
        var config = ProcessConfigBuilder.Agile();
        var parent = new WorkItemBuilder(10, "Parent 10").AsFeature().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

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
        var config = ProcessConfigBuilder.Agile();
        var parent = new WorkItemBuilder(10, "Parent 10").AsFeature().WithAreaPath(@"Project\Team").WithIterationPath(@"Project\Sprint1").Build();

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
        var config = ProcessConfigBuilder.Agile();

        var result = SeedFactory.Create("Orphan seed", null, config);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Explicit type is required");
    }

    [Fact]
    public void Create_NoParent_WithTypeOverride_Succeeds()
    {
        var config = ProcessConfigBuilder.Agile();

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
        var config = ProcessConfigBuilder.Agile();
        var parent = new WorkItemBuilder(10, "Parent story").AsUserStory().WithAreaPath(@"Project\TeamA").WithIterationPath(@"Project\Sprint1").Build();

        var result = SeedFactory.Create("Child task", parent, config);

        result.IsSuccess.ShouldBeTrue();
        result.Value.AreaPath.Value.ShouldBe("Project\\TeamA");
        result.Value.IterationPath.Value.ShouldBe("Project\\Sprint1");
    }

    [Fact]
    public void Create_NoParent_DoesNotInheritPaths()
    {
        var config = ProcessConfigBuilder.Agile();

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
        var config = ProcessConfigBuilder.Agile();

        var result = SeedFactory.Create(title!, null, config, WorkItemType.Epic);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("title cannot be empty");
    }

}
