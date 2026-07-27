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
    public async Task RecordIntent_StampsATagAndLeavesTheIntentOpen()
    {
        var identity = StagedIdentity.New();

        var intent = await _repo.RecordIntentAsync(identity);

        intent.Identity.ShouldBe(identity);
        intent.IdempotencyTag.ShouldBe(PublishIntent.TagFor(identity));
        intent.IsOpen.ShouldBeTrue();
        intent.PublishedId.ShouldBeNull();
        intent.CompletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task RecordIntent_ThenComplete_ClosesTheIntent()
    {
        var identity = StagedIdentity.New();
        await _repo.RecordIntentAsync(identity);

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

        await _repo.RecordIntentAsync(open);
        await _repo.RecordIntentAsync(closed);
        await _repo.CompleteIntentAsync(closed, 7);

        var openIntents = await _repo.GetOpenIntentsAsync();

        openIntents.Select(i => i.Identity).ShouldBe([open]);
    }

    [Fact]
    public async Task RecordIntent_OnAnAlreadyOpenIntent_KeepsTheOriginalTag()
    {
        // A retry must not re-mint the tag: the recovery query would then look for a tag that
        // is not on the item the first attempt may already have created, and the duplicate this
        // ledger exists to prevent would happen anyway (PolyphonyRequiem/twig#270).
        var identity = StagedIdentity.New();
        var first = await _repo.RecordIntentAsync(identity);

        var second = await _repo.RecordIntentAsync(identity);

        second.IdempotencyTag.ShouldBe(first.IdempotencyTag);
        second.RecordedAt.ShouldBe(first.RecordedAt);
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
                await new SqlitePublishIntentRepository(store).RecordIntentAsync(identity);

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
