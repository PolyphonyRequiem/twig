using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Verifies <see cref="SqliteWorkItemRepository.ClearPhantomDirtyFlagsAsync"/>
/// against a real in-memory SQLite database. Phantom dirty items (is_dirty=1 with
/// zero pending_changes rows) must be cleansed; items with real pending changes or
/// seed items must never be touched.
/// </summary>
public sealed class PhantomDirtyCleansingTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkItemRepository _repo;

    public PhantomDirtyCleansingTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteWorkItemRepository(_store, new WorkItemMapper());
    }

    public void Dispose() => _store.Dispose();

    // ═══════════════════════════════════════════════════════════════
    //  (1) Items with is_dirty=1 and zero pending_changes rows
    //      are cleared
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SinglePhantomDirtyItem_IsCleansed()
    {
        var item = CreateWorkItem(1, "Task", "Phantom", "Active");
        item.SetDirty();
        await _repo.SaveAsync(item);

        var cleared = await _repo.ClearPhantomDirtyFlagsAsync();

        cleared.ShouldBe(1);
        var loaded = await _repo.GetByIdAsync(1);
        loaded.ShouldNotBeNull();
        loaded.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task MultiplePhantomDirtyItems_AllCleansed()
    {
        for (var i = 1; i <= 5; i++)
        {
            var item = CreateWorkItem(i, "Task", $"Phantom {i}", "Active");
            item.SetDirty();
            await _repo.SaveAsync(item);
        }

        var cleared = await _repo.ClearPhantomDirtyFlagsAsync();

        cleared.ShouldBe(5);
        for (var i = 1; i <= 5; i++)
        {
            (await _repo.GetByIdAsync(i))!.IsDirty.ShouldBeFalse();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  (2) Items with real pending_changes rows remain untouched
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DirtyItemWithPendingChange_RemainsUntouched()
    {
        var item = CreateWorkItem(1, "Task", "Real Dirty", "Active");
        item.SetDirty();
        await _repo.SaveAsync(item);

        InsertPendingChange(1, "set_field", "System.Title", "Old", "New");

        var cleared = await _repo.ClearPhantomDirtyFlagsAsync();

        cleared.ShouldBe(0);
        (await _repo.GetByIdAsync(1))!.IsDirty.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  (3) Seed items are never cleansed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DirtySeedWithNoPendingChanges_NeverCleansed()
    {
        var seed = new WorkItemBuilder(-101, "Dirty Seed").AsSeed().Build();
        seed.SetDirty();
        await _repo.SaveAsync(seed);

        var cleared = await _repo.ClearPhantomDirtyFlagsAsync();

        cleared.ShouldBe(0);
        var loaded = await _repo.GetByIdAsync(seed.Id);
        loaded.ShouldNotBeNull();
        loaded.IsDirty.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  (4) Cleansing count returned is accurate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Count_MatchesExactNumberOfPhantomsCleansed()
    {
        // 3 phantom dirty (no pending changes)
        for (var i = 1; i <= 3; i++)
        {
            var phantom = CreateWorkItem(i, "Task", $"Phantom {i}", "Active");
            phantom.SetDirty();
            await _repo.SaveAsync(phantom);
        }

        // 2 real dirty (have pending changes)
        for (var i = 4; i <= 5; i++)
        {
            var real = CreateWorkItem(i, "Task", $"Real {i}", "Active");
            real.SetDirty();
            await _repo.SaveAsync(real);
            InsertPendingChange(i, "set_field", "System.Title", "A", "B");
        }

        // 1 clean (not dirty)
        var clean = CreateWorkItem(6, "Task", "Clean", "Active");
        await _repo.SaveAsync(clean);

        // 1 dirty seed
        var seed = new WorkItemBuilder(-301, "Seed").AsSeed().Build();
        seed.SetDirty();
        await _repo.SaveAsync(seed);

        var cleared = await _repo.ClearPhantomDirtyFlagsAsync();

        cleared.ShouldBe(3);
    }

    [Fact]
    public async Task MixedScenario_VerifiesEachItemFinalState()
    {
        // Phantom dirty → cleansed
        var phantom = CreateWorkItem(1, "Task", "Phantom", "Active");
        phantom.SetDirty();
        await _repo.SaveAsync(phantom);

        // Real dirty → stays dirty
        var realDirty = CreateWorkItem(2, "Task", "Real Dirty", "Active");
        realDirty.SetDirty();
        await _repo.SaveAsync(realDirty);
        InsertPendingChange(2, "set_field", "System.Title", "Old", "New");

        // Clean → stays clean
        var clean = CreateWorkItem(3, "Task", "Clean", "Active");
        await _repo.SaveAsync(clean);

        // Dirty seed → stays dirty
        var seed = new WorkItemBuilder(-401, "Dirty Seed").AsSeed().Build();
        seed.SetDirty();
        await _repo.SaveAsync(seed);

        var cleared = await _repo.ClearPhantomDirtyFlagsAsync();

        cleared.ShouldBe(1);
        (await _repo.GetByIdAsync(1))!.IsDirty.ShouldBeFalse();
        (await _repo.GetByIdAsync(2))!.IsDirty.ShouldBeTrue();
        (await _repo.GetByIdAsync(3))!.IsDirty.ShouldBeFalse();
        (await _repo.GetByIdAsync(seed.Id))!.IsDirty.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  (5) Empty repository returns count 0
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task EmptyRepository_ReturnsZero()
    {
        var cleared = await _repo.ClearPhantomDirtyFlagsAsync();
        cleared.ShouldBe(0);
    }

    [Fact]
    public async Task OnlyCleanItems_ReturnsZero()
    {
        var item1 = CreateWorkItem(1, "Task", "Clean 1", "Active");
        var item2 = CreateWorkItem(2, "Task", "Clean 2", "Active");
        await _repo.SaveAsync(item1);
        await _repo.SaveAsync(item2);

        var cleared = await _repo.ClearPhantomDirtyFlagsAsync();
        cleared.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Additional edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Idempotent_SecondCallReturnsZero()
    {
        var item = CreateWorkItem(1, "Task", "Phantom", "Active");
        item.SetDirty();
        await _repo.SaveAsync(item);

        var first = await _repo.ClearPhantomDirtyFlagsAsync();
        var second = await _repo.ClearPhantomDirtyFlagsAsync();

        first.ShouldBe(1);
        second.ShouldBe(0);
    }

    [Fact]
    public async Task PendingChangeForDifferentItem_DoesNotProtectPhantom()
    {
        // Item 1: dirty, no pending changes → phantom
        var phantom = CreateWorkItem(1, "Task", "Phantom", "Active");
        phantom.SetDirty();
        await _repo.SaveAsync(phantom);

        // Item 2: dirty, has pending changes → real dirty
        var real = CreateWorkItem(2, "Task", "Real", "Active");
        real.SetDirty();
        await _repo.SaveAsync(real);
        InsertPendingChange(2, "set_field", "System.Title", "A", "B");

        var cleared = await _repo.ClearPhantomDirtyFlagsAsync();

        cleared.ShouldBe(1);
        (await _repo.GetByIdAsync(1))!.IsDirty.ShouldBeFalse();
        (await _repo.GetByIdAsync(2))!.IsDirty.ShouldBeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private void InsertPendingChange(
        int workItemId, string changeType, string? fieldName,
        string? oldValue, string? newValue)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pending_changes (work_item_id, change_type, field_name, old_value, new_value, created_at)
            VALUES (@wid, @ct, @fn, @ov, @nv, '2026-01-01T00:00:00Z');
            """;
        cmd.Parameters.AddWithValue("@wid", workItemId);
        cmd.Parameters.AddWithValue("@ct", changeType);
        cmd.Parameters.AddWithValue("@fn", (object?)fieldName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ov", (object?)oldValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nv", (object?)newValue ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static WorkItem CreateWorkItem(
        int id, string type, string title, string state,
        int? parentId = null)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Parse(type).Value,
            Title = title,
            State = state,
            ParentId = parentId,
            IterationPath = IterationPath.Parse(@"Project\Sprint1").Value,
            AreaPath = AreaPath.Parse(@"Project\Area").Value,
        };
    }
}
