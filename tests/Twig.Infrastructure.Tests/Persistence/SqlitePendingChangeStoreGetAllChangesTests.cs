using System.Globalization;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Focused coverage for <see cref="SqlitePendingChangeStore.GetAllChangesAsync"/>: the one-snapshot
/// projection joined against the durable seed tables. The tests exercise the row order, the raw
/// value preservation, the note mirror, and every branch of the seed remap resolution.
/// </summary>
public class SqlitePendingChangeStoreGetAllChangesTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqlitePendingChangeStore _changeStore;
    private readonly SqliteStagedIdentityRegistry _registry;
    private readonly SqlitePublishIdMapRepository _publishIdMap;
    private readonly SqliteWorkItemRepository _workItemRepo;

    public SqlitePendingChangeStoreGetAllChangesTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _changeStore = new SqlitePendingChangeStore(_store);
        _registry = new SqliteStagedIdentityRegistry(_store);
        _publishIdMap = new SqlitePublishIdMapRepository(_store, _registry);
        _workItemRepo = new SqliteWorkItemRepository(_store, new WorkItemMapper());
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task ReturnsEmpty_WhenJournalIsEmpty()
    {
        var details = await _changeStore.GetAllChangesAsync();

        details.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReturnsGloballyOrderedRowsAcrossWorkItems()
    {
        // The store returns rows ordered by pending_changes.id GLOBALLY, not per work item —
        // a caller replaying the journal needs to see the exact staging sequence.
        await InsertWorkItemAsync(1);
        await InsertWorkItemAsync(2);

        await _changeStore.AddChangeAsync(1, "field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(2, "field", "System.State", "New", "Active");
        await _changeStore.AddChangeAsync(1, "field", "System.Title", "B", "C");
        await _changeStore.AddChangeAsync(2, "note", null, null, "second");

        var details = await _changeStore.GetAllChangesAsync();

        details.Select(d => (d.WorkItemId, d.NewValue))
            .ShouldBe(new (int, string?)[]
            {
                (1, "B"),
                (2, "Active"),
                (1, "C"),
                (2, "second"),
            });

        // pending_change_id is monotonically increasing, which is what the caller relies on.
        details.Select(d => d.PendingChangeId).ShouldBeInOrder(SortDirection.Ascending);
    }

    [Fact]
    public async Task DoesNotCollapseRepeatedEditsOfTheSameField()
    {
        // Two edits of System.Title with the same old and new values must survive as two rows;
        // collapsing them here would erase the fact that the user edited the field twice.
        await InsertWorkItemAsync(1);
        await _changeStore.AddChangeAsync(1, "field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(1, "field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(1, "field", "System.Title", "A", "B");

        var details = await _changeStore.GetAllChangesAsync();

        details.Count.ShouldBe(3);
        details.ShouldAllBe(d => d.Field == "System.Title" && d.OldValue == "A" && d.NewValue == "B");
    }

    [Fact]
    public async Task NoteAndAddNoteMirrorNewValue_OtherKindsDoNot()
    {
        await InsertWorkItemAsync(1);
        await _changeStore.AddChangeAsync(1, "note", null, null, "current shape");
        await _changeStore.AddChangeAsync(1, "add_note", null, null, "legacy shape");
        await _changeStore.AddChangeAsync(1, "field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(1, "state", "System.State", "New", "Active");

        var details = await _changeStore.GetAllChangesAsync();

        details[0].Note.ShouldBe("current shape");
        details[1].Note.ShouldBe("legacy shape");
        details[2].Note.ShouldBeNull();
        details[3].Note.ShouldBeNull();
    }

    [Fact]
    public async Task ResolvesSeedRemap_ForNegativeAliasBackedByStagedIdentity()
    {
        // Wayfinder 0014: a staged seed lives as a negative alias in pending_changes and a
        // durable row in staged_identities. The projection has to join the two so the caller
        // gets both the human-readable alias and the collision-free identity.
        var seed = await _registry.MintAsync();
        await _changeStore.AddChangeAsync(seed.Alias.Value, "field", "System.Title", null, "Seedling");

        var details = await _changeStore.GetAllChangesAsync();

        var only = details.ShouldHaveSingleItem();
        only.WorkItemId.ShouldBe(seed.Alias.Value);
        only.SeedRemap.ShouldNotBeNull();
        only.SeedRemap!.Value.StagedIdentity.ShouldBe(seed.Identity);
        only.SeedRemap!.Value.StagedAlias.ShouldBe(seed.Alias);
        only.SeedRemap!.Value.PublishedWorkItemId.ShouldBeNull();
    }

    [Fact]
    public async Task ResolvesSeedRemap_ForRemappedPositiveWorkItemId()
    {
        // After publish the pending_changes row carries the positive ADO id (see
        // RemapWorkItemIdAsync). The projection walks publish_id_map so the caller can still
        // see which staged identity minted the row.
        var seed = await _registry.MintAsync();
        await _changeStore.AddChangeAsync(seed.Alias.Value, "note", null, null, "before publish");

        // publish_id_map lookup is by new_id, so simulate the publish step: record the mapping
        // and repoint the staged row at the freshly-minted ADO id.
        const int adoId = 4242;
        await _publishIdMap.RecordMappingAsync(seed.Identity, adoId);
        await _changeStore.RemapWorkItemIdAsync(seed.Alias.Value, adoId);

        var details = await _changeStore.GetAllChangesAsync();

        var only = details.ShouldHaveSingleItem();
        only.WorkItemId.ShouldBe(adoId);
        only.SeedRemap.ShouldNotBeNull();
        only.SeedRemap!.Value.StagedIdentity.ShouldBe(seed.Identity);
        only.SeedRemap!.Value.StagedAlias.ShouldBe(seed.Alias);
        only.SeedRemap!.Value.PublishedWorkItemId.ShouldBe(adoId);
    }

    [Fact]
    public async Task LegacyUnresolvedNegativeAlias_YieldsRowWithoutSeedRemap()
    {
        // A pre-0014 seed can leave a negative alias in pending_changes without a matching
        // staged_identities row. The read must still surface the row — it just cannot invent
        // a StagedIdentity for it.
        InsertRawPendingChange(workItemId: -999, kind: "field", field: "System.Title", oldValue: null, newValue: "Legacy");

        var details = await _changeStore.GetAllChangesAsync();

        var only = details.ShouldHaveSingleItem();
        only.WorkItemId.ShouldBe(-999);
        only.SeedRemap.ShouldBeNull();
    }

    [Fact]
    public async Task PositiveWorkItemIdWithoutRemap_YieldsRowWithoutSeedRemap()
    {
        await InsertWorkItemAsync(100);
        await _changeStore.AddChangeAsync(100, "field", "System.Title", "A", "B");

        var details = await _changeStore.GetAllChangesAsync();

        var only = details.ShouldHaveSingleItem();
        only.WorkItemId.ShouldBe(100);
        only.SeedRemap.ShouldBeNull();
    }

    [Fact]
    public async Task RawHtmlIsPreservedVerbatim_InNewValue()
    {
        // The projection never sanitises or normalises: whatever the caller staged is what
        // comes back out. That includes fully-formed HTML in a note body.
        const string html = "<div><b>bold</b> &amp; <i>italic</i><br/></div>";
        await InsertWorkItemAsync(1);
        await _changeStore.AddChangeAsync(1, "note", null, null, html);

        var details = await _changeStore.GetAllChangesAsync();

        var only = details.ShouldHaveSingleItem();
        only.NewValue.ShouldBe(html);
        only.Note.ShouldBe(html);
    }

    [Fact]
    public async Task UnknownKindIsPreservedRawly_WithoutNoteMirror()
    {
        // Forward compatibility: a kind this build doesn't recognise is passed through as-is.
        // No exception, no drop, and no note mirror.
        await InsertWorkItemAsync(1);
        await _changeStore.AddChangeAsync(1, "future_kind_2050", "SomeField", "old", "new");

        var details = await _changeStore.GetAllChangesAsync();

        var only = details.ShouldHaveSingleItem();
        only.Kind.ShouldBe("future_kind_2050");
        only.Field.ShouldBe("SomeField");
        only.OldValue.ShouldBe("old");
        only.NewValue.ShouldBe("new");
        only.Note.ShouldBeNull();
    }

    [Fact]
    public async Task AmbiguousPublishIdMap_ThrowsInvalidOperationException()
    {
        // Two publish_id_map rows sharing the same new_id would let the read pick a
        // StagedIdentity arbitrarily — the projection refuses to guess and forces the caller
        // to reconcile the durable tables first.
        var seedA = await _registry.MintAsync();
        var seedB = await _registry.MintAsync();
        const int adoId = 7777;
        await _publishIdMap.RecordMappingAsync(seedA.Identity, adoId);
        await _publishIdMap.RecordMappingAsync(seedB.Identity, adoId);

        await _changeStore.AddChangeAsync(adoId, "field", "System.Title", "A", "B");

        await Should.ThrowAsync<InvalidOperationException>(() => _changeStore.GetAllChangesAsync());
    }

    [Fact]
    public async Task StagedAtRoundTripsAsDateTimeOffset()
    {
        await InsertWorkItemAsync(1);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _changeStore.AddChangeAsync(1, "field", "System.Title", "A", "B");
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        var details = await _changeStore.GetAllChangesAsync();

        var only = details.ShouldHaveSingleItem();
        only.StagedAt.ShouldBeInRange(before, after);
    }

    private async Task InsertWorkItemAsync(int id)
    {
        var typeResult = WorkItemType.Parse("Task");
        var iterResult = IterationPath.Parse(@"Project\Sprint1");
        var areaResult = AreaPath.Parse(@"Project\Area");
        var item = new WorkItem
        {
            Id = id,
            Type = typeResult.Value,
            Title = $"Work Item {id}",
            State = "Active",
            IterationPath = iterResult.Value,
            AreaPath = areaResult.Value,
        };
        await _workItemRepo.SaveAsync(item);
    }

    private void InsertRawPendingChange(int workItemId, string kind, string? field, string? oldValue, string? newValue)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pending_changes (work_item_id, change_type, field_name, old_value, new_value, created_at)
            VALUES (@workItemId, @kind, @field, @oldValue, @newValue, @createdAt);
            """;
        cmd.Parameters.AddWithValue("@workItemId", workItemId);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@field", (object?)field ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@oldValue", (object?)oldValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@newValue", (object?)newValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}
