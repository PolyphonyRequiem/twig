using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class SqliteLocalRelationshipRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly SqliteCacheStore _store;
    private readonly SqliteLocalRelationshipRepository _repo;

    public SqliteLocalRelationshipRepositoryTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteLocalRelationshipRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    private static LocalWorkItemId Id(int n) => LocalWorkItemId.FromGuid(new Guid($"00000000-0000-7000-8000-{n:D12}"));

    private static LocalRelationship Edge(int source, int target, LinkKind kind = LinkKind.PredecessorSuccessor) =>
        new(Id(source), Id(target), kind, EmulationStorage.LocalWithProviderProjection, PublicationState.Pending, 0, Now, Now);

    [Fact]
    public async Task Upsert_RoundTrips_AndStoresCanonicalValues()
    {
        var write = await _repo.UpsertAsync(Edge(1, 2), expectedRevision: null);

        write.Status.ShouldBe(LocalRelationshipWriteStatus.Written);
        write.Current!.Revision.ShouldBe(1);
        (await _repo.GetAsync(Id(1), Id(2), LinkKind.PredecessorSuccessor)).ShouldBe(write.Current);

        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = """
            SELECT source_local_id, target_local_id, kind, storage, publication, created_at, updated_at, deleted_at
            FROM pending.local_relationships;
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe(Id(1).ToString());
        reader.GetString(1).ShouldBe(Id(2).ToString());
        reader.GetString(2).ShouldBe("predecessor-successor");
        reader.GetString(3).ShouldBe("local-with-provider-projection");
        reader.GetString(4).ShouldBe("pending");
        reader.GetString(5).ShouldBe("2026-01-02T03:04:05.0000000+00:00");
        reader.GetString(6).ShouldBe("2026-01-02T03:04:05.0000000+00:00");
        reader.IsDBNull(7).ShouldBeTrue();
    }

    [Fact]
    public async Task Upsert_EnforcesCompareAndSwap()
    {
        await _repo.UpsertAsync(Edge(1, 2), null);

        var create = await _repo.UpsertAsync(Edge(1, 2), null);
        create.Status.ShouldBe(LocalRelationshipWriteStatus.Conflict);
        create.Current!.Revision.ShouldBe(1);

        (await _repo.UpsertAsync(Edge(1, 2), 7)).Status.ShouldBe(LocalRelationshipWriteStatus.Conflict);
        (await _repo.UpsertAsync(Edge(1, 2) with { Publication = PublicationState.Published }, 1))
            .Current!.Revision.ShouldBe(2);
    }

    [Fact]
    public async Task Tombstone_SetsDeletedAt_HidesByDefault_AndBlocksBlindRecreate()
    {
        await _repo.UpsertAsync(Edge(1, 2), null);
        var later = Now.AddHours(1);

        (await _repo.TombstoneAsync(Id(1), Id(2), LinkKind.PredecessorSuccessor, 5, later)).Status.ShouldBe(LocalRelationshipWriteStatus.Conflict);
        var tomb = await _repo.TombstoneAsync(Id(1), Id(2), LinkKind.PredecessorSuccessor, 1, later);
        tomb.Current!.DeletedAt.ShouldBe(later);
        tomb.Current.Revision.ShouldBe(2);

        (await _repo.GetForItemsAsync([Id(1)])).ShouldBeEmpty();
        (await _repo.GetForItemsAsync([Id(2)], includeDeleted: true)).Single().IsDeleted.ShouldBeTrue();
        (await _repo.GetAsync(Id(1), Id(2), LinkKind.PredecessorSuccessor))!.DeletedAt.ShouldBe(later);

        (await _repo.UpsertAsync(Edge(1, 2), null)).Status.ShouldBe(LocalRelationshipWriteStatus.Conflict);
        var revived = await _repo.UpsertAsync(Edge(1, 2) with { CreatedAt = later, UpdatedAt = later }, 2);
        revived.Current!.IsDeleted.ShouldBeFalse();
        revived.Current.Revision.ShouldBe(3);
        revived.Current.CreatedAt.ShouldBe(Now);
        (await _repo.GetAsync(Id(1), Id(2), LinkKind.PredecessorSuccessor)).ShouldBe(revived.Current);
    }

    [Fact]
    public async Task Tombstone_OfMissingEdge_IsConflict() =>
        (await _repo.TombstoneAsync(Id(8), Id(9), LinkKind.Related, 1, Now)).Status.ShouldBe(LocalRelationshipWriteStatus.Conflict);

    [Fact]
    public async Task Upsert_RejectsDeletedInput() =>
        await Should.ThrowAsync<ArgumentException>(() => _repo.UpsertAsync(Edge(1, 2) with { DeletedAt = Now }, null));

    [Fact]
    public async Task Related_IsStoredOnceWithLesserEndpointFirst()
    {
        var write = await _repo.UpsertAsync(Edge(9, 4, LinkKind.Related), null);
        write.Current!.Source.ShouldBe(Id(4));
        write.Current.Target.ShouldBe(Id(9));

        (await _repo.UpsertAsync(Edge(4, 9, LinkKind.Related), null)).Status.ShouldBe(LocalRelationshipWriteStatus.Conflict);
        (await _repo.GetAsync(Id(9), Id(4), LinkKind.Related)).ShouldBe(write.Current);
        (await _repo.TombstoneAsync(Id(9), Id(4), LinkKind.Related, 1, Now)).Status.ShouldBe(LocalRelationshipWriteStatus.Written);

        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT source_local_id, target_local_id FROM pending.local_relationships;";
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe(Id(4).ToString());
        reader.GetString(1).ShouldBe(Id(9).ToString());
        reader.Read().ShouldBeFalse();
    }

    [Fact]
    public async Task DirectedKinds_KeepTheirOrder()
    {
        await _repo.UpsertAsync(Edge(9, 4), null);
        (await _repo.UpsertAsync(Edge(4, 9), null)).Status.ShouldBe(LocalRelationshipWriteStatus.Written);
        (await _repo.GetForItemsAsync([Id(4)])).Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetForItems_MatchesEitherEndpoint()
    {
        await _repo.UpsertAsync(Edge(1, 2), null);
        await _repo.UpsertAsync(Edge(3, 1, LinkKind.ParentChild), null);
        await _repo.UpsertAsync(Edge(5, 6), null);

        (await _repo.GetForItemsAsync([Id(1)])).Count.ShouldBe(2);
        (await _repo.GetForItemsAsync([])).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Related", "00000000-0000-7000-8000-000000000001")]
    [InlineData("artifact", "00000000-0000-7000-8000-000000000001")]
    [InlineData("related", "00000000-0000-7000-8000-00000000000A")]
    public async Task Read_RefusesNonCanonicalStoredValues(string kind, string source)
    {
        using (var cmd = _store.GetConnection().CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO pending.local_relationships
                    (source_local_id, target_local_id, kind, storage, publication, revision, created_at, updated_at)
                VALUES (@source, @target, @kind, 'local-only', 'not-applicable', 1, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                """;
            cmd.Parameters.AddWithValue("@source", source);
            cmd.Parameters.AddWithValue("@target", Id(2).ToString());
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.ExecuteNonQuery();
        }

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _repo.GetForItemsAsync([Id(2)]));
        ex.Message.ShouldStartWith("local-relationship-unreadable:");
    }

    [Fact]
    public async Task LocalEdges_SurviveProviderLinkReplacementForSameSource()
    {
        await _repo.UpsertAsync(Edge(1, 2), null);

        var links = new SqliteWorkItemLinkRepository(_store);
        await links.SaveLinksAsync(1, [new WorkItemLink(1, 3, LinkTypes.Related)]);
        await links.SaveLinksAsync(1, []);

        (await _repo.GetForItemsAsync([Id(1)])).Single().Target.ShouldBe(Id(2));
    }

    [Fact]
    public void Table_LivesInDurableSchema_NotMirror()
    {
        var conn = _store.GetConnection();
        TableExists(conn, "pending", "local_relationships").ShouldBeTrue();
        TableExists(conn, "main", "local_relationships").ShouldBeFalse();
    }

    [Fact]
    public async Task LocalEdges_SurviveMirrorRebuild()
    {
        await WithFileStore(async path =>
        {
            using (var store = new SqliteCacheStore($"Data Source={path}"))
            {
                await new SqliteLocalRelationshipRepository(store).UpsertAsync(Edge(1, 2), null);
                Exec(store.GetConnection(), "UPDATE metadata SET value = '0' WHERE key = 'schema_version';");
            }

            using var reopened = new SqliteCacheStore($"Data Source={path}");
            reopened.SchemaWasRebuilt.ShouldBeTrue("the mirror must actually have been rebuilt");
            var edge = await new SqliteLocalRelationshipRepository(reopened).GetAsync(Id(1), Id(2), LinkKind.PredecessorSuccessor);
            edge.ShouldNotBeNull();
            edge.Revision.ShouldBe(1);
        });
    }

    [Fact]
    public async Task DurableMigration_FromV12_AppliesNeutralTableThenAdditiveColumns()
    {
        await WithFileStore(async path =>
        {
            var identity = StagedIdentity.New();
            using (var store = new SqliteCacheStore($"Data Source={path}"))
            {
                await new SqlitePublishIntentRepository(store).RecordIntentAsync(identity, "Keep me", "Task");
                var conn = store.GetConnection();
                Exec(conn, "DROP TABLE pending.local_relationships;");
                Exec(conn, "DROP TABLE pending.local_work_items;");
                Exec(conn, "DROP TABLE pending.external_identities;");
                Exec(conn, "PRAGMA pending.user_version = 12;");
            }

            using var reopened = new SqliteCacheStore($"Data Source={path}");
            var c = reopened.GetConnection();
            TableExists(c, "pending", "local_relationships").ShouldBeTrue();
            TableExists(c, "pending", "local_work_items").ShouldBeTrue();
            Columns(c).ShouldBe(
            [
                "source_local_id", "target_local_id", "kind", "revision", "created_at", "updated_at", "deleted_at",
                "storage", "publication",
            ]);
            DurableVersion(c).ShouldBe(SqliteCacheStore.DurableSchemaVersion);
            (await new SqlitePublishIntentRepository(reopened).GetIntentAsync(identity)).ShouldNotBeNull();
        });
    }

    [Fact]
    public async Task DurableMigration_FromNeutralV13_AddsColumnsAdditivelyAndKeepsRows()
    {
        await WithFileStore(async path =>
        {
            using (var store = new SqliteCacheStore($"Data Source={path}"))
            {
                var conn = store.GetConnection();
                Exec(conn, "DROP TABLE pending.local_relationships;");
                // #1168's v13 DDL, verbatim, with a neutral-layer row that carries no emulation policy.
                Exec(conn, $"""
                    CREATE TABLE pending.local_relationships (
                      source_local_id TEXT NOT NULL,
                      target_local_id TEXT NOT NULL,
                      kind TEXT NOT NULL,
                      revision INTEGER NOT NULL CHECK (revision >= 1),
                      created_at TEXT NOT NULL,
                      updated_at TEXT NOT NULL,
                      deleted_at TEXT,
                      PRIMARY KEY (source_local_id, target_local_id, kind)
                    );
                    CREATE INDEX pending.idx_local_relationships_target ON local_relationships(target_local_id);
                    INSERT INTO pending.local_relationships
                    VALUES ('{Id(7)}', '{Id(8)}', 'related', 4, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00', NULL);
                    PRAGMA pending.user_version = 13;
                    """);
            }

            using var reopened = new SqliteCacheStore($"Data Source={path}");
            var c = reopened.GetConnection();
            DurableVersion(c).ShouldBe(SqliteCacheStore.DurableSchemaVersion);
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT revision, storage IS NULL, publication IS NULL
                    FROM pending.local_relationships WHERE source_local_id = '{Id(7)}' AND target_local_id = '{Id(8)}';
                    """;
                using var reader = cmd.ExecuteReader();
                reader.Read().ShouldBeTrue();
                reader.GetInt64(0).ShouldBe(4);
                reader.GetBoolean(1).ShouldBeTrue();
                reader.GetBoolean(2).ShouldBeTrue();
            }

            var repo = new SqliteLocalRelationshipRepository(reopened);
            var write = await repo.UpsertAsync(Edge(1, 2), expectedRevision: null);
            write.Status.ShouldBe(LocalRelationshipWriteStatus.Written);
            (await repo.GetAsync(Id(1), Id(2), LinkKind.PredecessorSuccessor)).ShouldBe(write.Current);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableMigration_ReplayingV14OverExistingColumns_FailsLoudlyAndRollsBack(bool collideOnPublication)
    {
        // Repro for the Migration_To* fixture failure: a current store stamped back below 14
        // while keeping v14's columns. Production must refuse rather than mask the collision,
        // and the failed upgrade must not advance the stamped version.
        await WithFileStore(path =>
        {
            using (var store = new SqliteCacheStore($"Data Source={path}"))
            {
                var conn = store.GetConnection();
                if (collideOnPublication)
                    Exec(conn, "ALTER TABLE pending.local_relationships DROP COLUMN storage;");
                Exec(conn, "PRAGMA pending.user_version = 13;");
            }

            var ex = Should.Throw<InvalidOperationException>(() => new SqliteCacheStore($"Data Source={path}").Dispose());
            var sqliteError = ex.InnerException.ShouldBeOfType<SqliteException>();
            sqliteError.SqliteErrorCode.ShouldBe(1);
            sqliteError.Message.ShouldContain($"duplicate column name: {(collideOnPublication ? "publication" : "storage")}");

            SqliteConnection.ClearAllPools();
            using var raw = new SqliteConnection($"Data Source={Path.Combine(Path.GetDirectoryName(path)!, "pending.db")}");
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(13);
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('local_relationships') WHERE name = 'storage';";
            Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(collideOnPublication ? 0 : 1,
                "failure on the second ALTER must roll back the first ALTER");
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('local_relationships') WHERE name = 'publication';";
            Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(1);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Read_RowWithoutEmulationColumns_IsUnreadable()
    {
        Exec(_store.GetConnection(), $"""
            INSERT INTO pending.local_relationships (source_local_id, target_local_id, kind, revision, created_at, updated_at)
            VALUES ('{Id(1)}', '{Id(2)}', 'predecessor-successor', 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _repo.GetAsync(Id(1), Id(2), LinkKind.PredecessorSuccessor));
        ex.Message.ShouldStartWith("local-relationship-unreadable:");
    }

    [Fact]
    public void Schema_RejectsRevisionBelowOne() =>
        Should.Throw<SqliteException>(() => Exec(_store.GetConnection(), $"""
            INSERT INTO pending.local_relationships (source_local_id, target_local_id, kind, revision, created_at, updated_at, storage, publication)
            VALUES ('{Id(1)}', '{Id(2)}', 'related', 0, 'x', 'x', 'local-only', 'not-applicable');
            """));

    private static List<string> Columns(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info('local_relationships', 'pending');";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static int DurableVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA pending.user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static async Task WithFileStore(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"twig-localrel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await body(Path.Combine(dir, "twig.db"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection conn, string schema, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM {schema}.sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() is not null;
    }
}
