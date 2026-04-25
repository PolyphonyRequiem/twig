using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="SqliteWorkItemRepository.ClearDirtyFlagAsync"/>.
/// Verifies single-item dirty flag clearing.
/// </summary>
public sealed class ClearDirtyFlagTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkItemRepository _repo;

    public ClearDirtyFlagTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteWorkItemRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task ClearsDirtyFlag_OnDirtyItem()
    {
        var item = CreateWorkItem(1, "Task", "Dirty Item", "Active");
        item.SetDirty();
        await _repo.SaveAsync(item);

        await _repo.ClearDirtyFlagAsync(1);

        var loaded = await _repo.GetByIdAsync(1);
        loaded.ShouldNotBeNull();
        loaded.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task NoOp_OnCleanItem()
    {
        var item = CreateWorkItem(1, "Task", "Clean Item", "Active");
        await _repo.SaveAsync(item);

        await _repo.ClearDirtyFlagAsync(1);

        var loaded = await _repo.GetByIdAsync(1);
        loaded.ShouldNotBeNull();
        loaded.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task NoOp_WhenItemDoesNotExist()
    {
        // Should not throw when the ID doesn't exist
        await _repo.ClearDirtyFlagAsync(9999);
    }

    [Fact]
    public async Task CrossItemIsolation_OnlyAffectsTargetItem()
    {
        var dirty1 = CreateWorkItem(1, "Task", "Dirty 1", "Active");
        dirty1.SetDirty();
        await _repo.SaveAsync(dirty1);

        var dirty2 = CreateWorkItem(2, "Task", "Dirty 2", "Active");
        dirty2.SetDirty();
        await _repo.SaveAsync(dirty2);

        await _repo.ClearDirtyFlagAsync(1);

        (await _repo.GetByIdAsync(1))!.IsDirty.ShouldBeFalse();
        (await _repo.GetByIdAsync(2))!.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task Idempotent_SecondCallIsNoOp()
    {
        var item = CreateWorkItem(1, "Task", "Dirty Item", "Active");
        item.SetDirty();
        await _repo.SaveAsync(item);

        await _repo.ClearDirtyFlagAsync(1);
        await _repo.ClearDirtyFlagAsync(1);

        var loaded = await _repo.GetByIdAsync(1);
        loaded.ShouldNotBeNull();
        loaded.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task WorksOnSeedItems()
    {
        WorkItem.InitializeSeedCounter(-100);
        var seed = WorkItem.CreateSeed(WorkItemType.Task, "Seed Item");
        seed.SetDirty();
        await _repo.SaveAsync(seed);

        await _repo.ClearDirtyFlagAsync(seed.Id);

        var loaded = await _repo.GetByIdAsync(seed.Id);
        loaded.ShouldNotBeNull();
        loaded.IsDirty.ShouldBeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(
        int id, string type, string title, string state)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Parse(type).Value,
            Title = title,
            State = state,
            IterationPath = IterationPath.Parse(@"Project\Sprint1").Value,
            AreaPath = AreaPath.Parse(@"Project\Area").Value,
        };
    }
}
