using System.Globalization;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class SqliteRecoverableWorkItemDraftStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"twig-draft-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(_directory, "twig.db");
    private string ConnectionString => $"Data Source={DatabasePath};Pooling=False";

    public SqliteRecoverableWorkItemDraftStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ReopenPreservesNullEmptyConflictsAndQualifiedIdentity_WithGenerationCas()
    {
        var key = Key("binding-a");
        var other = Key("binding-b");
        RecoverableWorkItemDraft first;
        using (var cache = new SqliteCacheStore(ConnectionString))
        {
            IRecoverableWorkItemDraftStore drafts = new SqliteRecoverableWorkItemDraftStore(cache);
            first = RecoverableWorkItemDraft.Create(key, 5)
                .SetField("field", null, string.Empty)
                .Observe(6, [new DraftFieldConflict("field", null, "server", string.Empty)]);
            (await drafts.TrySaveAsync(first, null)).ShouldBeTrue();
            (await drafts.TrySaveAsync(first, null)).ShouldBeFalse();
            (await drafts.TrySaveAsync(RecoverableWorkItemDraft.Create(other, 5), null)).ShouldBeTrue();
        }

        using var reopened = new SqliteCacheStore(ConnectionString);
        IRecoverableWorkItemDraftStore store = new SqliteRecoverableWorkItemDraftStore(reopened);
        var loaded = await store.GetAsync(key);
        loaded.ShouldNotBeNull();
        loaded.ItemKey.ShouldBe(key);
        loaded.Generation.ShouldBe(first.Generation);
        loaded.BaselineRevision.ShouldBe(5);
        loaded.ObservedRevision.ShouldBe(6);
        loaded.Changes.ShouldBe(first.Changes);
        loaded.Conflicts.ShouldBe(first.Conflicts);
        var newer = loaded.Observe(7, [new DraftFieldConflict("field", null, "new server", string.Empty)]);
        (await store.TrySaveAsync(newer, loaded.Generation)).ShouldBeTrue();
        var stale = loaded.Observe(8);
        (await store.TrySaveAsync(stale, loaded.Generation)).ShouldBeFalse();
        var readBack = await store.GetAsync(key);
        readBack.ShouldNotBeNull();
        readBack.Generation.ShouldBe(newer.Generation);
        readBack.Changes.ShouldBe(newer.Changes);
        readBack.Conflicts.ShouldBe(newer.Conflicts);
        (await store.GetAsync(other))!.Changes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Migration14To15AndDraftObservationPreserveSeedPendingAndJournalRows()
    {
        Dictionary<string, string[]> before;
        using (var cache = new SqliteCacheStore(ConnectionString))
        {
            SeedUnrelatedRows(cache.GetConnection());
            using var seed = cache.GetConnection().CreateCommand();
            seed.CommandText = """
                DROP TABLE pending.recoverable_work_item_drafts;
                PRAGMA pending.user_version = 14;
                """;
            seed.ExecuteNonQuery();
            before = SnapshotUnrelatedRows(cache.GetConnection());
            before.Values.ShouldAllBe(rows => rows.Length == 1);
        }

        using var migrated = new SqliteCacheStore(ConnectionString);
        using (var version = migrated.GetConnection().CreateCommand())
        {
            version.CommandText = "PRAGMA pending.user_version;";
            Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(15);
        }
        var drafts = new SqliteRecoverableWorkItemDraftStore(migrated);
        var key = Key("binding");
        (await drafts.GetAsync(key)).ShouldBeNull();
        var draft = RecoverableWorkItemDraft.Create(key, 5).SetField("field", "baseline", "draft intent");
        (await drafts.TrySaveAsync(draft, null)).ShouldBeTrue();
        var observed = draft.Observe(6, [new DraftFieldConflict("field", "baseline", "remote", "draft intent")]);
        (await drafts.TrySaveAsync(observed, draft.Generation)).ShouldBeTrue();
        (await drafts.GetAsync(key))!.Changes.ShouldBe(draft.Changes);
        var after = SnapshotUnrelatedRows(migrated.GetConnection());
        foreach (var (table, rows) in before)
            after[table].ShouldBe(rows, $"draft migration and CAS must preserve every cell in {table}");
    }

    [Fact]
    public void FailedMigrationDoesNotStampVersion15OrChangeExistingRows()
    {
        Dictionary<string, string[]> before;
        using (var cache = new SqliteCacheStore(ConnectionString))
        {
            SeedUnrelatedRows(cache.GetConnection());
            using var prepare = cache.GetConnection().CreateCommand();
            prepare.CommandText = """
                DROP TABLE pending.recoverable_work_item_drafts;
                PRAGMA pending.user_version = 14;
                CREATE INDEX pending.recoverable_work_item_drafts ON pending_changes(work_item_id);
                """;
            prepare.ExecuteNonQuery();
            before = SnapshotUnrelatedRows(cache.GetConnection());
            before.Values.ShouldAllBe(rows => rows.Length == 1);
        }

        var failure = Should.Throw<InvalidOperationException>(() =>
        {
            using var failed = new SqliteCacheStore(ConnectionString);
        });
        failure.InnerException.ShouldBeOfType<SqliteException>();

        using var pending = new SqliteConnection(
            $"Data Source={Path.Combine(_directory, "pending.db")};Pooling=False");
        pending.Open();
        using var inspect = pending.CreateCommand();
        inspect.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(inspect.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(14);
        inspect.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='recoverable_work_item_drafts';";
        Convert.ToInt32(inspect.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);
        var after = SnapshotUnrelatedRows(pending, prefix: "");
        foreach (var (table, rows) in before)
            after[table].ShouldBe(rows);
    }

    private static Dictionary<string, string[]> SnapshotUnrelatedRows(SqliteConnection connection, string prefix = "pending.")
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var table in new[] { "pending_changes", "staged_identities", "proposal_journals" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {prefix}{table} ORDER BY 1;";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var cells = new List<string>();
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    var value = reader.IsDBNull(index) ? null
                        : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
                    cells.Add(value is null ? "null;" : $"{value.Length}:{value};");
                }
                rows.Add(string.Concat(cells));
            }
            result.Add(table, rows.ToArray());
        }
        return result;
    }

    private static void SeedUnrelatedRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pending.pending_changes
                (work_item_id, change_type, field_name, old_value, new_value, created_at)
            VALUES (-7, 'FieldChange', 'field', NULL, 'pending intent', 'fixed-time');
            INSERT INTO pending.staged_identities (staged_identity, alias, created_at)
            VALUES ('seed-identity', -7, 'fixed-time');
            INSERT INTO pending.proposal_journals
                (digest, schema_version, organization, project, source_path, canonical_json, state, previewed_at)
            VALUES ('existing-digest', 1, 'scope', 'project', 'source', '{}', 'Planned', 'fixed-time');
            """;
        command.ExecuteNonQuery();
    }

    private static QualifiedWorkItemKey Key(string binding) => new(@"C:\repo", "connection", binding, 42);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
