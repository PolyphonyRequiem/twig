using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Epic #1168 integrate-ado-store: ADO items written to the disposable mirror are projected onto a
/// durable local identity in the same transaction.
/// </summary>
public sealed class AdoMirrorIdentityProjectionTests : IDisposable
{
    private const string Scope = "test-collection-scope";

    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly SqliteLocalWorkItemRepository _identities;
    private readonly SqliteWorkItemRepository _mirror;

    public AdoMirrorIdentityProjectionTests()
    {
        _identities = new SqliteLocalWorkItemRepository(_store);
        _mirror = Projected(_store, _identities, Scope);
    }

    public void Dispose() => _store.Dispose();

    private static SqliteWorkItemRepository Projected(SqliteCacheStore store, SqliteLocalWorkItemRepository identities, string scope)
        => new(store, new WorkItemMapper(), new AdoMirrorIdentityProjection(identities, scope));

    private static ExternalWorkItemIdentity Ado(int id, string scope = Scope) => new("ado", scope, id.ToString());

    private static WorkItem Synced(int id, int revision)
    {
        var item = new WorkItemBuilder(id, "Item " + id).Build();
        item.MarkSynced(revision);
        return item;
    }

    [Fact]
    public async Task Save_ProjectsOntoOneStableLocalId_AndTracksTheLatestRevisionToken()
    {
        await _mirror.SaveAsync(Synced(42, 3));
        var first = await _identities.FindAsync(Ado(42));
        first.ShouldNotBeNull();
        (await _identities.GetObservedTokenAsync(Ado(42)))!.Token.ShouldBe(new ProviderRevisionToken("3"));

        await _mirror.SaveBatchAsync([Synced(42, 5), Synced(43, 1)]);

        (await _identities.FindAsync(Ado(42))).ShouldBe(first);
        (await _identities.GetObservedTokenAsync(Ado(42)))!.Token.ShouldBe(new ProviderRevisionToken("5"));
        (await _identities.FindAsync(Ado(43))).ShouldNotBeNull();
        CountRows("pending.local_work_items").ShouldBe(2);
        (await _identities.GetExternalIdentitiesAsync(first.Value)).ShouldBe([Ado(42)]);
    }

    [Fact]
    public async Task UnsyncedRevision_IsRecordedAsNoToken()
    {
        await _mirror.SaveAsync(new WorkItemBuilder(7, "Never synced").Build());

        var observed = await _identities.GetObservedTokenAsync(Ado(7));
        observed.ShouldNotBeNull();
        observed.Token.ShouldBeNull();
    }

    [Fact]
    public async Task Seeds_HaveNoAdoIdentity_AndAreNotProjected()
    {
        await _mirror.SaveBatchAsync([new WorkItemBuilder(-1, "Seed").AsSeed().Build()]);

        CountRows("main.work_items").ShouldBe(1);
        CountRows("pending.external_identities").ShouldBe(0);
        CountRows("pending.local_work_items").ShouldBe(0);
    }

    [Fact]
    public async Task MirrorDeletion_NeverChangesTheLocalIdentity()
    {
        await _mirror.SaveAsync(Synced(42, 1));
        var before = await _identities.FindAsync(Ado(42));

        await _mirror.DeleteByIdAsync(42);
        await _mirror.EvictExceptAsync(new HashSet<int>());
        await _mirror.SaveAsync(Synced(42, 2));

        (await _identities.FindAsync(Ado(42))).ShouldBe(before);
        CountRows("pending.local_work_items").ShouldBe(1);
    }

    [Fact]
    public async Task SameNativeId_InAnotherScope_IsADistinctIdentity()
    {
        await _mirror.SaveAsync(Synced(42, 1));
        await Projected(_store, _identities, "other-scope").SaveAsync(Synced(42, 1));

        (await _identities.FindAsync(Ado(42, "other-scope"))).ShouldNotBe(await _identities.FindAsync(Ado(42)));
    }

    [Fact]
    public async Task AmbientRollback_UndoesTheMirrorWriteAndTheProjectionTogether()
    {
        var conn = _store.GetConnection();
        using (var tx = conn.BeginTransaction())
        {
            _store.ActiveTransaction = tx;
            try
            {
                await _mirror.SaveBatchAsync([Synced(42, 1)]);
                tx.Rollback();
            }
            finally { _store.ActiveTransaction = null; }
        }

        CountRows("main.work_items").ShouldBe(0);
        CountRows("pending.external_identities").ShouldBe(0);
        CountRows("pending.local_work_items").ShouldBe(0);
    }

    [Fact]
    public async Task FailedBatch_RollsBackEveryMirrorRowAndMapping()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => _mirror.SaveBatchAsync([Synced(42, 1)], cts.Token));

        _store.ActiveTransaction.ShouldBeNull();
        CountRows("main.work_items").ShouldBe(0);
        CountRows("pending.external_identities").ShouldBe(0);
        CountRows("pending.local_work_items").ShouldBe(0);
    }

    [Fact]
    public async Task ProjectionIsOff_ForThePublicConstructor()
    {
        await new SqliteWorkItemRepository(_store, new WorkItemMapper()).SaveAsync(Synced(42, 1));

        CountRows("main.work_items").ShouldBe(1);
        CountRows("pending.external_identities").ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" padded")]
    public void Projection_RejectsAnInvalidScope_InsteadOfGuessingOne(string scope)
        => Should.Throw<ArgumentException>(() => new AdoMirrorIdentityProjection(_identities, scope));

    [Fact]
    public async Task MirrorRebuild_ReobservesTheSameLocalIdentity()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"twig_1168_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var source = $"Data Source={Path.Combine(dir, "twig.db")}";
        try
        {
            LocalWorkItemId? before;
            using (var store = new SqliteCacheStore(source))
            {
                var identities = new SqliteLocalWorkItemRepository(store);
                await Projected(store, identities, Scope).SaveAsync(Synced(42, 1));
                before = await identities.FindAsync(Ado(42));

                using var cmd = store.GetConnection().CreateCommand();
                cmd.CommandText = "UPDATE main.metadata SET value = '0' WHERE key = 'schema_version';";
                cmd.ExecuteNonQuery().ShouldBe(1);
            }

            using (var store = new SqliteCacheStore(source))
            {
                store.SchemaWasRebuilt.ShouldBeTrue();
                var identities = new SqliteLocalWorkItemRepository(store);
                var mirror = Projected(store, identities, Scope);
                (await mirror.GetByIdAsync(42)).ShouldBeNull();

                await mirror.SaveAsync(Synced(42, 2));

                before.ShouldNotBeNull();
                (await identities.FindAsync(Ado(42))).ShouldBe(before);
                (await identities.GetObservedTokenAsync(Ado(42)))!.Token.ShouldBe(new ProviderRevisionToken("2"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private long CountRows(string table)
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
