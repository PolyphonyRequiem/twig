using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.TestKit;

public class WorkItemBuilderTests
{
    [Fact]
    public void Build_MinimalDefaults_ReturnsTaskInNewState()
    {
        var item = new WorkItemBuilder(1, "Fix login").Build();

        item.Id.ShouldBe(1);
        item.Title.ShouldBe("Fix login");
        item.Type.ShouldBe(WorkItemType.Task);
        item.State.ShouldBe("New");
        item.ParentId.ShouldBeNull();
        item.AssignedTo.ShouldBeNull();
        item.IsSeed.ShouldBeFalse();
        item.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void Build_FullyConfigured_SetsAllProperties()
    {
        var item = new WorkItemBuilder(42, "Login endpoint")
            .AsUserStory()
            .InState("Active")
            .WithParent(100)
            .AssignedTo("Alice")
            .WithIterationPath(@"Project\Sprint 1")
            .WithAreaPath("Project")
            .Build();

        item.Id.ShouldBe(42);
        item.Title.ShouldBe("Login endpoint");
        item.Type.ShouldBe(WorkItemType.UserStory);
        item.State.ShouldBe("Active");
        item.ParentId.ShouldBe(100);
        item.AssignedTo.ShouldBe("Alice");
        item.IterationPath.Value.ShouldBe(@"Project\Sprint 1");
        item.AreaPath.Value.ShouldBe("Project");
    }

    [Fact]
    public void Build_AsSeed_SetsSeedProperties()
    {
        var item = new WorkItemBuilder(-1, "Seed task")
            .AsSeed(daysOld: 5)
            .Build();

        item.IsSeed.ShouldBeTrue();
        item.SeedCreatedAt.ShouldNotBeNull();
        // Should be approximately 5 days ago
        (DateTimeOffset.UtcNow - item.SeedCreatedAt!.Value).TotalDays.ShouldBeInRange(4.9, 5.1);
    }

    [Fact]
    public void Build_Dirty_SetsDirtyFlag()
    {
        var item = new WorkItemBuilder(1, "Dirty item")
            .Dirty()
            .Build();

        item.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void Build_LastSyncedAt_SetsTimestamp()
    {
        var timestamp = new DateTimeOffset(2025, 1, 15, 10, 0, 0, TimeSpan.Zero);
        var item = new WorkItemBuilder(1, "Synced item")
            .LastSyncedAt(timestamp)
            .Build();

        item.LastSyncedAt.ShouldBe(timestamp);
    }

    [Fact]
    public void Simple_CreatesMinimalItem()
    {
        var item = WorkItemBuilder.Simple(5, "Quick task");

        item.Id.ShouldBe(5);
        item.Title.ShouldBe("Quick task");
        item.Type.ShouldBe(WorkItemType.Task);
        item.State.ShouldBe("New");
    }

    [Theory]
    [InlineData("AsEpic")]
    [InlineData("AsFeature")]
    [InlineData("AsBug")]
    [InlineData("AsProductBacklogItem")]
    [InlineData("AsRequirement")]
    [InlineData("AsIssue")]
    public void Build_TypeShortcuts_SetCorrectType(string method)
    {
        var builder = new WorkItemBuilder(1, "Test");
        var expected = method switch
        {
            "AsEpic" => WorkItemType.Epic,
            "AsFeature" => WorkItemType.Feature,
            "AsBug" => WorkItemType.Bug,
            "AsProductBacklogItem" => WorkItemType.ProductBacklogItem,
            "AsRequirement" => WorkItemType.Requirement,
            "AsIssue" => WorkItemType.Issue,
            _ => throw new ArgumentException(method),
        };

        // Use reflection to call the method
        var item = method switch
        {
            "AsEpic" => builder.AsEpic().Build(),
            "AsFeature" => builder.AsFeature().Build(),
            "AsBug" => builder.AsBug().Build(),
            "AsProductBacklogItem" => builder.AsProductBacklogItem().Build(),
            "AsRequirement" => builder.AsRequirement().Build(),
            "AsIssue" => builder.AsIssue().Build(),
            _ => throw new ArgumentException(method),
        };

        item.Type.ShouldBe(expected);
    }
}
