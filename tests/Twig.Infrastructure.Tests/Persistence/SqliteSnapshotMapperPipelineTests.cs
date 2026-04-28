using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests verifying that SqliteWorkItemRepository uses the snapshot→mapper pipeline
/// for all read operations. Save → read round-trips must faithfully preserve all
/// WorkItem properties through the WorkItemSnapshot intermediary.
/// </summary>
public sealed class SqliteSnapshotMapperPipelineTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkItemRepository _repo;

    public SqliteSnapshotMapperPipelineTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteWorkItemRepository(_store, new WorkItemMapper());
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task RoundTrip_AllProperties_PreservedThroughSnapshotPipeline()
    {
        var item = new WorkItemBuilder(42, "Full Round-Trip")
            .AsBug()
            .InState("Resolved")
            .WithParent(10)
            .AssignedTo("alice@example.com")
            .WithIterationPath(@"Project\Sprint 3")
            .WithAreaPath(@"Project\Backend")
            .AsSeed()
            .Build();

        item.MarkSynced(7);
        item.SetField("Microsoft.VSTS.Common.Priority", "1");
        item.SetField("System.Tags", "critical; hotfix");
        item.SetDirty();

        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(42);
        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(42);
        loaded.Title.ShouldBe("Full Round-Trip");
        loaded.Type.ShouldBe(WorkItemType.Parse("Bug").Value);
        loaded.State.ShouldBe("Resolved");
        loaded.ParentId.ShouldBe(10);
        loaded.AssignedTo.ShouldBe("alice@example.com");
        loaded.IterationPath.Value.ShouldBe(@"Project\Sprint 3");
        loaded.AreaPath.Value.ShouldBe(@"Project\Backend");
        loaded.IsSeed.ShouldBeTrue();
        loaded.Revision.ShouldBe(7);
        loaded.IsDirty.ShouldBeTrue();
        loaded.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("1");
        loaded.Fields["System.Tags"].ShouldBe("critical; hotfix");
    }

    [Fact]
    public async Task RoundTrip_NullableFields_PreservedAsNull()
    {
        var item = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Task,
            Title = "Nullable Test",
            State = "New",
            ParentId = null,
            AssignedTo = null,
            IterationPath = IterationPath.Parse(@"Project\Sprint1").Value,
            AreaPath = AreaPath.Parse(@"Project\Area").Value,
        };

        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(1);
        loaded.ShouldNotBeNull();
        loaded.ParentId.ShouldBeNull();
        loaded.AssignedTo.ShouldBeNull();
    }

    [Fact]
    public async Task RoundTrip_EmptyFields_PreservedThroughPipeline()
    {
        var item = new WorkItemBuilder(5, "No Fields").Build();

        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(5);
        loaded.ShouldNotBeNull();
        loaded.Fields.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetChildrenAsync_UsesSnapshotPipeline_ReturnsCorrectItems()
    {
        var parent = new WorkItemBuilder(1, "Parent").Build();
        var child1 = new WorkItemBuilder(2, "Child A").WithParent(1).Build();
        var child2 = new WorkItemBuilder(3, "Child B").WithParent(1).Build();

        await _repo.SaveAsync(parent);
        await _repo.SaveAsync(child1);
        await _repo.SaveAsync(child2);

        var children = await _repo.GetChildrenAsync(1);
        children.Count.ShouldBe(2);
        children.ShouldAllBe(c => c.ParentId == 1);
    }

    [Fact]
    public async Task GetByIterationAsync_UsesSnapshotPipeline()
    {
        var item = new WorkItemBuilder(1, "Sprint Item")
            .WithIterationPath(@"Project\Sprint 5")
            .Build();

        await _repo.SaveAsync(item);

        var results = await _repo.GetByIterationAsync(IterationPath.Parse(@"Project\Sprint 5").Value);
        results.Count.ShouldBe(1);
        results[0].Title.ShouldBe("Sprint Item");
    }

    [Fact]
    public async Task GetParentChainAsync_UsesSnapshotPipeline()
    {
        var root = new WorkItemBuilder(1, "Root").Build();
        var mid = new WorkItemBuilder(2, "Middle").WithParent(1).Build();
        var leaf = new WorkItemBuilder(3, "Leaf").WithParent(2).Build();

        await _repo.SaveAsync(root);
        await _repo.SaveAsync(mid);
        await _repo.SaveAsync(leaf);

        var chain = await _repo.GetParentChainAsync(3);
        chain.Count.ShouldBe(3);
        chain[0].Title.ShouldBe("Root");
        chain[1].Title.ShouldBe("Middle");
        chain[2].Title.ShouldBe("Leaf");
    }

    [Fact]
    public async Task FindByPatternAsync_UsesSnapshotPipeline()
    {
        var item = new WorkItemBuilder(1, "Searchable Widget").Build();
        await _repo.SaveAsync(item);

        var results = await _repo.FindByPatternAsync("Widget");
        results.Count.ShouldBe(1);
        results[0].Title.ShouldBe("Searchable Widget");
    }

    [Fact]
    public async Task GetDirtyItemsAsync_UsesSnapshotPipeline()
    {
        var item = new WorkItemBuilder(1, "Dirty Item").Build();
        item.SetDirty();
        await _repo.SaveAsync(item);

        var results = await _repo.GetDirtyItemsAsync();
        results.Count.ShouldBe(1);
        results[0].IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task GetSeedsAsync_UsesSnapshotPipeline()
    {
        var seed = new WorkItemBuilder(-1, "My Seed").AsSeed().Build();
        await _repo.SaveAsync(seed);

        var results = await _repo.GetSeedsAsync();
        results.Count.ShouldBe(1);
        results[0].IsSeed.ShouldBeTrue();
    }

    [Fact]
    public async Task RoundTrip_RevisionZero_PreservedCorrectly()
    {
        var item = new WorkItemBuilder(10, "Rev Zero").Build();
        // Don't call MarkSynced — revision stays 0
        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(10);
        loaded.ShouldNotBeNull();
        loaded.Revision.ShouldBe(0);
    }

    [Fact]
    public async Task GetByIdsAsync_UsesSnapshotPipeline()
    {
        var item1 = new WorkItemBuilder(1, "First").Build();
        var item2 = new WorkItemBuilder(2, "Second").Build();
        var item3 = new WorkItemBuilder(3, "Third").Build();

        await _repo.SaveAsync(item1);
        await _repo.SaveAsync(item2);
        await _repo.SaveAsync(item3);

        var results = await _repo.GetByIdsAsync([1, 3]);
        results.Count.ShouldBe(2);
        results.ShouldContain(r => r.Id == 1);
        results.ShouldContain(r => r.Id == 3);
    }
}
