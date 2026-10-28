using System.Text.Json;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Providers;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Providers;

public sealed class ProviderWorkingSetSyncServiceTests
{
    [Fact]
    public async Task StorageFailureRollsBackSnapshotsAndDurableIdentityObservationsTogether()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var identities = new SqliteLocalWorkItemRepository(store);
        var first = Snapshot("I_first");
        var second = Snapshot("I_second");
        var local = await identities.ResolveAsync(first.Identity, new("before"));
        using var trigger = store.GetConnection().CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER reject_second BEFORE INSERT ON provider_work_item_snapshots
            WHEN NEW.native_id='I_second' BEGIN SELECT RAISE(ABORT,'fixture failure'); END;
            """;
        await trigger.ExecuteNonQueryAsync();
        var service = new ProviderWorkingSetSyncService(Reader(first, second), store, identities);

        await Should.ThrowAsync<SqliteException>(() => service.PullAsync(CancellationToken.None));

        (await identities.FindAsync(first.Identity)).ShouldBe(local.Id);
        (await identities.GetObservedTokenAsync(first.Identity)).ShouldNotBeNull().Token.ShouldBe(new("before"));
        (await identities.FindAsync(second.Identity)).ShouldBeNull();
        trigger.CommandText = "SELECT COUNT(*) FROM provider_work_item_snapshots;";
        Convert.ToInt32(await trigger.ExecuteScalarAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("incomplete")]
    [InlineData("duplicate")]
    public async Task InvalidWorkingSetRefusesBeforeAnyLocalObservation(string invalid)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var identities = new SqliteLocalWorkItemRepository(store);
        var snapshot = Snapshot("I_first");
        var other = invalid switch
        {
            "foreign" => snapshot with { Identity = new("github", "https://ghe.example", "I_second") },
            "incomplete" => Snapshot("I_second") with { RelationshipsComplete = false },
            _ => snapshot,
        };
        var service = new ProviderWorkingSetSyncService(Reader(snapshot, other), store, identities);

        await Should.ThrowAsync<InvalidDataException>(() => service.PullAsync(CancellationToken.None));

        (await identities.FindAsync(snapshot.Identity)).ShouldBeNull();
    }

    [Fact]
    public async Task SnapshotAndOpaqueRevisionSurviveReopenAndRefreshPreservesOwnedFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "twig-provider-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = $"Data Source={Path.Combine(directory, "twig.db")}";
        var snapshot = Snapshot("I_first");
        LocalWorkItemId localId;
        try
        {
            using (var store = new SqliteCacheStore(connection))
            {
                var identities = new SqliteLocalWorkItemRepository(store);
                var service = new ProviderWorkingSetSyncService(Reader(snapshot), store, identities);
                (await service.PullAsync(CancellationToken.None)).ShouldBe(1);
                localId = (await identities.FindAsync(snapshot.Identity)).ShouldNotBeNull();
                (await identities.UpdateAsync(localId, 1, FieldValues.Empty.Set("System.Title", new TextValue("owned"))))
                    .Value.ShouldBeOfType<LocalWriteApplied>();
                (await service.PullAsync(CancellationToken.None)).ShouldBe(1);
            }
            using (var reopened = new SqliteCacheStore(connection))
            {
                var identities = new SqliteLocalWorkItemRepository(reopened);
                (await identities.GetAsync(localId)).ShouldNotBeNull().Fields["System.Title"].Value.ShouldBe(new TextValue("owned"));
                (await identities.GetAsync(localId)).ShouldNotBeNull().Revision.ShouldBe(2);
                (await identities.GetObservedTokenAsync(snapshot.Identity)).ShouldNotBeNull().Token.ShouldBe(snapshot.RevisionToken);
                using var query = reopened.GetConnection().CreateCommand();
                query.CommandText = "SELECT snapshot_json FROM provider_work_item_snapshots;";
                var stored = JsonSerializer.Deserialize((string)(await query.ExecuteScalarAsync())!,
                    TwigJsonContext.Default.ProviderWorkItemSnapshot).ShouldNotBeNull();
                stored.Identity.ShouldBe(snapshot.Identity);
                stored.Fields.ShouldBe(snapshot.Fields);
                stored.RevisionToken.ShouldBe(snapshot.RevisionToken);
                stored.RelationshipsComplete.ShouldBeTrue();
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static ProviderWorkItemSnapshot Snapshot(string id) =>
        new(new("github", "https://api.github.com", id), new("opaque:not-an-integer"),
            FieldValues.Empty.Set("System.Title", new TextValue("remote")), [], true);

    private static IProviderWorkingSetReader Reader(params ProviderWorkItemSnapshot[] snapshots)
    {
        var reader = Substitute.For<IProviderWorkingSetReader>();
        reader.Capabilities.Returns(ProviderCapabilityCatalog.GitHub);
        reader.ResolveResourceScopeAsync(Arg.Any<CancellationToken>()).Returns("https://api.github.com");
        reader.FetchWorkingSetAsync(Arg.Any<CancellationToken>()).Returns(snapshots);
        return reader;
    }
}
