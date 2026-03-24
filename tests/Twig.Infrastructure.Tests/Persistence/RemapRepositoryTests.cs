using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="SqliteSeedLinkRepository.RemapIdAsync"/>.
/// </summary>
public class SqliteSeedLinkRepositoryRemapTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteSeedLinkRepository _repo;

    public SqliteSeedLinkRepositoryRemapTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteSeedLinkRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task RemapIdAsync_UpdatesSourceId()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Related, now));

        await _repo.RemapIdAsync(-1, 100);

        var links = await _repo.GetLinksForItemAsync(100);
        links.Count.ShouldBe(1);
        links[0].SourceId.ShouldBe(100);
        links[0].TargetId.ShouldBe(-2);
    }

    [Fact]
    public async Task RemapIdAsync_UpdatesTargetId()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-2, -1, SeedLinkTypes.Blocks, now));

        await _repo.RemapIdAsync(-1, 200);

        var links = await _repo.GetLinksForItemAsync(200);
        links.Count.ShouldBe(1);
        links[0].SourceId.ShouldBe(-2);
        links[0].TargetId.ShouldBe(200);
    }

    [Fact]
    public async Task RemapIdAsync_UpdatesBothSourceAndTarget()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Related, now));
        await _repo.AddLinkAsync(new SeedLink(-3, -1, SeedLinkTypes.DependsOn, now));

        await _repo.RemapIdAsync(-1, 500);

        var links = await _repo.GetLinksForItemAsync(500);
        links.Count.ShouldBe(2);
        links.ShouldContain(l => l.SourceId == 500 && l.TargetId == -2);
        links.ShouldContain(l => l.SourceId == -3 && l.TargetId == 500);
    }

    [Fact]
    public async Task RemapIdAsync_NoOp_WhenOldIdNotPresent()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Related, now));

        await _repo.RemapIdAsync(-99, 100);

        var all = await _repo.GetAllSeedLinksAsync();
        all.Count.ShouldBe(1);
        all[0].SourceId.ShouldBe(-1);
        all[0].TargetId.ShouldBe(-2);
    }

    [Fact]
    public async Task RemapIdAsync_DoesNotAffectOtherLinks()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AddLinkAsync(new SeedLink(-1, -2, SeedLinkTypes.Related, now));
        await _repo.AddLinkAsync(new SeedLink(-3, -4, SeedLinkTypes.Blocks, now));

        await _repo.RemapIdAsync(-1, 100);

        var link34 = await _repo.GetLinksForItemAsync(-3);
        link34.Count.ShouldBe(1);
        link34[0].SourceId.ShouldBe(-3);
        link34[0].TargetId.ShouldBe(-4);
    }
}

/// <summary>
/// Tests for <see cref="SqliteWorkItemRepository.RemapParentIdAsync"/>.
/// </summary>
public class SqliteWorkItemRepositoryRemapTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkItemRepository _repo;

    public SqliteWorkItemRepositoryRemapTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteWorkItemRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task RemapParentIdAsync_UpdatesChildrenParentId()
    {
        var child1 = new WorkItem { Id = -10, Type = WorkItemType.Task, Title = "Child 1", State = "New", ParentId = -1, IsSeed = true };
        var child2 = new WorkItem { Id = -11, Type = WorkItemType.Bug, Title = "Child 2", State = "New", ParentId = -1, IsSeed = true };
        await _repo.SaveAsync(child1);
        await _repo.SaveAsync(child2);

        await _repo.RemapParentIdAsync(-1, 500);

        var c1 = await _repo.GetByIdAsync(-10);
        var c2 = await _repo.GetByIdAsync(-11);
        c1!.ParentId.ShouldBe(500);
        c2!.ParentId.ShouldBe(500);
    }

    [Fact]
    public async Task RemapParentIdAsync_NoOp_WhenNoChildren()
    {
        var item = new WorkItem { Id = -1, Type = WorkItemType.Task, Title = "Solo", State = "New", ParentId = 42, IsSeed = true };
        await _repo.SaveAsync(item);

        await _repo.RemapParentIdAsync(-99, 500);

        var loaded = await _repo.GetByIdAsync(-1);
        loaded!.ParentId.ShouldBe(42);
    }

    [Fact]
    public async Task RemapParentIdAsync_DoesNotAffectOtherParents()
    {
        var child1 = new WorkItem { Id = -10, Type = WorkItemType.Task, Title = "Child of -1", State = "New", ParentId = -1, IsSeed = true };
        var child2 = new WorkItem { Id = -11, Type = WorkItemType.Task, Title = "Child of -2", State = "New", ParentId = -2, IsSeed = true };
        await _repo.SaveAsync(child1);
        await _repo.SaveAsync(child2);

        await _repo.RemapParentIdAsync(-1, 500);

        var c1 = await _repo.GetByIdAsync(-10);
        var c2 = await _repo.GetByIdAsync(-11);
        c1!.ParentId.ShouldBe(500);
        c2!.ParentId.ShouldBe(-2); // Unchanged
    }
}
