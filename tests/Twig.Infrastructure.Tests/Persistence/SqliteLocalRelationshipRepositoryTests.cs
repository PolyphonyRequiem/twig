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

    private static LocalWorkItemId Id(int value) =>
        LocalWorkItemId.FromGuid(Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));

    private static LocalRelationship Edge(int source, int target, LinkKind kind = LinkKind.PredecessorSuccessor) =>
        new(Id(source), Id(target), kind, EmulationStorage.LocalWithProviderProjection,
            PublicationState.Pending, 0, Now, Now, null);

    [Fact]
    public async Task Upsert_RoundTrips_AndStoresCanonicalTokens()
    {
        var write = await _repo.UpsertAsync(Edge(1, 2), expectedRevision: null);

        write.Status.ShouldBe(LocalRelationshipWriteStatus.Written);
        write.Current!.Revision.ShouldBe(1);
        (await _repo.GetAsync(Id(1), Id(2), LinkKind.PredecessorSuccessor)).ShouldBe(write.Current);

        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT kind, storage, publication FROM pending.local_relationships;";
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("predecessor-successor");
        reader.GetString(1).ShouldBe("local-with-provider-projection");
        reader.GetString(2).ShouldBe("pending");
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
    public async Task Tombstone_HidesByDefault_AndBlocksBlindRecreate()
    {
        await _repo.UpsertAsync(Edge(1, 2), null);

        (await _repo.TombstoneAsync(Id(1), Id(2), LinkKind.PredecessorSuccessor, 5, Now)).Status.ShouldBe(LocalRelationshipWriteStatus.Conflict);
        var tomb = await _repo.TombstoneAsync(Id(1), Id(2), LinkKind.PredecessorSuccessor, 1, Now);
        tomb.Current!.Deleted.ShouldBeTrue();
        tomb.Current.Revision.ShouldBe(2);

        (await _repo.GetForItemsAsync([Id(1)])).ShouldBeEmpty();
        (await _repo.GetForItemsAsync([Id(2)], includeDeleted: true)).Single().Deleted.ShouldBeTrue();
        (await _repo.GetAsync(Id(1), Id(2), LinkKind.PredecessorSuccessor))!.Deleted.ShouldBeTrue();

        (await _repo.UpsertAsync(Edge(1, 2), null)).Status.ShouldBe(LocalRelationshipWriteStatus.Conflict);
        var revived = await _repo.UpsertAsync(Edge(1, 2), 2);
        revived.Current!.Deleted.ShouldBeFalse();
        revived.Current.Revision.ShouldBe(3);
    }

    [Fact]
    public async Task Tombstone_OfMissingEdge_IsConflict() =>
        (await _repo.TombstoneAsync(Id(8), Id(9), LinkKind.Related, 1, Now)).Status.ShouldBe(LocalRelationshipWriteStatus.Conflict);

    [Fact]
    public async Task Upsert_RejectsDeletedInput() =>
        await Should.ThrowAsync<ArgumentException>(() => _repo.UpsertAsync(Edge(1, 2) with { DeletedAt = Now }, null));

    [Fact]
    public async Task GetForItems_MatchesEitherEndpoint()
    {
        await _repo.UpsertAsync(Edge(1, 2), null);
        await _repo.UpsertAsync(Edge(3, 1, LinkKind.ParentChild), null);
        await _repo.UpsertAsync(Edge(5, 6), null);

        (await _repo.GetForItemsAsync([Id(1)])).Count.ShouldBe(2);
        (await _repo.GetForItemsAsync([])).ShouldBeEmpty();
    }

    [Fact]
    public async Task Read_RefusesNonCanonicalStoredToken()
    {
        using (var cmd = _store.GetConnection().CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO pending.local_relationships
                    (source_local_id, target_local_id, kind, storage, publication, revision, created_at, updated_at, deleted_at)
                VALUES (@source, @target, 'Related', 'none', 'not-applicable', 1,
                    '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00', NULL);
                """;
            cmd.Parameters.AddWithValue("@source", Id(1).ToString());
            cmd.Parameters.AddWithValue("@target", Id(2).ToString());
            cmd.ExecuteNonQuery();
        }

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _repo.GetForItemsAsync([Id(1)]));
        ex.Message.ShouldStartWith("local-relationship-unreadable:");
    }

    [Fact]
    public async Task LocalEdges_SurviveProviderLinkReplacementForSameSource()
    {
        await _repo.UpsertAsync(Edge(1, 2), null);

        var links = new SqliteWorkItemLinkRepository(_store);
        await links.SaveLinksAsync(1, [new WorkItemLink(1, 3, LinkTypes.Related)]);
        await links.SaveLinksAsync(1, []);

        (await _repo.GetForItemsAsync([Id(1)])).Single().TargetId.ShouldBe(Id(2));
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
    public async Task DurableMigration_FromPreviousVersion_AddsTableAndKeepsExistingRows()
    {
        await WithFileStore(async path =>
        {
            var identity = StagedIdentity.New();
            using (var store = new SqliteCacheStore($"Data Source={path}"))
            {
                await new SqlitePublishIntentRepository(store).RecordIntentAsync(identity, "Keep me", "Task");
                var conn = store.GetConnection();
                Exec(conn, "DROP TABLE pending.local_relationships;");
                Exec(conn, $"PRAGMA pending.user_version = {SqliteCacheStore.DurableSchemaVersion - 1};");
            }

            using var reopened = new SqliteCacheStore($"Data Source={path}");
            var c = reopened.GetConnection();
            TableExists(c, "pending", "local_relationships").ShouldBeTrue();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "PRAGMA pending.user_version;";
                Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(SqliteCacheStore.DurableSchemaVersion);
            }

            (await new SqlitePublishIntentRepository(reopened).GetIntentAsync(identity)).ShouldNotBeNull();
        });
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
