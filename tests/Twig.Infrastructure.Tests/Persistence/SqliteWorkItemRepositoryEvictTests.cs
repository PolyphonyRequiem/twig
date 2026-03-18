using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="SqliteWorkItemRepository.EvictExceptAsync"/>:
/// deletes non-kept items, preserves kept items, handles empty keep set, handles all-kept.
/// </summary>
public class SqliteWorkItemRepositoryEvictTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkItemRepository _repo;

    public SqliteWorkItemRepositoryEvictTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteWorkItemRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    // ═══════════════════════════════════════════════════════════════
    //  Deletes non-kept items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvictExceptAsync_DeletesItemsNotInKeepSet()
    {
        await _repo.SaveAsync(CreateWorkItem(1, "Task", "Keep"));
        await _repo.SaveAsync(CreateWorkItem(2, "Task", "Keep"));
        await _repo.SaveAsync(CreateWorkItem(3, "Task", "Evict"));
        await _repo.SaveAsync(CreateWorkItem(4, "Task", "Evict"));

        await _repo.EvictExceptAsync(new HashSet<int> { 1, 2 });

        (await _repo.GetByIdAsync(1)).ShouldNotBeNull();
        (await _repo.GetByIdAsync(2)).ShouldNotBeNull();
        (await _repo.GetByIdAsync(3)).ShouldBeNull();
        (await _repo.GetByIdAsync(4)).ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Preserves kept items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvictExceptAsync_PreservesAllKeptItems()
    {
        await _repo.SaveAsync(CreateWorkItem(10, "Feature", "Feature A"));
        await _repo.SaveAsync(CreateWorkItem(20, "Task", "Task B"));
        await _repo.SaveAsync(CreateWorkItem(30, "Bug", "Bug C"));

        await _repo.EvictExceptAsync(new HashSet<int> { 10, 20, 30 });

        (await _repo.GetByIdAsync(10)).ShouldNotBeNull();
        (await _repo.GetByIdAsync(20)).ShouldNotBeNull();
        (await _repo.GetByIdAsync(30)).ShouldNotBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty keep set — deletes everything
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvictExceptAsync_EmptyKeepSet_DeletesAll()
    {
        await _repo.SaveAsync(CreateWorkItem(1, "Task", "Item 1"));
        await _repo.SaveAsync(CreateWorkItem(2, "Task", "Item 2"));

        await _repo.EvictExceptAsync(new HashSet<int>());

        (await _repo.GetByIdAsync(1)).ShouldBeNull();
        (await _repo.GetByIdAsync(2)).ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  All items kept — deletes nothing
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvictExceptAsync_AllKept_DeletesNothing()
    {
        await _repo.SaveAsync(CreateWorkItem(1, "Task", "Item 1"));
        await _repo.SaveAsync(CreateWorkItem(2, "Task", "Item 2"));

        await _repo.EvictExceptAsync(new HashSet<int> { 1, 2 });

        (await _repo.GetByIdAsync(1)).ShouldNotBeNull();
        (await _repo.GetByIdAsync(2)).ShouldNotBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Keep set contains IDs not in DB — no error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvictExceptAsync_KeepSetWithExtraIds_NoError()
    {
        await _repo.SaveAsync(CreateWorkItem(1, "Task", "Item 1"));

        // Keep set includes IDs 1, 999 — 999 doesn't exist, should not cause error
        await _repo.EvictExceptAsync(new HashSet<int> { 1, 999 });

        (await _repo.GetByIdAsync(1)).ShouldNotBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty DB — empty keep set — no error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EvictExceptAsync_EmptyDbEmptyKeep_NoError()
    {
        await _repo.EvictExceptAsync(new HashSet<int>());
        // Should not throw
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helper
    // ═══════════════════════════════════════════════════════════════

    private static WorkItem CreateWorkItem(int id, string type, string title, int? parentId = null)
    {
        var typeResult = WorkItemType.Parse(type);
        var iterResult = IterationPath.Parse(@"Project\Sprint1");
        var areaResult = AreaPath.Parse(@"Project\Area");

        return new WorkItem
        {
            Id = id,
            Type = typeResult.Value,
            Title = title,
            State = "Active",
            ParentId = parentId,
            IterationPath = iterResult.Value,
            AreaPath = areaResult.Value,
        };
    }
}
