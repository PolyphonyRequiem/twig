using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// #1168 U5a. Fixture caveat: a real current-version store with its mirror version stamp lowered
/// to 15, which exercises the mismatch branch; it is not a captured historical v15 database.
/// </summary>
public sealed class SchemaRebuildLocalWorkTests
{
    private const string SeedTitle = "Unpublished seed";
    private const int DirtyId = 77;

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twig-rebuild-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Cs(string dir) => $"Data Source={Path.Combine(dir, "twig.db")}";

    private static SqliteConnection OpenReadOnly(string file)
    {
        var c = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        c.Open();
        return c;
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string, object)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return cmd.ExecuteScalar();
    }

    private static void Cleanup(string dir)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir, recursive: true);
    }

    private const string Advice = "No local work was dropped. Preserve this cache's files and open it " +
        "with a compatible Twig version, or export/recover the work first; do not delete the cache.";

    private static string Present(int seeds, int dirty) =>
        $"mirror-local-work-present: this cache holds {seeds} seed(s) and {dirty} dirty item(s) that exist only locally " +
        "and a rebuild would drop them. " + Advice;

    private const string Unverifiable =
        "mirror-local-work-unverifiable: this cache's work item table has an older shape that cannot show " +
        "whether it holds local-only work. " + Advice;

    private const string DirtyTitle = "Dirty local edit";

    private sealed record Seeded(StagedSeedIdentity Minted);

    private static async Task<Seeded> SeedAsync(string dir, bool seed, bool dirty, Action<SqliteConnection>? reshape = null)
    {
        using var store = new SqliteCacheStore(Cs(dir));
        var repo = new SqliteWorkItemRepository(store, new WorkItemMapper());
        var minted = await new SqliteStagedIdentityRegistry(store).MintAsync();
        if (seed)
            await repo.SaveAsync(new WorkItem { Id = minted.Alias.Value, StagedIdentity = minted.Identity, IsSeed = true, Title = SeedTitle, Type = WorkItemType.Task });
        if (dirty)
            await repo.SaveAsync(new WorkItem { Id = DirtyId, Title = DirtyTitle, Type = WorkItemType.Task });
        using (var cmd = store.GetConnection().CreateCommand())
        {
            cmd.CommandText = (dirty ? "UPDATE work_items SET is_dirty = 1 WHERE id = @id;" : "") + "UPDATE metadata SET value = '15' WHERE key = 'schema_version';";
            cmd.Parameters.AddWithValue("@id", DirtyId);
            cmd.ExecuteNonQuery();
        }
        reshape?.Invoke(store.GetConnection());
        return new Seeded(minted);
    }

    private static void AssertIntact(string dir, Seeded s, bool seed, bool dirty, bool seedFlag = true, bool dirtyFlag = true)
    {
        SqliteConnection.ClearAllPools();
        using var mirror = OpenReadOnly(Path.Combine(dir, "twig.db"));
        Scalar(mirror, "SELECT value FROM metadata WHERE key = 'schema_version';").ShouldBe("15");
        var m = s.Minted;
        if (seed)
        {
            var sql = "SELECT COUNT(*) FROM work_items WHERE id = @id AND title = @t AND staged_identity = @i" + (seedFlag ? " AND is_seed = 1;" : ";");
            Convert.ToInt32(Scalar(mirror, sql, ("@id", m.Alias.Value), ("@t", SeedTitle), ("@i", m.Identity.ToString()))).ShouldBe(1, "seed row");
        }
        if (dirty)
        {
            var sql = "SELECT COUNT(*) FROM work_items WHERE id = @id AND title = @t" + (dirtyFlag ? " AND is_dirty = 1;" : ";");
            Convert.ToInt32(Scalar(mirror, sql, ("@id", DirtyId), ("@t", DirtyTitle))).ShouldBe(1, "dirty row");
        }
        using var durable = OpenReadOnly(Path.Combine(dir, "pending.db"));
        Convert.ToInt32(Scalar(durable, "SELECT COUNT(*) FROM staged_identities WHERE alias = @a AND staged_identity = @i AND retired_at IS NULL;",
            ("@a", m.Alias.Value), ("@i", m.Identity.ToString()))).ShouldBe(1, "durable active identity");
    }

    private static string Refusal(string dir)
    {
        var ex = Should.Throw<InvalidOperationException>(() => new SqliteCacheStore(Cs(dir)).Dispose());
        SqliteConnection.ClearAllPools();
        return ex.Message;
    }

    private static async Task RefusesAsync(bool seed, bool dirty, string expected, Action<SqliteConnection>? reshape = null,
        bool seedFlag = true, bool dirtyFlag = true)
    {
        var dir = NewDir();
        try
        {
            var s = await SeedAsync(dir, seed, dirty, reshape);
            AssertIntact(dir, s, seed, dirty, seedFlag, dirtyFlag);
            Refusal(dir).ShouldBe(expected);
            AssertIntact(dir, s, seed, dirty, seedFlag, dirtyFlag);
        }
        finally { Cleanup(dir); }
    }

    private static void DropColumn(SqliteConnection c, string column, string index)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"DROP INDEX {index}; ALTER TABLE work_items DROP COLUMN {column};";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public Task SchemaRebuild_WithSeedRowOnly_RefusesAndPreservesIt() => RefusesAsync(true, false, Present(1, 0));

    [Fact]
    public Task SchemaRebuild_WithDirtyRowOnly_RefusesAndPreservesIt() => RefusesAsync(false, true, Present(0, 1));

    [Fact]
    public Task SchemaRebuild_WithSeedAndDirtyRows_ReportsBoth() => RefusesAsync(true, true, Present(1, 1));

    [Fact]
    public async Task SchemaRebuild_WithEditedSeedAndWorkspaceContext_RefusesWithoutChangingEitherStore()
    {
        var dir = NewDir();
        const string editedFieldsJson =
            """{"System.Title":"Edited seed title","System.Description":"Full seed description\nwith multiple lines.","Custom.Marker":"preserve-this-value"}""";
        const string editedTitle = "Edited seed title";
        const string editedState = "Drafted locally";
        const string note = "Seed note must survive an interrupted preservation attempt.";
        try
        {
            var seeded = await SeedAsync(dir, seed: true, dirty: false, c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    UPDATE work_items
                    SET title = @title, state = @state, parent_id = -73, assigned_to = 'seed-author',
                        iteration_path = 'Project\Sprint 8', area_path = 'Project\Area 4', revision = 9,
                        seed_created_at = '2026-08-14T12:34:56.0000000+00:00', fields_json = @fields
                    WHERE is_seed = 1;

                    INSERT INTO pending.pending_changes
                        (work_item_id, change_type, field_name, old_value, new_value, created_at)
                    SELECT id, 'note', NULL, NULL, @note, '2026-08-14T12:35:00.0000000+00:00'
                    FROM work_items WHERE is_seed = 1;

                    INSERT INTO context (key, value) VALUES
                        ('active_work_item_id', (SELECT CAST(id AS TEXT) FROM work_items WHERE is_seed = 1)),
                        ('workspace_mode', 'workspace-scoped-mode'),
                        ('workspace_context_marker', 'opaque-context-value'),
                        ('nav_history_cursor', '401')
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;

                    INSERT INTO navigation_history (id, work_item_id, visited_at)
                    SELECT 401, id, '2026-08-14T12:36:00.0000000+00:00'
                    FROM work_items WHERE is_seed = 1;
                    INSERT INTO navigation_history (id, work_item_id, visited_at)
                    SELECT 402, id, '2026-08-14T12:37:00.0000000+00:00'
                    FROM work_items WHERE is_seed = 1;
                    """;
                cmd.Parameters.AddWithValue("@title", editedTitle);
                cmd.Parameters.AddWithValue("@state", editedState);
                cmd.Parameters.AddWithValue("@fields", editedFieldsJson);
                cmd.Parameters.AddWithValue("@note", note);
                cmd.ExecuteNonQuery();
            });

            AssertEditedSeedAndContextIntact(dir, seeded, editedTitle, editedState, editedFieldsJson, note);
            Refusal(dir).ShouldBe(Present(1, 0));
            AssertEditedSeedAndContextIntact(dir, seeded, editedTitle, editedState, editedFieldsJson, note);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public Task SchemaRebuild_SeedFlagMissing_StillRefusesOnDirtyRow() =>
        RefusesAsync(false, true, Present(0, 1), c => DropColumn(c, "is_seed", "idx_work_items_seed"), seedFlag: true);

    [Fact]
    public Task SchemaRebuild_DirtyFlagMissing_StillRefusesOnSeedRow() =>
        RefusesAsync(true, false, Present(1, 0), c => DropColumn(c, "is_dirty", "idx_work_items_dirty"), dirtyFlag: true);

    [Fact]
    public Task SchemaRebuild_SeedFlagMissingNoDirtyRow_RefusesAsUnverifiable() =>
        RefusesAsync(false, true, Unverifiable, c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE work_items SET is_dirty = 0;";
            cmd.ExecuteNonQuery();
            DropColumn(c, "is_seed", "idx_work_items_seed");
        }, dirtyFlag: false);

    [Fact]
    public Task SchemaRebuild_DirtyFlagMissingNoSeedRow_RefusesAsUnverifiable() =>
        RefusesAsync(false, true, Unverifiable, c =>
        {
            DropColumn(c, "is_dirty", "idx_work_items_dirty");
        }, dirtyFlag: false);

    [Fact]
    public async Task SchemaRebuild_WithNonEmptyLegacyShape_RefusesAsUnverifiable()
    {
        var dir = NewDir();
        try
        {
            var s = await SeedAsync(dir, seed: false, dirty: true, c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "DROP INDEX idx_work_items_dirty; DROP INDEX idx_work_items_seed; " +
                    "ALTER TABLE work_items DROP COLUMN is_seed; ALTER TABLE work_items DROP COLUMN is_dirty;";
                cmd.ExecuteNonQuery();
            });
            AssertIntact(dir, s, seed: false, dirty: true, dirtyFlag: false);
            Refusal(dir).ShouldBe(Unverifiable);
            AssertIntact(dir, s, seed: false, dirty: true, dirtyFlag: false);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SchemaRebuild_WithEmptyLegacyShape_Rebuilds()
    {
        var dir = NewDir();
        try
        {
            await SeedAsync(dir, seed: false, dirty: false, c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "DROP INDEX idx_work_items_dirty; DROP INDEX idx_work_items_seed; " +
                    "ALTER TABLE work_items DROP COLUMN is_seed; ALTER TABLE work_items DROP COLUMN is_dirty;";
                cmd.ExecuteNonQuery();
            });
            using var reopened = new SqliteCacheStore(Cs(dir));
            reopened.SchemaWasRebuilt.ShouldBeTrue();
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SchemaRebuild_WithOnlyCleanMirrorRows_StillRebuilds()
    {
        var dir = NewDir();
        try
        {
            using (var old = new SqliteCacheStore(Cs(dir)))
            {
                await new SqliteWorkItemRepository(old, new WorkItemMapper()).SaveAsync(
                    new WorkItem { Id = 88, Title = "Clean synced item", Type = WorkItemType.Task });
                using var cmd = old.GetConnection().CreateCommand();
                cmd.CommandText = "UPDATE metadata SET value = '15' WHERE key = 'schema_version';";
                cmd.ExecuteNonQuery();
            }

            using var reopened = new SqliteCacheStore(Cs(dir));
            reopened.SchemaWasRebuilt.ShouldBeTrue();
            Convert.ToInt32(Scalar(reopened.GetConnection(), "SELECT COUNT(*) FROM work_items;")).ShouldBe(0);
        }
        finally { Cleanup(dir); }
    }

    private static void AssertEditedSeedAndContextIntact(
        string dir,
        Seeded seeded,
        string title,
        string state,
        string fieldsJson,
        string note)
    {
        AssertIntact(dir, seeded, seed: false, dirty: false);

        using var mirror = OpenReadOnly(Path.Combine(dir, "twig.db"));
        var alias = seeded.Minted.Alias.Value;
        Scalar(mirror, """
            SELECT title FROM work_items
            WHERE id = @id AND staged_identity = @identity AND is_seed = 1;
            """, ("@id", alias), ("@identity", seeded.Minted.Identity.ToString()))
            .ShouldBe(title);
        Scalar(mirror, "SELECT state FROM work_items WHERE id = @id;", ("@id", alias))
            .ShouldBe(state);
        Scalar(mirror, "SELECT fields_json FROM work_items WHERE id = @id;", ("@id", alias))
            .ShouldBe(fieldsJson);
        Convert.ToInt32(Scalar(mirror, """
            SELECT COUNT(*) FROM work_items
            WHERE id = @id AND parent_id = -73 AND assigned_to = 'seed-author'
              AND iteration_path = 'Project\Sprint 8' AND area_path = 'Project\Area 4'
              AND revision = 9 AND seed_created_at = '2026-08-14T12:34:56.0000000+00:00';
            """, ("@id", alias))).ShouldBe(1);

        Scalar(mirror, "SELECT value FROM context WHERE key = 'active_work_item_id';")
            .ShouldBe(alias.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Scalar(mirror, "SELECT value FROM context WHERE key = 'workspace_mode';")
            .ShouldBe("workspace-scoped-mode");
        Scalar(mirror, "SELECT value FROM context WHERE key = 'workspace_context_marker';")
            .ShouldBe("opaque-context-value");
        Scalar(mirror, "SELECT value FROM context WHERE key = 'nav_history_cursor';")
            .ShouldBe("401");
        Convert.ToInt32(Scalar(mirror, """
            SELECT COUNT(*) FROM navigation_history
            WHERE work_item_id = @id AND id IN (401, 402);
            """, ("@id", alias))).ShouldBe(2);

        using var durable = OpenReadOnly(Path.Combine(dir, "pending.db"));
        Convert.ToInt32(Scalar(durable, """
            SELECT COUNT(*) FROM pending_changes
            WHERE work_item_id = @id AND change_type = 'note' AND new_value = @note;
            """, ("@id", alias), ("@note", note))).ShouldBe(1);
    }
}