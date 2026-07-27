using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests for <see cref="SqlitePublishIntentRepository"/> — the durable intent
/// ledger added by wayfinder 0015. Uses :memory: databases for isolation.
/// </summary>
public class SqlitePublishIntentRepositoryTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqlitePublishIntentRepository _repo;

    public SqlitePublishIntentRepositoryTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqlitePublishIntentRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task RecordIntent_PERSISTS_TheIntentAndLeavesItOpen()
    {
        var identity = StagedIdentity.New();

        var returned = await _repo.RecordIntentAsync(identity, "Ship the thing", "Task");

        // Assert on what was READ BACK, not on the object handed to us. An earlier version of
        // this test asserted only the returned in-memory instance, which is built BEFORE the
        // INSERT — deleting the INSERT entirely left it green. It was also the only test named
        // for that write path.
        var persisted = await _repo.GetIntentAsync(identity);

        persisted.ShouldNotBeNull("the intent must actually reach the durable store");
        persisted.Identity.ShouldBe(identity);
        persisted.Title.ShouldBe("Ship the thing");
        persisted.TypeName.ShouldBe("Task");
        persisted.IsOpen.ShouldBeTrue();
        persisted.PublishedId.ShouldBeNull();
        persisted.CompletedAt.ShouldBeNull();

        // The returned instance must describe the same row, or callers fencing on RecordedAt
        // would be fencing on a value the store does not hold.
        persisted.RecordedAt.ShouldBe(returned.RecordedAt);
    }

    [Fact]
    public async Task RecordIntent_ThenComplete_ClosesTheIntent()
    {
        var identity = StagedIdentity.New();
        await _repo.RecordIntentAsync(identity, "Ship the thing", "Task");

        await _repo.CompleteIntentAsync(identity, 4242);

        var stored = await _repo.GetIntentAsync(identity);
        stored.ShouldNotBeNull();
        stored.IsOpen.ShouldBeFalse();
        stored.PublishedId.ShouldBe(4242);
        stored.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetOpenIntents_ReturnsOnlyThoseWithNoRecordedOutcome()
    {
        var open = StagedIdentity.New();
        var closed = StagedIdentity.New();

        await _repo.RecordIntentAsync(open, "Open one", "Task");
        await _repo.RecordIntentAsync(closed, "Closed one", "Bug");
        await _repo.CompleteIntentAsync(closed, 7);

        var openIntents = await _repo.GetOpenIntentsAsync();

        openIntents.Select(i => i.Identity).ShouldBe([open]);
    }

    [Fact]
    public async Task RecordIntent_OnAnAlreadyOpenIntent_KeepsTheOriginalRecordedAt()
    {
        // A retry must not re-stamp RecordedAt. It is the lower bound the recovery query fences
        // on, so moving it forward would push the fence PAST the create the first attempt may
        // already have made — the orphan stops being findable and the duplicate this ledger
        // exists to prevent happens anyway (PolyphonyRequiem/twig#270).
        var identity = StagedIdentity.New();
        var first = await _repo.RecordIntentAsync(identity, "Ship the thing", "Task");

        var second = await _repo.RecordIntentAsync(identity, "Ship the thing", "Task");

        second.RecordedAt.ShouldBe(first.RecordedAt);
    }

    [Fact]
    public async Task RecordIntent_OnACOMPLETEDIntent_PreservesThePublishedId()
    {
        // THE BUG REVIEW CAUGHT. An earlier version preserved an existing row only when
        // `IsOpen`, so a COMPLETED intent fell through to `ON CONFLICT DO UPDATE`, which reset
        // published_id to NULL and re-stamped recorded_at. That destroyed the only surviving
        // proof the ADO item existed AND moved the recovery fence past it — so the retry created
        // a duplicate, in exactly the #270 scenario this ledger exists to prevent.
        var identity = StagedIdentity.New();
        var first = await _repo.RecordIntentAsync(identity, "Ship the thing", "Task");
        await _repo.CompleteIntentAsync(identity, 4242);

        var second = await _repo.RecordIntentAsync(identity, "Ship the thing", "Task");

        second.PublishedId.ShouldBe(4242, "the recorded outcome is the evidence recovery adopts");
        second.RecordedAt.ShouldBe(first.RecordedAt, "re-stamping the fence would skip the orphan");
        second.IsOpen.ShouldBeFalse();

        // And it must be true of the persisted row, not just the returned instance.
        var persisted = await _repo.GetIntentAsync(identity);
        persisted.ShouldNotBeNull();
        persisted.PublishedId.ShouldBe(4242);
    }

    [Fact]
    public async Task GetIntent_ForAnUnrecordedIdentity_ReturnsNull()
    {
        var stored = await _repo.GetIntentAsync(StagedIdentity.New());

        stored.ShouldBeNull();
    }

    [Fact]
    public async Task IntentsSurviveAMirrorRebuild()
    {
        // 0013's durability test: the intent record lives in the sibling pending.db, which a
        // mirror drop/rebuild must not be able to reach. A record erased by the very crash it
        // exists to survive would be worthless.
        //
        // This must be FILE-backed. An in-memory mirror gets a private in-memory durable store
        // (SqliteCacheStore.DeriveDurableDataSource), so a reopened store would get a fresh one
        // and the test would fail for a reason that has nothing to do with durability.
        var dir = Path.Combine(Path.GetTempPath(), $"twig-intent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "twig.db");

        try
        {
            var identity = StagedIdentity.New();

            using (var store = new SqliteCacheStore($"Data Source={dbPath}"))
            {
                await new SqlitePublishIntentRepository(store)
                    .RecordIntentAsync(identity, "Ship the thing", "Task");

                // Force the mirror to be dropped and recreated on the next open.
                using var bump = store.GetConnection().CreateCommand();
                bump.CommandText = "UPDATE metadata SET value = '0' WHERE key = 'schema_version';";
                bump.ExecuteNonQuery();
            }

            using (var reopened = new SqliteCacheStore($"Data Source={dbPath}"))
            {
                // Fixture guard: if the mirror were NOT rebuilt this test proves nothing, since
                // surviving a no-op is not surviving anything.
                reopened.SchemaWasRebuilt.ShouldBeTrue("the mirror must actually have been rebuilt");

                var stored = await new SqlitePublishIntentRepository(reopened).GetIntentAsync(identity);

                stored.ShouldNotBeNull();
                stored.IsOpen.ShouldBeTrue();
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
