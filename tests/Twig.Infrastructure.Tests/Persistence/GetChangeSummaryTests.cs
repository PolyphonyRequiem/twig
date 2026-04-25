using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="SqlitePendingChangeStore.GetChangeSummaryAsync"/>.
/// Verifies note and field-edit counts are accurately grouped.
/// </summary>
public sealed class GetChangeSummaryTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqlitePendingChangeStore _changeStore;
    private readonly SqliteWorkItemRepository _repo;

    public GetChangeSummaryTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _changeStore = new SqlitePendingChangeStore(_store);
        _repo = new SqliteWorkItemRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task NoChanges_ReturnsBothZero()
    {
        var (notes, fieldEdits) = await _changeStore.GetChangeSummaryAsync(1);
        notes.ShouldBe(0);
        fieldEdits.ShouldBe(0);
    }

    [Fact]
    public async Task OnlyNotes_CountsCorrectly()
    {
        await SaveWorkItem(1);
        await _changeStore.AddChangeAsync(1, "add_note", null, null, "Note 1");
        await _changeStore.AddChangeAsync(1, "add_note", null, null, "Note 2");

        var (notes, fieldEdits) = await _changeStore.GetChangeSummaryAsync(1);

        notes.ShouldBe(2);
        fieldEdits.ShouldBe(0);
    }

    [Fact]
    public async Task OnlyFieldEdits_CountsCorrectly()
    {
        await SaveWorkItem(1);
        await _changeStore.AddChangeAsync(1, "set_field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(1, "set_field", "System.State", "New", "Active");
        await _changeStore.AddChangeAsync(1, "set_field", "System.AssignedTo", null, "User");

        var (notes, fieldEdits) = await _changeStore.GetChangeSummaryAsync(1);

        notes.ShouldBe(0);
        fieldEdits.ShouldBe(3);
    }

    [Fact]
    public async Task MixedChanges_CountsBothCorrectly()
    {
        await SaveWorkItem(1);
        await _changeStore.AddChangeAsync(1, "set_field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(1, "add_note", null, null, "Note");
        await _changeStore.AddChangeAsync(1, "set_field", "System.State", "New", "Active");

        var (notes, fieldEdits) = await _changeStore.GetChangeSummaryAsync(1);

        notes.ShouldBe(1);
        fieldEdits.ShouldBe(2);
    }

    [Fact]
    public async Task CrossItemIsolation_OnlyCountsRequestedItem()
    {
        await SaveWorkItem(1);
        await SaveWorkItem(2);
        await _changeStore.AddChangeAsync(1, "set_field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(1, "add_note", null, null, "Note");
        await _changeStore.AddChangeAsync(2, "set_field", "System.Title", "C", "D");
        await _changeStore.AddChangeAsync(2, "set_field", "System.State", "New", "Active");
        await _changeStore.AddChangeAsync(2, "add_note", null, null, "Note 2");

        var (notes1, fieldEdits1) = await _changeStore.GetChangeSummaryAsync(1);
        var (notes2, fieldEdits2) = await _changeStore.GetChangeSummaryAsync(2);

        notes1.ShouldBe(1);
        fieldEdits1.ShouldBe(1);
        notes2.ShouldBe(1);
        fieldEdits2.ShouldBe(2);
    }

    [Fact]
    public async Task UnknownChangeType_DoesNotAffectCounts()
    {
        await SaveWorkItem(1);
        await _changeStore.AddChangeAsync(1, "set_field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(1, "unknown_type", null, null, "data");

        var (notes, fieldEdits) = await _changeStore.GetChangeSummaryAsync(1);

        notes.ShouldBe(0);
        fieldEdits.ShouldBe(1);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private async Task SaveWorkItem(int id)
    {
        var item = new WorkItem
        {
            Id = id,
            Type = WorkItemType.Parse("Task").Value,
            Title = $"Work Item {id}",
            State = "Active",
            IterationPath = IterationPath.Parse(@"Project\Sprint1").Value,
            AreaPath = AreaPath.Parse(@"Project\Area").Value,
        };
        await _repo.SaveAsync(item);
    }
}
