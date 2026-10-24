using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class DraftProposalCaptureStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"twig-capture-{Guid.NewGuid():N}");
    private string ConnectionString => $"Data Source={Path.Combine(_directory, "twig.db")};Pooling=False;Default Timeout=1";
    private string ExportDirectory => Path.Combine(_directory, "proposals");
    private QualifiedWorkItemKey Key => new(_directory, "connection", "binding", 42);
    private PlanOrigin Origin => new(_directory, "fingerprint", 1, "connection", "binding", 1,
        "selection", 1, "identity", "method", "credential", "tenant", "object", "issuer", "authority", null);
    private static PlanWorkspace Workspace => new() { Organization = "scope", Project = "project" };

    public DraftProposalCaptureStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ExactReplaySurvivesReopenWithoutImportRefreshOrExport()
    {
        PlanJournal first;
        RecoverableWorkItemDraft draft;
        WorkItemSnapshot snapshot;
        using (var cache = new SqliteCacheStore(ConnectionString))
        {
            (draft, snapshot) = await SeedAsync(cache);
            first = await CaptureAsync(cache, draft, snapshot);
            first.DraftCapture.ShouldNotBeNull();
            first.DraftCapture.Draft.Generation.ShouldBe(draft.Generation);
            first.Origin.ShouldBe(Origin);
            first.State.ShouldBe(PlanOperationState.Planned);
            first.AuthorizationMode.ShouldBeNull();
            first.ConfirmedAt.ShouldBeNull();
            var batch = new PlanDocumentParser().Parse(first.CanonicalJson).Plan!.Operations.Single().ShouldBeOfType<BatchOperation>();
            batch.Fields["Fixture.Empty"].ShouldBe(string.Empty);
            batch.Fields["Fixture.Clear"].ShouldBeNull();
            File.Exists(first.SourcePath).ShouldBeFalse();
        }
        using var reopened = new SqliteCacheStore(ConnectionString);
        // A replay must not even attempt another INSERT OR IGNORE.
        InstallProbe(reopened, () => throw new InvalidOperationException("Replay imported twice."));
        var replay = await CaptureAsync(reopened, draft, snapshot);
        replay.CanonicalJson.ShouldBe(first.CanonicalJson);
        replay.Digest.ShouldBe(first.Digest);
        replay.PreviewedAt.ShouldBe(first.PreviewedAt);
        replay.LastPreviewedAt.ShouldBe(first.LastPreviewedAt);
        Count(reopened, "proposal_journals").ShouldBe(1);
        Count(reopened, "proposal_operations").ShouldBe(1);
        (await new SqliteRecoverableWorkItemDraftStore(reopened).GetAsync(Key))!.Generation.ShouldBe(draft.Generation);
    }

    [Fact]
    public async Task IdenticalEffectsAtNewGenerationHaveDifferentNativeOperationAndDigest()
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot) = await SeedAsync(cache);
        var first = await CaptureAsync(cache, draft, snapshot);
        var newer = draft.SetField("Fixture.Empty", "old", string.Empty);
        newer.Changes.ShouldBe(draft.Changes);
        (await new SqliteRecoverableWorkItemDraftStore(cache).TrySaveAsync(newer, draft.Generation)).ShouldBeTrue();
        var second = await CaptureAsync(cache, newer, snapshot);
        second.Digest.ShouldNotBe(first.Digest);
        second.DraftCapture!.OperationId.ShouldNotBe(first.DraftCapture!.OperationId);
        second.DraftCapture.Draft.Generation.ShouldBe(newer.Generation);
        Count(cache, "proposal_journals").ShouldBe(2);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("baseline")]
    [InlineData("conflict")]
    [InlineData("effects")]
    [InlineData("key")]
    [InlineData("snapshot")]
    [InlineData("pending")]
    public async Task StaleOrConflictingInputsRefuseWithZeroJournalWrites(string mismatch)
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot) = await SeedAsync(cache);
        var expected = mismatch switch
        {
            "generation" => new RecoverableWorkItemDraft(Key, 5, 5, draft.Generation + 1, draft.Changes, []),
            "baseline" => new RecoverableWorkItemDraft(Key, 4, 4, draft.Generation, draft.Changes, []),
            "conflict" => draft.Observe(5, [new DraftFieldConflict("Fixture.Empty", "old", "other", "")]),
            "effects" => new RecoverableWorkItemDraft(Key, 5, 5, draft.Generation,
                [new("Fixture.Clear", "old", null), new("Fixture.Empty", "old", "different")], []),
            "key" => new RecoverableWorkItemDraft(new(_directory, "other", "binding", 42),
                5, 5, draft.Generation, draft.Changes, []),
            _ => draft,
        };
        if (mismatch == "snapshot")
            snapshot = snapshot with { Revision = 6 };
        if (mismatch == "pending")
            Execute(cache, """
                INSERT INTO pending.pending_changes(work_item_id, change_type, field_name, old_value, new_value, created_at)
                VALUES (99, 'FieldChange', 'Fixture.Other', 'old', 'pending', 'fixed-time');
                """);
        await Should.ThrowAsync<InvalidOperationException>(() => CaptureAsync(cache, expected, snapshot));
        Count(cache, "proposal_journals").ShouldBe(0);
        Count(cache, "proposal_operations").ShouldBe(0);
        var retained = (await new SqliteRecoverableWorkItemDraftStore(cache).GetAsync(Key))!;
        retained.Generation.ShouldBe(draft.Generation);
        retained.Changes.ShouldBe(draft.Changes);
        Count(cache, "pending_changes").ShouldBe(mismatch == "pending" ? 1 : 0);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("binding")]
    [InlineData("source")]
    [InlineData("bytes")]
    [InlineData("origin")]
    public async Task ExistingDigestIsNeverBackfilledOrRebound(string mismatch)
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot) = await SeedAsync(cache);
        var first = await CaptureAsync(cache, draft, snapshot);
        if (mismatch == "unbound")
        {
            Execute(cache, "DELETE FROM pending.proposal_operations; DELETE FROM pending.proposal_journals;");
            var parsed = new PlanDocumentParser().Parse(first.CanonicalJson);
            await new SqlitePlanJournalRepository(cache).ImportAsync(parsed.Plan!, first.CanonicalJson,
                first.Digest, first.SourcePath, DateTimeOffset.UtcNow, origin: Origin);
        }
        else Execute(cache, mismatch switch
        {
            "binding" => "UPDATE pending.proposal_journals SET draft_capture_json=json_set(draft_capture_json,'$.draft.generation',987);",
            "source" => "UPDATE pending.proposal_journals SET source_path='different-source';",
            "bytes" => "UPDATE pending.proposal_journals SET canonical_json='{}';",
            _ => "UPDATE pending.proposal_journals SET origin_json=NULL;",
        });
        var before = HeaderJson(cache);
        if (mismatch == "origin")
            await Should.ThrowAsync<InvalidDataException>(() => CaptureAsync(cache, draft, snapshot));
        else
            await Should.ThrowAsync<InvalidOperationException>(() => CaptureAsync(cache, draft, snapshot));
        HeaderJson(cache).ShouldBe(before);
        Count(cache, "proposal_journals").ShouldBe(1);
        Count(cache, "proposal_operations").ShouldBe(1);
        (await new SqliteRecoverableWorkItemDraftStore(cache).GetAsync(Key))!.Generation.ShouldBe(draft.Generation);
        first.AuthorizationMode.ShouldBeNull();
    }

    [Fact]
    public async Task CancellationDuringActualJournalInsertRollsBackHeaderOperationsAndBinding()
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot) = await SeedAsync(cache);
        using var cancellation = new CancellationTokenSource();
        InstallProbe(cache, cancellation.Cancel);
        await Should.ThrowAsync<OperationCanceledException>(() => CaptureAsync(cache, draft, snapshot, cancellation.Token));
        Count(cache, "proposal_journals").ShouldBe(0);
        Count(cache, "proposal_operations").ShouldBe(0);
        cache.ActiveTransaction.ShouldBeNull();
        (await new SqliteRecoverableWorkItemDraftStore(cache).GetAsync(Key))!.Generation.ShouldBe(draft.Generation);
    }

    [Theory]
    [InlineData("twig.db")]
    [InlineData("pending.db")]
    public async Task ImmediateCaptureTransactionExcludesSecondConnectionWriter(string database)
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot) = await SeedAsync(cache);
        using var other = new SqliteConnection($"Data Source={Path.Combine(_directory, database)};Pooling=False;Default Timeout=1");
        other.Open();
        var attempted = false;
        InstallProbe(cache, () =>
        {
            attempted = true;
            var error = Should.Throw<SqliteException>(() => { using var competing = other.BeginTransaction(deferred: false); });
            error.SqliteErrorCode.ShouldBe(5);
        });
        await CaptureAsync(cache, draft, snapshot);
        attempted.ShouldBeTrue();
        using var afterCommit = other.BeginTransaction(deferred: false);
        afterCommit.Rollback();
    }

    [Fact]
    public async Task ImmediateCaptureTransactionExcludesCrossProcessDraftUpdate()
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot) = await SeedAsync(cache);
        var attempted = false;
        InstallProbe(cache, () =>
        {
            attempted = true;
            // Python is already a repository validation prerequisite; absence fails, never skips.
            var start = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("""
                import sqlite3, sys
                c = sqlite3.connect(sys.argv[1], timeout=0.1)
                try:
                    c.execute("UPDATE recoverable_work_item_drafts SET generation=generation+1")
                    c.commit()
                except sqlite3.OperationalError as e:
                    if e.sqlite_errorcode == sqlite3.SQLITE_BUSY:
                        print("writer-blocked", flush=True)
                        sys.exit(0)
                    raise
                sys.exit(9)
                """);
            start.ArgumentList.Add(Path.Combine(_directory, "pending.db"));
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Cross-process writer did not start.");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            try
            {
                child.WaitForExit(5000).ShouldBeTrue("cross-process writer must finish within five seconds");
                child.ExitCode.ShouldBe(0);
                stdout.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult().Trim().ShouldBe("writer-blocked");
                stderr.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult().ShouldBeEmpty();
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(5000).ShouldBeTrue("owned writer process must be reaped");
                }
            }
        });
        await CaptureAsync(cache, draft, snapshot);
        attempted.ShouldBeTrue();
        (await new SqliteRecoverableWorkItemDraftStore(cache).GetAsync(Key))!.Generation.ShouldBe(draft.Generation);
    }

    [Fact]
    public async Task ConcurrentCommittedDraftAdvanceRefusesOldGenerationWithoutJournal()
    {
        using var cache = new SqliteCacheStore(ConnectionString);
        var (draft, snapshot) = await SeedAsync(cache);
        using (var other = new SqliteCacheStore(ConnectionString))
            (await new SqliteRecoverableWorkItemDraftStore(other)
                .TrySaveAsync(draft.SetField("Fixture.Empty", "old", "new intent"), draft.Generation)).ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(() => CaptureAsync(cache, draft, snapshot));
        Count(cache, "proposal_journals").ShouldBe(0);
    }

    [Fact]
    public async Task Migration15To16RetainsDraftAndGenericJournalWithNullCapture()
    {
        RecoverableWorkItemDraft draft;
        string digest;
        using (var cache = new SqliteCacheStore(ConnectionString))
        {
            var seeded = await SeedAsync(cache);
            draft = seeded.Draft;
            var journal = await CaptureAsync(cache, draft, seeded.Snapshot);
            digest = journal.Digest;
            Execute(cache, "ALTER TABLE pending.proposal_journals DROP COLUMN draft_capture_json; PRAGMA pending.user_version=15;");
            AssertLegacySchemaFingerprint(cache.GetConnection(), 15);
        }
        using (var migrated = new SqliteCacheStore(ConnectionString))
        {
            AssertCurrentCaptureColumn(migrated);
            var loaded = await new SqlitePlanJournalRepository(migrated).GetAsync(digest);
            loaded.ShouldNotBeNull();
            loaded.DraftCapture.ShouldBeNull("migration must never backfill generic history");
            Count(migrated, "proposal_operations").ShouldBe(1);
            (await new SqliteRecoverableWorkItemDraftStore(migrated).GetAsync(Key))!.Changes.ShouldBe(draft.Changes);
            var snapshot = (await new SqliteWorkItemRepository(migrated, new WorkItemMapper()).GetSnapshotByIdAsync(42))!;
            await Should.ThrowAsync<InvalidOperationException>(() => CaptureAsync(migrated, draft, snapshot));
        }
        using var reopened = new SqliteCacheStore(ConnectionString);
        AssertCurrentCaptureColumn(reopened);
        (await new SqlitePlanJournalRepository(reopened).GetAsync(digest))!.DraftCapture.ShouldBeNull();
    }

    [Fact]
    public async Task FailedMigration16DoesNotStampVersionOrChangeExistingRows()
    {
        string before;
        using (var cache = new SqliteCacheStore(ConnectionString))
        {
            var (draft, snapshot) = await SeedAsync(cache);
            await CaptureAsync(cache, draft, snapshot);
            before = HeaderJson(cache);
            // Simulate an interrupted/foreign allocation with the column already present.
            Execute(cache, "PRAGMA pending.user_version=15;");
        }
        Should.Throw<InvalidOperationException>(() => { using var failed = new SqliteCacheStore(ConnectionString); });
        using var durable = new SqliteConnection($"Data Source={Path.Combine(_directory, "pending.db")};Pooling=False");
        durable.Open();
        using var command = durable.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(15);
        command.CommandText = "SELECT json_array(digest,canonical_json,origin_json,draft_capture_json,source_path,last_previewed_at) FROM proposal_journals;";
        command.ExecuteScalar().ShouldBe(before);
    }

    private async Task<(RecoverableWorkItemDraft Draft, WorkItemSnapshot Snapshot)> SeedAsync(SqliteCacheStore cache)
    {
        var mapper = new WorkItemMapper();
        var repository = new SqliteWorkItemRepository(cache, mapper);
        await repository.SaveAsync(mapper.Map(new WorkItemSnapshot
        {
            Id = 42, Revision = 5, Title = "baseline", TypeName = "FixtureType", State = "FixtureState",
            Fields = new Dictionary<string, string?> { ["Fixture.Empty"] = "old", ["Fixture.Clear"] = "old" },
        }));
        var snapshot = (await repository.GetSnapshotByIdAsync(42))!;
        var draft = RecoverableWorkItemDraft.Create(Key, 5)
            .SetField("Fixture.Empty", "old", "").SetField("Fixture.Clear", "old", null);
        (await new SqliteRecoverableWorkItemDraftStore(cache).TrySaveAsync(draft, null)).ShouldBeTrue();
        return (draft, snapshot);
    }

    private Task<PlanJournal> CaptureAsync(SqliteCacheStore cache, RecoverableWorkItemDraft draft,
        WorkItemSnapshot snapshot, CancellationToken ct = default)
        => new SqliteDraftProposalCaptureStore(cache).CaptureAsync(draft, snapshot, Origin, Workspace, ExportDirectory, ct);

    private static void InstallProbe(SqliteCacheStore cache, Action probe)
    {
        cache.GetConnection().CreateFunction("capture_probe", () => { probe(); return 0; });
        Execute(cache, """
            CREATE TRIGGER pending.capture_probe BEFORE INSERT ON proposal_journals
            BEGIN SELECT capture_probe(); END;
            """);
    }

    private static int Count(SqliteCacheStore cache, string table)
    {
        using var command = cache.GetConnection().CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pending.{table};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string HeaderJson(SqliteCacheStore cache)
    {
        using var command = cache.GetConnection().CreateCommand();
        command.CommandText = """
            SELECT json_array(digest,canonical_json,origin_json,draft_capture_json,source_path,last_previewed_at)
            FROM pending.proposal_journals;
            """;
        return (string)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteCacheStore cache, string sql)
    {
        using var command = cache.GetConnection().CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static void AssertLegacySchemaFingerprint(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA pending.user_version;";
        Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(version);
        var table = version < 8 ? "plan_journals" : "proposal_journals";
        command.CommandText = $"PRAGMA pending.table_info({table});";
        var columns = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));
        var expected = new List<string>
        {
            "digest", "schema_version", "organization", "project", "source_path", "canonical_json",
            "state", "previewed_at", "confirmed_at", "completed_at", "error",
        };
        if (version >= 9)
            expected.AddRange(["authorization_mode", "authorizer_identity", "rationale", "review_model_json", "authorized_at"]);
        if (version >= 11) expected.Add("last_previewed_at");
        if (version >= 12) expected.Add("origin_json");
        columns.ShouldBe(expected, "fixture column fingerprint must match the declared pre-capture schema");
        command.CommandText = "SELECT count(*) FROM pending.sqlite_master WHERE type='table' AND name='recoverable_work_item_drafts';";
        Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(version >= 15 ? 1 : 0);
    }

    private static void AssertCurrentCaptureColumn(SqliteCacheStore cache)
    {
        using var command = cache.GetConnection().CreateCommand();
        command.CommandText = "PRAGMA pending.user_version;";
        Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(16);
        command.CommandText = "SELECT count(*) FROM pragma_table_info('proposal_journals','pending') WHERE name='draft_capture_json';";
        Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture).ShouldBe(1);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
