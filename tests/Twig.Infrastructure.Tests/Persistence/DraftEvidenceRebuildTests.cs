using System.Globalization;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// A permitted clean mirror rebuild drops the mirror-resident served-evidence metadata but must leave the
/// durable recoverable draft and the durable Bench untouched; only an explicit inspection (RecordAsync) may admit
/// evidence again. Fixtures are real current-version stores with schema_version lowered to '15', not captured
/// historical databases.
/// </summary>
public sealed class DraftEvidenceRebuildTests : IDisposable
{
    private const string Unavailable = "metadata-unavailable: explicitly inspect this draft's native served form first.";
    private const string Stale = "metadata-stale: qualified native observation or local schema identity changed.";
    private const string BenchName = "inspected";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"twig-draft-evidence-rebuild-{Guid.NewGuid():N}");
    private QualifiedWorkItemKey Key => new(_directory, "connection", "binding", 42);
    private PlanOrigin Origin => new(_directory, "fingerprint", 1, "connection", "binding", 1,
        "selection", 1, "identity", "method", "credential", "tenant", "object", "issuer", "authority", null);
    private string DbPath => Path.Combine(_directory, "twig.db");
    private string ConnectionString => $"Data Source={DbPath};Pooling=False";

    public DraftEvidenceRebuildTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task CleanRebuild_PreservesDurableDraftExactly_RemovesServedEvidenceMetadata()
    {
        string draftBefore;
        using (var cache = new SqliteCacheStore(ConnectionString))
        {
            await SeedInitialAsync(cache);
            var persisted = (await new SqliteRecoverableWorkItemDraftStore(cache).GetAsync(Key)).ShouldNotBeNull();
            persisted.ItemKey.ShouldBe(Key);
            persisted.Generation.ShouldBeGreaterThan(0);
            draftBefore = Describe(persisted);
            draftBefore.ShouldBe($"{Key}|5|5|1|System.Title:baseline->intent|0");
            ServedEvidenceRows(cache).ShouldBe(1);
            Scalar(cache, "SELECT COUNT(*) FROM metadata WHERE key='binding_generation';").ShouldBe("0");
            Scalar(cache, "SELECT value FROM metadata WHERE key='schema_version';").ShouldBe("17");
            Scalar(cache, "SELECT is_seed FROM work_items WHERE id=42;").ShouldBe("0");
            Scalar(cache, "SELECT is_dirty FROM work_items WHERE id=42;").ShouldBe("0");
            Execute(cache, "UPDATE metadata SET value='15' WHERE key='schema_version';");
        }

        using (var rebuilt = new SqliteCacheStore(ConnectionString))
        {
            Scalar(rebuilt, "SELECT value FROM metadata WHERE key='schema_version';").ShouldBe("17");
            Scalar(rebuilt, "SELECT COUNT(*) FROM work_items;").ShouldBe("0");
            ServedEvidenceRows(rebuilt).ShouldBe(0);
            Scalar(rebuilt, "SELECT COUNT(*) FROM metadata WHERE key='binding_generation';").ShouldBe("0");
            Should.Throw<InvalidOperationException>(() => new SqliteDraftCaptureEvidenceStore(rebuilt).Read(Key))
                .Message.ShouldBe(Unavailable);
            var retained = (await new SqliteRecoverableWorkItemDraftStore(rebuilt).GetAsync(Key)).ShouldNotBeNull();
            Describe(retained).ShouldBe(draftBefore);
        }

        SqliteConnection.ClearAllPools();
        using var raw = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
        raw.Open();
        using var command = raw.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM metadata WHERE key LIKE 'draft-served-evidence:%';";
        Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(0);
    }

    [Fact]
    public async Task RawReseedAlone_DoesNotRestoreObservation_ExplicitRecordDoes()
    {
        string draftBefore;
        string benchBefore;
        using (var cache = new SqliteCacheStore(ConnectionString))
        {
            await SeedInitialAsync(cache);
            draftBefore = Describe((await new SqliteRecoverableWorkItemDraftStore(cache).GetAsync(Key)).ShouldNotBeNull());
            benchBefore = Describe((await new SqliteBenchRepository(cache).GetByNameAsync(BenchName)).ShouldNotBeNull());
            benchBefore.ShouldEndWith($"|{BenchName}|False|Item:42");
            Execute(cache, "UPDATE metadata SET value='15' WHERE key='schema_version';");
        }

        using var rebuilt = new SqliteCacheStore(ConnectionString);
        var retained = (await new SqliteRecoverableWorkItemDraftStore(rebuilt).GetAsync(Key)).ShouldNotBeNull();
        var bench = (await new SqliteBenchRepository(rebuilt).GetByNameAsync(BenchName)).ShouldNotBeNull();
        Describe(bench).ShouldBe(benchBefore);
        var snapshot = await ReseedMirrorAsync(rebuilt);
        var source = new SqliteDraftCaptureEvidenceStore(rebuilt);

        Should.Throw<InvalidOperationException>(() => source.Read(Key)).Message.ShouldBe(Unavailable);
        (await Should.ThrowAsync<InvalidOperationException>(
            () => source.ValidateAsync(retained, snapshot, Origin, "any-prior-fingerprint", default)))
            .Message.ShouldBe(Unavailable);

        var evidence = BuildEvidence(source, snapshot, bench);
        await source.RecordAsync(evidence, snapshot, default);
        var fingerprint = SqliteDraftCaptureEvidenceStore.Fingerprint(evidence);
        SqliteDraftCaptureEvidenceStore.Fingerprint(source.Read(Key)).ShouldBe(fingerprint);
        await source.ValidateAsync(retained, snapshot, Origin, fingerprint, default);
        Describe((await new SqliteRecoverableWorkItemDraftStore(rebuilt).GetAsync(Key)).ShouldNotBeNull()).ShouldBe(draftBefore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedEvidencedInputAfterReinspection_IsRejected(bool bumpRevision)
    {
        using (var cache = new SqliteCacheStore(ConnectionString))
        {
            await SeedInitialAsync(cache);
            Execute(cache, "UPDATE metadata SET value='15' WHERE key='schema_version';");
        }

        using var rebuilt = new SqliteCacheStore(ConnectionString);
        var retained = (await new SqliteRecoverableWorkItemDraftStore(rebuilt).GetAsync(Key)).ShouldNotBeNull();
        var draftBefore = Describe(retained);
        var bench = (await new SqliteBenchRepository(rebuilt).GetByNameAsync(BenchName)).ShouldNotBeNull();
        var benchBefore = Describe(bench);
        var snapshot = await ReseedMirrorAsync(rebuilt);
        var source = new SqliteDraftCaptureEvidenceStore(rebuilt);
        var evidence = BuildEvidence(source, snapshot, bench);
        await source.RecordAsync(evidence, snapshot, default);
        var fingerprint = SqliteDraftCaptureEvidenceStore.Fingerprint(evidence);
        await source.ValidateAsync(retained, snapshot, Origin, fingerprint, default);

        var current = snapshot;
        if (bumpRevision)
        {
            var repository = new SqliteWorkItemRepository(rebuilt, new WorkItemMapper());
            await repository.SaveAsync(new WorkItemMapper().Map(snapshot with { Revision = 6 }));
            current = (await repository.GetSnapshotByIdAsync(42))!;
            current.Revision.ShouldBe(6);
        }
        else
        {
            source.ProcessFingerprint().ShouldBe(evidence.ProcessFingerprint);
            Execute(rebuilt, "UPDATE process_types SET icon_id='changed';");
            source.ProcessFingerprint().ShouldNotBe(evidence.ProcessFingerprint);
        }

        (await Should.ThrowAsync<InvalidOperationException>(
            () => source.ValidateAsync(retained, current, Origin, fingerprint, default))).Message.ShouldBe(Stale);
        Describe((await new SqliteRecoverableWorkItemDraftStore(rebuilt).GetAsync(Key)).ShouldNotBeNull()).ShouldBe(draftBefore);
        Describe((await new SqliteBenchRepository(rebuilt).GetByNameAsync(BenchName)).ShouldNotBeNull()).ShouldBe(benchBefore);
    }

    /// <summary>Initial setup only: creates the mirror rows, the durable Bench and draft, and records evidence.</summary>
    private async Task SeedInitialAsync(SqliteCacheStore cache)
    {
        var snapshot = await ReseedMirrorAsync(cache);
        var benches = new SqliteBenchRepository(cache);
        var created = (await benches.CreateAsync(BenchName)).ShouldNotBeNull();
        await benches.AddSelectorAsync(created.Id, BenchSelector.ForItem(42));
        var bench = (await benches.GetByNameAsync(BenchName)).ShouldNotBeNull();
        var draft = RecoverableWorkItemDraft.Create(Key, 5).SetField("System.Title", "baseline", "intent");
        (await new SqliteRecoverableWorkItemDraftStore(cache).TrySaveAsync(draft, null)).ShouldBeTrue();
        var source = new SqliteDraftCaptureEvidenceStore(cache);
        await source.RecordAsync(BuildEvidence(source, snapshot, bench), snapshot, default);
    }

    /// <summary>Mirror-only reseed (work item and process types); touches no durable table.</summary>
    private static async Task<WorkItemSnapshot> ReseedMirrorAsync(SqliteCacheStore cache)
    {
        var repository = new SqliteWorkItemRepository(cache, new WorkItemMapper());
        await repository.SaveAsync(new WorkItemMapper().Map(new WorkItemSnapshot
        {
            Id = 42, Revision = 5, TypeName = "FixtureType", Title = "baseline", State = "Before",
            Fields = new Dictionary<string, string?> { ["Custom.Unsupported"] = "baseline" },
        }));
        await new SqliteProcessTypeStore(cache).SaveAsync(new ProcessTypeRecord
        {
            TypeName = "FixtureType", States = [new("Before", StateCategory.Proposed, null), new("After", StateCategory.Completed, null)],
        });
        return (await repository.GetSnapshotByIdAsync(42))!;
    }

    private DraftCaptureEvidence BuildEvidence(SqliteDraftCaptureEvidenceStore source, WorkItemSnapshot snapshot, Bench bench)
        => new(1, Key, Origin, null, snapshot.TypeName, 5,
            source.ProcessFingerprint(), SqliteDraftCaptureEvidenceStore.SnapshotFingerprint(snapshot),
            new FormLayout("Fixture.Reference", "process", [])
            {
                SystemControls = new[] { "System.Title", "System.AssignedTo", "System.State", "Custom.Unsupported" }
                    .Select(name => new LayoutControl(name, name, "FieldControl", false, true, false)).ToArray(),
            }, [], bench, null);

    private static string Describe(RecoverableWorkItemDraft draft)
        => $"{draft.ItemKey}|{draft.BaselineRevision}|{draft.ObservedRevision}|{draft.Generation}|"
           + string.Join(";", draft.Changes.Select(c => $"{c.FieldRef}:{c.BaselineValue}->{c.ProposedValue}"))
           + $"|{draft.Conflicts.Count}";

    private static string Describe(Bench bench)
        => $"{bench.Id}|{bench.Name}|{bench.IsDefault}|"
           + string.Join(";", bench.Selectors.Select(s => $"{s.Kind}:{s.Payload}").OrderBy(s => s, StringComparer.Ordinal));

    private static int ServedEvidenceRows(SqliteCacheStore cache)
        => int.Parse(Scalar(cache, "SELECT COUNT(*) FROM metadata WHERE key LIKE 'draft-served-evidence:%';"), CultureInfo.InvariantCulture);

    private static string Scalar(SqliteCacheStore cache, string sql)
    {
        using var command = cache.GetConnection().CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!;
    }

    private static void Execute(SqliteCacheStore cache, string sql)
    {
        using var command = cache.GetConnection().CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
