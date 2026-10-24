using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>Epic #1168: the durable provider-neutral local work item store (schema v13).</summary>
public sealed class SqliteLocalWorkItemRepositoryTests : IDisposable
{
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly SqliteLocalWorkItemRepository _repo;

    public SqliteLocalWorkItemRepositoryTests() => _repo = new SqliteLocalWorkItemRepository(_store);

    public void Dispose() => _store.Dispose();

    private static FieldValues Title(string title) => FieldValues.Empty.Set("System.Title", new TextValue(title));

    [Fact]
    public async Task Create_MintsVersion7IdentityAtRevisionOne_AndReadsBack()
    {
        var created = await _repo.CreateAsync(Title("a"));

        created.Id.Value.Version.ShouldBe(7);
        created.Revision.ShouldBe(1);
        created.IsDeleted.ShouldBeFalse();
        (await _repo.GetAsync(created.Id)).ShouldBe(created);
        (await _repo.GetAsync(LocalWorkItemId.New())).ShouldBeNull();
    }

    [Fact]
    public async Task Update_AtCurrentRevision_AppliesAndIncrements()
    {
        var created = await _repo.CreateAsync(Title("a"));

        var result = await _repo.UpdateAsync(created.Id, 1, Title("b"));

        var applied = result.Value.ShouldBeOfType<LocalWriteApplied>();
        applied.Record.Revision.ShouldBe(2);
        applied.Record.Fields.ShouldBe(Title("b"));
        (await _repo.GetAsync(created.Id))!.Fields.ShouldBe(Title("b"));
    }

    [Fact]
    public async Task Update_AtStaleRevision_IsAConflict_AndWritesNothing()
    {
        var created = await _repo.CreateAsync(Title("a"));
        await _repo.UpdateAsync(created.Id, 1, Title("b"));

        var result = await _repo.UpdateAsync(created.Id, 1, Title("stale"));

        var conflict = result.Value.ShouldBeOfType<LocalRevisionConflict>();
        conflict.Current.Revision.ShouldBe(2);
        conflict.Current.Fields.ShouldBe(Title("b"));
        (await _repo.GetAsync(created.Id))!.Revision.ShouldBe(2);
    }

    [Fact]
    public async Task Delete_WritesTerminalTombstone()
    {
        var created = await _repo.CreateAsync(Title("a"));

        var deleted = (await _repo.DeleteAsync(created.Id, 1)).Value.ShouldBeOfType<LocalWriteApplied>().Record;

        deleted.IsDeleted.ShouldBeTrue();
        deleted.Revision.ShouldBe(2);
        deleted.Fields.ShouldBe(Title("a"));
        (await _repo.GetAsync(created.Id))!.IsDeleted.ShouldBeTrue();

        (await _repo.UpdateAsync(created.Id, 2, Title("revived"))).Value.ShouldBeOfType<LocalWorkItemTombstoned>();
        (await _repo.DeleteAsync(created.Id, 2)).Value.ShouldBeOfType<LocalWorkItemTombstoned>();
        (await _repo.GetAsync(created.Id))!.Revision.ShouldBe(2);
    }

    [Fact]
    public async Task Writes_ToUnknownIdentity_AreNotFound()
    {
        var unknown = LocalWorkItemId.New();

        (await _repo.UpdateAsync(unknown, 1, Title("x"))).Value.ShouldBe(new LocalWorkItemNotFound(unknown));
        (await _repo.DeleteAsync(unknown, 1)).Value.ShouldBe(new LocalWorkItemNotFound(unknown));
    }

    [Fact]
    public async Task ResolveExternal_IsIdempotent_AndKeyedOnAllThreeParts()
    {
        var (first, created) = await _repo.ResolveExternalAsync("ado", "org-guid", "42", "7");
        var (again, createdAgain) = await _repo.ResolveExternalAsync("ado", "org-guid", "42", "8");

        created.ShouldBeTrue();
        createdAgain.ShouldBeFalse();
        again.ShouldBe(first);
        (await _repo.GetAsync(first))!.Fields.ShouldBe(FieldValues.Empty);

        // Each part of the key distinguishes; native ids are case-preserving.
        (await _repo.ResolveExternalAsync("github", "org-guid", "42", null)).Id.ShouldNotBe(first);
        (await _repo.ResolveExternalAsync("ado", "other-org", "42", null)).Id.ShouldNotBe(first);
        var lower = (await _repo.ResolveExternalAsync("github", "https://api.github.com", "i_abc", null)).Id;
        var upper = (await _repo.ResolveExternalAsync("github", "https://api.github.com", "I_ABC", null)).Id;
        lower.ShouldNotBe(upper);

        (await _repo.FindExternalAsync("ado", "org-guid", "42")).ShouldBe(first);
        (await _repo.FindExternalAsync("ado", "org-guid", "43")).ShouldBeNull();
        CountRows("local_work_items").ShouldBe(5);
        CountRows("external_identities").ShouldBe(5);
    }

    [Fact]
    public async Task ObservedToken_IsOpaque_Nullable_AndNeverMintsAMapping()
    {
        await _repo.ResolveExternalAsync("ado", "org", "1", "7");
        (await _repo.GetObservedTokenAsync("ado", "org", "1")).ShouldBe((true, "7"));

        (await _repo.RecordObservedTokenAsync("ado", "org", "1", "W/\"etag\"")).ShouldBeTrue();
        (await _repo.GetObservedTokenAsync("ado", "org", "1")).ShouldBe((true, "W/\"etag\""));

        (await _repo.RecordObservedTokenAsync("ado", "org", "1", null)).ShouldBeTrue();
        (await _repo.GetObservedTokenAsync("ado", "org", "1")).ShouldBe((true, (string?)null));

        (await _repo.RecordObservedTokenAsync("ado", "org", "2", "1")).ShouldBeFalse();
        (await _repo.GetObservedTokenAsync("ado", "org", "2")).ShouldBe((false, (string?)null));
        CountRows("external_identities").ShouldBe(1);
    }

    [Fact]
    public async Task ResolveExternal_JoinsAmbientTransaction_AndRollsBackWithIt()
    {
        var conn = _store.GetConnection();
        using (var tx = conn.BeginTransaction())
        {
            _store.ActiveTransaction = tx;
            try
            {
                await _repo.ResolveExternalAsync("ado", "org", "1", null);
                tx.Rollback();
            }
            finally { _store.ActiveTransaction = null; }
        }

        CountRows("local_work_items").ShouldBe(0);
        CountRows("external_identities").ShouldBe(0);
    }

    [Fact]
    public async Task FieldsJson_IsTheCanonicalConverterForm()
    {
        var fields = FieldValues.Empty
            .Set("System.Tags", new StringListValue(["a", "b"]))
            .Set("System.AssignedTo", new IdentityValue("guid-1", "Daniel"));
        var created = await _repo.CreateAsync(fields);

        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT fields_json FROM pending.local_work_items WHERE local_id = @id;";
        cmd.Parameters.AddWithValue("@id", created.Id.ToString());
        var json = (string)cmd.ExecuteScalar()!;

        json.ShouldBe(JsonSerializer.Serialize(fields, TwigJsonContext.Default.FieldValues));
        json.ShouldStartWith("""{"System.AssignedTo":{"kind":"identity","nativeId":"guid-1","displayName":"Daniel"}""");
    }

    [Fact]
    public void Validation_RejectsBlankKeyPartsAndEmptyIds()
    {
        Should.Throw<ArgumentException>(() => _repo.ResolveExternalAsync(" ", "s", "n", null));
        Should.Throw<ArgumentException>(() => _repo.ResolveExternalAsync("ado", "", "n", null));
        Should.Throw<ArgumentException>(() => _repo.FindExternalAsync("ado", "s", " "));
        Should.ThrowAsync<ArgumentException>(() => _repo.UpdateAsync(default, 1, FieldValues.Empty));
    }

    private long CountRows(string table)
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM pending.{table};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}

/// <summary>Epic #1168: the v13 tables live in the never-dropped durable store.</summary>
public sealed class LocalWorkItemDurabilityTests
{
    [Fact]
    public async Task V12DurableStore_MigratesAdditively_PreservingExistingRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"twig_1168_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var source = $"Data Source={Path.Combine(dir, "twig.db")}";
        try
        {
            // Reconstruct a v12 durable store: drop the v13 tables and rewind the version.
            using (var store = new SqliteCacheStore(source))
            using (var cmd = store.GetConnection().CreateCommand())
            {
                cmd.CommandText = """
                    DROP TABLE pending.local_work_items;
                    DROP TABLE pending.external_identities;
                    DROP TABLE pending.local_relationships;
                    INSERT INTO pending.pending_changes (work_item_id, change_type, field_name, new_value, created_at)
                        VALUES (-1, 'field', 'System.Title', 'keep me', '2026-01-01T00:00:00.0000000+00:00');
                    PRAGMA pending.user_version = 12;
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var store = new SqliteCacheStore(source))
            {
                using (var cmd = store.GetConnection().CreateCommand())
                {
                    cmd.CommandText = "PRAGMA pending.user_version;";
                    Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(13);
                    cmd.CommandText = "SELECT new_value FROM pending.pending_changes WHERE work_item_id = -1;";
                    cmd.ExecuteScalar().ShouldBe("keep me");
                }

                var created = await new SqliteLocalWorkItemRepository(store)
                    .CreateAsync(FieldValues.Empty.Set("x", new BooleanValue(true)));
                created.Revision.ShouldBe(1);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task MirrorRebuild_LeavesLocalRecordsAndMappingsIntact()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"twig_1168_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var source = $"Data Source={Path.Combine(dir, "twig.db")}";
        try
        {
            LocalWorkItemRecord created;
            LocalWorkItemId mapped;
            using (var store = new SqliteCacheStore(source))
            {
                var repo = new SqliteLocalWorkItemRepository(store);
                created = await repo.CreateAsync(FieldValues.Empty.Set("x", new IntegerValue(3)));
                mapped = (await repo.ResolveExternalAsync("ado", "org", "9", "4")).Id;

                // Force the next open to see a stale mirror schema and rebuild it.
                using var cmd = store.GetConnection().CreateCommand();
                cmd.CommandText = "UPDATE main.metadata SET value = '0' WHERE key = 'schema_version';";
                cmd.ExecuteNonQuery().ShouldBe(1);
            }

            using (var store = new SqliteCacheStore(source))
            {
                store.SchemaWasRebuilt.ShouldBeTrue();
                var repo = new SqliteLocalWorkItemRepository(store);
                (await repo.GetAsync(created.Id)).ShouldBe(created);
                (await repo.FindExternalAsync("ado", "org", "9")).ShouldBe(mapped);
                (await repo.GetObservedTokenAsync("ado", "org", "9")).ShouldBe((true, "4"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void LocalRelationships_KeyIsSourceTargetKind_AndAllowsDistinctKinds()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        using var cmd = store.GetConnection().CreateCommand();
        const string insert = """
            INSERT INTO pending.local_relationships (source_local_id, target_local_id, kind, revision, created_at, updated_at)
            VALUES ('a', 'b', @kind, 1, 't', 't');
            """;
        cmd.CommandText = insert;
        cmd.Parameters.AddWithValue("@kind", "parent-child");
        cmd.ExecuteNonQuery();
        cmd.Parameters["@kind"].Value = "related";
        cmd.ExecuteNonQuery();

        cmd.Parameters["@kind"].Value = "parent-child";
        Should.Throw<SqliteException>(() => cmd.ExecuteNonQuery());

        cmd.Parameters.Clear();
        cmd.CommandText = """
            INSERT INTO pending.local_relationships (source_local_id, target_local_id, kind, revision, created_at, updated_at)
            VALUES ('a', 'c', 'related', 0, 't', 't');
            """;
        Should.Throw<SqliteException>(() => cmd.ExecuteNonQuery());
    }
}
