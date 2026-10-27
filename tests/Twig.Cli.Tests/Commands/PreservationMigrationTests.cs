using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Plan;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Serialization;
using System.Text.Json;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Trait("Category", "HostInventory")]
public sealed class PreservationMigrationTests
{
    [Fact]
    public async Task SuccessfulMigrationPreservesSeedAndStandingThroughProductionReadersAfterReopen()
    {
        var persistentRoot = CanonicalTempRoot.Create("twig-preservation-deep-");
        Console.WriteLine($"retained-migration-fixture={persistentRoot}");
        await using var fixture = new NativeBrowserFixture(persistentRoot);
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var registry = runtime.GetServices<ISystemWorktreeRegistry>().ShouldHaveSingleItem();
        registry.ShouldBeSameAs(runtime.GetRequiredService<ISystemWorktreeRegistry>());
        runtime.GetRequiredService<IProviderTargetBindingStore>().ShouldBeSameAs(registry);
        runtime.GetRequiredService<IConnectionBindingService>().RegistryPath
            .ShouldBe(Path.Combine(fixture.Home, "system.db"));
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = runtime.GetRequiredService<IWorkItemRepository>();
        var context = runtime.GetRequiredService<IContextStore>();
        var history = runtime.GetRequiredService<INavigationHistoryStore>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Parent"));
        await items.SaveAsync(NativeBrowserFixture.Item(84, "Forward target"));
        var minted = await runtime.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
        var created = new SeedFactory().CreateUnparented(
            "Retained local work", WorkItemType.Task,
            AreaPath.Parse(@"Work\Private").Value,
            IterationPath.Parse(@"Work\Later").Value, minted, "Author", parentId: 42);
        created.IsSuccess.ShouldBeTrue(created.Error);
        var seed = created.Value.WithSeedFields("Edited local title", new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>Unpublished body</p>",
            ["Custom.Empty"] = "",
            ["Custom.Null"] = null,
            ["Custom.Value"] = "retained",
        });
        await items.SaveAsync(seed);
        await runtime.GetRequiredService<IPendingChangeStore>()
            .AddChangeAsync(seed.Id, "note", null, null, "Unpublished note");
        await runtime.GetRequiredService<ISeedLinkRepository>().AddLinkAsync(
            new SeedLink(seed.Id, 42, SeedLinkTypes.Related, DateTimeOffset.UtcNow));
        var historical = await runtime.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
        var intents = runtime.GetRequiredService<IPublishIntentRepository>();
        await intents.RecordIntentAsync(historical.Identity, "Previously published", "Task");
        await intents.CompleteIntentAsync(historical.Identity, 84);
        await runtime.GetRequiredService<IPublishIdMapRepository>().RecordMappingAsync(historical.Identity, 84);
        await runtime.GetRequiredService<IStagedIdentityRegistry>().RetireAsync(historical.Identity);
        var edited = NativeBrowserFixture.Item(77, "Pending edit");
        edited.UpdateField("Custom.Value", "unpublished");
        await items.SaveAsync(edited);
        var localStore = runtime.GetRequiredService<ILocalWorkItemStore>();
        var rich = await localStore.CreateAsync(FieldValues.Empty.Set("Local.Content", new TextValue("original")));
        var richUpdated = (await localStore.UpdateAsync(rich.Id, rich.Revision,
            rich.Fields.Set("Local.Content", new TextValue("retained")))).Value.ShouldBeOfType<LocalWriteApplied>().Record;
        var toDelete = await localStore.CreateAsync(FieldValues.Empty.Set("Local.Content", new TextValue("tombstone")));
        var tombstone = (await localStore.DeleteAsync(toDelete.Id, toDelete.Revision))
            .Value.ShouldBeOfType<LocalWriteApplied>().Record;
        var originalLocals = new List<LocalWorkItemRecord> { richUpdated, tombstone };
        var identities = runtime.GetRequiredService<IExternalIdentityMap>();
        foreach (var id in new[] { 42, 84, 77 })
        {
            var local = await identities.FindAsync(new ExternalWorkItemIdentity(
                "ado", ((SqliteWorkItemRepository)items).DurableScope!, id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            local.ShouldNotBeNull();
            var record = await localStore.GetAsync(local.Value);
            record.ShouldNotBeNull();
            originalLocals.Add(record);
        }
        var pending = runtime.GetRequiredService<IPendingChangeStore>();
        await pending.AddChangeAsync(77, "field", "Custom.Value", null, "");
        await pending.AddChangeAsync(77, "field", "Custom.Value", "", null);
        var proposal = Path.Combine(fixture.Paths.TwigDir, "retained-proposal.json");
        await File.WriteAllTextAsync(proposal,
            "{\"version\":1,\"workspace\":{\"organization\":\"fixture\",\"project\":\"Work\"},\"operations\":[{\"id\":\"retained\",\"kind\":\"batch\",\"workItemId\":42,\"expectedRevision\":3,\"fields\":{\"System.Title\":\"Retained intent\"}}]}");
        var journal = (await runtime.GetRequiredService<IPlanLifecycleService>().PreviewAsync(proposal)).Digest!;
        await runtime.GetRequiredService<IPlanJournalRepository>().SaveOperationErrorAsync(
            journal, "retained", "Preserved unknown outcome", PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);
        await context.SetActiveWorkItemIdAsync(seed.Id);
        await context.SetValueAsync("workspace_mode", "Manual");
        await context.SetValueAsync("area_tree_json", "{\"opaque\":[null,\"\",7]}");
        await context.SetValueAsync("custom.standing.preference", "{\"opaque\":[false,null,\"user value\"]}");
        await history.RecordVisitAsync(42);
        await history.RecordVisitAsync(seed.Id);
        await history.RecordVisitAsync(seed.Id);
        await history.RecordVisitAsync(84);
        (await history.GoBackAsync()).ShouldBe(seed.Id);
        var expectedHistory = await history.GetHistoryAsync();
        var expectedSeed = await ((SqliteWorkItemRepository)items).GetSnapshotByIdAsync(seed.Id);
        var scope = ((SqliteWorkItemRepository)items).DurableScope!;
        var before = new StoreParityHarness(store, scope, preserveOpaqueContext: true).Inspect();
        before.IsConsistent.ShouldBeTrue();
        before.Classes.Single(entry => entry.ClassName == "pending.local_work_items").RowCount.ShouldBe(originalLocals.Count);
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(),
            scope);

        var preview = migration.Preview();
        var result = await migration.ApplyAsync(preview.Digest);

        result.State.ShouldBe("active");
        Directory.Exists(result.BackupDirectory).ShouldBeTrue();
        var localId = migration.FindSeedLocalId(minted.Identity);
        localId.ShouldNotBeNull();
        localId.Value.Value.ShouldNotBe(minted.Identity.Value);
        (await migration.ApplyAsync(preview.Digest)).ShouldBe(result);
        migration.FindSeedLocalId(minted.Identity).ShouldBe(localId);
        foreach (var original in originalLocals)
            (await localStore.GetAsync(original.Id)).ShouldBe(original);
        var after = new StoreParityHarness(store, scope, preserveOpaqueContext: true).Inspect();
        after.IsConsistent.ShouldBeTrue();
        foreach (var original in before.Classes.Where(entry =>
                     entry.ClassName != "pending.local_work_items"
                     && entry.ClassName != "pending.seed_work_items"
                     && entry.ClassName != "pending.seed_local_identities"
                     && entry.ClassName != "pending.workspace_context"
                     && entry.ClassName != "pending.workspace_navigation_history"
                     && entry.ClassName != "pending.preservation_migrations"
                     && entry.ClassName != "pending.workspace-navigation-sequence"))
        {
            var retained = after.Classes.Single(entry => entry.ClassName == original.ClassName);
            retained.RowCount.ShouldBe(original.RowCount);
            retained.CanonicalDigest.ShouldBe(original.CanonicalDigest);
        }
        runtime.Dispose();

        var reopened = await fixture.CreateRuntimeAsync(initializeBench: false);
        var reopenedItems = reopened.GetRequiredService<IWorkItemRepository>();
        var reopenedContext = reopened.GetRequiredService<IContextStore>();
        var reopenedHistory = reopened.GetRequiredService<INavigationHistoryStore>();
        foreach (var original in originalLocals)
            (await reopened.GetRequiredService<ILocalWorkItemStore>().GetAsync(original.Id)).ShouldBe(original);
        var actualSeed = await ((SqliteWorkItemRepository)reopenedItems).GetSnapshotByIdAsync(seed.Id);
        actualSeed.ShouldNotBeNull();
        expectedSeed.ShouldNotBeNull();
        actualSeed.Fields.ShouldBe(expectedSeed.Fields);
        (actualSeed with { Fields = expectedSeed.Fields }).ShouldBe(expectedSeed);
        (await reopenedItems.GetSeedsAsync()).ShouldHaveSingleItem().Id.ShouldBe(seed.Id);
        (await reopenedContext.GetActiveWorkItemIdAsync()).ShouldBe(seed.Id);
        (await reopenedContext.GetValueAsync("workspace_mode")).ShouldBe("Manual");
        (await reopenedContext.GetValueAsync("area_tree_json")).ShouldBe("{\"opaque\":[null,\"\",7]}");
        (await reopenedContext.GetValueAsync("custom.standing.preference"))
            .ShouldBe("{\"opaque\":[false,null,\"user value\"]}");
        var actualHistory = await reopenedHistory.GetHistoryAsync();
        actualHistory.Entries.ShouldBe(expectedHistory.Entries);
        actualHistory.CursorEntryId.ShouldBe(expectedHistory.CursorEntryId);
        (await reopenedHistory.GoForwardAsync()).ShouldBe(84);
        (await reopened.GetRequiredService<IPendingChangeStore>().GetChangesAsync(seed.Id))
            .ShouldHaveSingleItem().NewValue.ShouldBe("Unpublished note");
        new PreservationMigrationService(reopened.GetRequiredService<SqliteCacheStore>(), fixture.CurrentPaths(), scope)
            .Rollback(preview.Digest).State.ShouldBe("rolled-back");
        reopened.Dispose();
        var rolledBack = await fixture.CreateRuntimeAsync(initializeBench: false);
        (await rolledBack.GetRequiredService<IContextStore>().GetValueAsync("custom.standing.preference"))
            .ShouldBe("{\"opaque\":[false,null,\"user value\"]}");
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData("intent-recorded")]
    [InlineData("backup-mirror-copied")]
    [InlineData("backup-qualified")]
    [InlineData("before-activation")]
    public async Task InterruptedPreparationKeepsCurrentViewAndResumesOriginalIntent(string phase)
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Existing current item"));
        var context = runtime.GetRequiredService<IContextStore>();
        await context.SetActiveWorkItemIdAsync(42);
        var migration = new PreservationMigrationService(runtime.GetRequiredService<SqliteCacheStore>(),
            fixture.CurrentPaths(), items.DurableScope!, current =>
            {
                if (current == phase) throw new IOException("Simulated migration interruption");
            });
        var preview = migration.Preview();

        await Should.ThrowAsync<IOException>(() => migration.ApplyAsync(preview.Digest));
        (await context.GetActiveWorkItemIdAsync()).ShouldBe(42);
        (await items.GetByIdAsync(42)).ShouldNotBeNull();
        runtime.Dispose();

        var restarted = await fixture.CreateRuntimeAsync(initializeBench: false);
        var recovery = new PreservationMigrationService(restarted.GetRequiredService<SqliteCacheStore>(),
            fixture.CurrentPaths(), items.DurableScope!);
        recovery.Preview().Digest.ShouldBe(preview.Digest);
        (await recovery.ApplyAsync(preview.Digest)).State.ShouldBe("active");
        if (phase == "backup-mirror-copied")
            Directory.GetDirectories(preview.BackupDirectory, "snapshot-*").Length.ShouldBeGreaterThanOrEqualTo(2);
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task SourceWriterAfterBackupPreventsActivationWithoutDiscardingItsChange()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Existing item"));
        var context = runtime.GetRequiredService<IContextStore>();
        var reviewedMode = await context.GetValueAsync("workspace_mode");
        reviewedMode.ShouldBe("Sprint");
        var generation = store.ValidateAdmittedGeneration();
        generation.ShouldNotBeNull();
        var writerHits = 0;
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!, phase =>
        {
            if (phase != "before-activation") return;
            store.ValidateAdmittedGeneration().ShouldBe(generation);
            using var precondition = store.GetConnection().CreateCommand();
            precondition.CommandText = """
                SELECT value FROM main.context WHERE key='workspace_mode';
                """;
            precondition.ExecuteScalar().ShouldBe(reviewedMode);
            using var target = store.GetConnection().CreateCommand();
            target.CommandText = """
                SELECT (SELECT count(*) FROM pending.seed_work_items)
                    + (SELECT count(*) FROM pending.seed_local_identities)
                    + (SELECT count(*) FROM pending.workspace_context)
                    + (SELECT count(*) FROM pending.workspace_navigation_history);
                """;
            Convert.ToInt64(target.ExecuteScalar()).ShouldBe(0);
            using var writer = store.GetConnection().CreateCommand();
            writer.CommandText = "UPDATE main.context SET value='Manual' WHERE key='workspace_mode';";
            writer.ExecuteNonQuery().ShouldBe(1);
            precondition.ExecuteScalar().ShouldBe("Manual");
            precondition.ExecuteScalar().ShouldNotBe(reviewedMode);
            store.ValidateAdmittedGeneration().ShouldBe(generation);
            writerHits++;
        });
        var preview = migration.Preview();

        var error = await Should.ThrowAsync<InvalidOperationException>(() => migration.ApplyAsync(preview.Digest));

        writerHits.ShouldBe(1);
        error.Message.ShouldStartWith("preservation-source-changed:");
        new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        store.ValidateAdmittedGeneration().ShouldBe(generation);
        (await context.GetValueAsync("workspace_mode")).ShouldBe("Manual");
        File.Exists(Path.Combine(preview.BackupDirectory, "manifest.json")).ShouldBeTrue();
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionAdmissionWriterIsFingerprintedBeforeActivationAndRetainedThroughRollback(bool afterActivation)
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Existing item"));
        var proposal = Path.Combine(fixture.Paths.TwigDir, "admission-migration.json");
        await File.WriteAllTextAsync(proposal,
            "{\"version\":1,\"workspace\":{\"organization\":\"fixture\",\"project\":\"Work\"},\"operations\":[{\"id\":\"edit\",\"kind\":\"batch\",\"workItemId\":42,\"expectedRevision\":3,\"fields\":{\"System.Title\":\"New title\"}}]}");
        var digest = (await runtime.GetRequiredService<IPlanLifecycleService>().PreviewAsync(proposal)).Digest.ShouldNotBeNull();
        var repository = runtime.GetRequiredService<IPlanJournalRepository>();
        var journal = (await repository.GetAsync(digest)).ShouldNotBeNull();
        var admission = new PlanExecutionAdmission
        {
            Digest = digest, Origin = journal.Origin.ShouldNotBeNull(), RecordedAt = DateTimeOffset.UtcNow,
        };
        string? recordedBytes = null;
        void RecordAdmission()
        {
            repository.RecordExecutionAdmissionAsync(admission).GetAwaiter().GetResult()
                .ShouldBe(PlanAdmissionRecordResult.Recorded);
            using var read = store.GetConnection().CreateCommand();
            read.CommandText = "SELECT execution_admission_json FROM pending.proposal_journals WHERE digest=$digest;";
            read.Parameters.AddWithValue("$digest", digest);
            recordedBytes = ((string?)read.ExecuteScalar()).ShouldNotBeNull();
        }
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!, phase =>
        {
            if (!afterActivation && phase == "before-activation") RecordAdmission();
        });
        var preview = migration.Preview();
        if (afterActivation)
        {
            (await migration.ApplyAsync(preview.Digest)).State.ShouldBe("active");
            RecordAdmission();
            migration.Rollback(preview.Digest).State.ShouldBe("rolled-back");
        }
        else
        {
            var error = await Should.ThrowAsync<InvalidOperationException>(() => migration.ApplyAsync(preview.Digest));
            error.Message.ShouldStartWith("preservation-source-changed:");
            new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        }
        recordedBytes.ShouldNotBeNull();
        using var retained = store.GetConnection().CreateCommand();
        retained.CommandText = "SELECT execution_admission_json FROM pending.proposal_journals WHERE digest=$digest;";
        retained.Parameters.AddWithValue("$digest", digest);
        retained.ExecuteScalar().ShouldBe(recordedBytes);
        var reloaded = (await repository.GetAsync(digest)).ShouldNotBeNull();
        var reloadedAdmission = reloaded.ExecutionAdmission.ShouldNotBeNull();
        JsonSerializer.Serialize(reloadedAdmission, TwigJsonContext.Default.PlanExecutionAdmission)
            .ShouldBe(JsonSerializer.Serialize(admission, TwigJsonContext.Default.PlanExecutionAdmission));
        reloaded.AuthorizationMode.ShouldBeNull();
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task BackupCopyCorruptionBlocksCertificationAndActivation()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Original item"));
        var context = runtime.GetRequiredService<IContextStore>();
        await context.SetActiveWorkItemIdAsync(42);
        await context.SetValueAsync("custom.backup-proof", "unchanged");
        var history = runtime.GetRequiredService<INavigationHistoryStore>();
        await history.RecordVisitAsync(42);
        var expectedHistory = await history.GetHistoryAsync();
        var expectedItem = await items.GetSnapshotByIdAsync(42);
        var before = new StoreParityHarness(store, items.DurableScope!, preserveOpaqueContext: true).Inspect();
        before.IsConsistent.ShouldBeTrue();
        var generation = store.ValidateAdmittedGeneration();
        generation.ShouldNotBeNull();
        var preview = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!).Preview();
        var backupRoot = Path.GetFullPath(preview.BackupDirectory);
        backupRoot.ShouldBe(Path.GetFullPath(Path.Combine(fixture.CurrentPaths().TwigDir,
            "backups", "preservation-" + preview.Digest)));
        var corruptedCopy = "";
        var corruption = System.Text.Encoding.ASCII.GetBytes("Deliberately corrupt owned SQLite backup");
        var checkpointHits = 0;
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!, phase =>
        {
            if (phase != "backup-durable-copied") return;
            checkpointHits++;
            checkpointHits.ShouldBe(1);
            var snapshot = Directory.GetDirectories(backupRoot, "snapshot-*").ShouldHaveSingleItem();
            Path.GetDirectoryName(Path.GetFullPath(snapshot)).ShouldBe(backupRoot);
            var mirror = Path.Combine(snapshot, "mirror.db");
            corruptedCopy = Path.Combine(snapshot, "pending.db");
            foreach (var path in new[] { mirror, corruptedCopy })
            {
                File.Exists(path).ShouldBeTrue();
                var current = Path.GetFullPath(path);
                while (true)
                {
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint).ShouldBe((FileAttributes)0);
                    if (current == Path.GetFullPath(fixture.CurrentPaths().TwigDir)) break;
                    current = Path.GetDirectoryName(current)
                        ?? throw new InvalidOperationException("Owned backup ancestor is missing.");
                }
                ReadIntegrity(path).ShouldBe("ok");
            }
            using (var file = new FileStream(corruptedCopy, FileMode.Truncate, FileAccess.Write, FileShare.None))
            {
                file.Write(corruption);
                file.Flush(flushToDisk: true);
            }
            File.ReadAllBytes(corruptedCopy).ShouldBe(corruption);
            Should.Throw<SqliteException>(() => ReadIntegrity(corruptedCopy)).SqliteErrorCode.ShouldBe(26);
        });

        var error = await Should.ThrowAsync<Exception>(() => migration.ApplyAsync(preview.Digest));

        checkpointHits.ShouldBe(1);
        var nativeError = error switch
        {
            SqliteException sqlite => sqlite,
            InvalidOperationException { InnerException: SqliteException sqlite }
                when error.Message.StartsWith("preservation-backup-unavailable:", StringComparison.Ordinal) => sqlite,
            _ => throw new Xunit.Sdk.XunitException($"Expected corrupt-backup SQLite refusal, received: {error}"),
        };
        nativeError.SqliteErrorCode.ShouldBe(26);
        File.Exists(Path.Combine(backupRoot, "manifest.json")).ShouldBeFalse();
        File.ReadAllBytes(corruptedCopy).ShouldBe(corruption);
        new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        store.ValidateAdmittedGeneration().ShouldBe(generation);
        using (var intent = store.GetConnection().CreateCommand())
        {
            intent.CommandText = "SELECT state FROM pending.preservation_migrations WHERE slot=1;";
            intent.ExecuteScalar().ShouldBe("preparing");
        }
        var after = new StoreParityHarness(store, items.DurableScope!, preserveOpaqueContext: true).Inspect();
        after.IsConsistent.ShouldBeTrue();
        after.Classes.Where(entry => entry.ClassName != "pending.preservation_migrations")
            .Select(entry => (entry.ClassName, entry.RowCount, entry.CanonicalDigest))
            .ShouldBe(before.Classes.Where(entry => entry.ClassName != "pending.preservation_migrations")
                .Select(entry => (entry.ClassName, entry.RowCount, entry.CanonicalDigest)));
        var actualItem = await items.GetSnapshotByIdAsync(42);
        actualItem.ShouldNotBeNull();
        expectedItem.ShouldNotBeNull();
        actualItem.Fields.ShouldBe(expectedItem.Fields);
        (actualItem with { Fields = expectedItem.Fields }).ShouldBe(expectedItem);
        (await context.GetActiveWorkItemIdAsync()).ShouldBe(42);
        (await context.GetValueAsync("custom.backup-proof")).ShouldBe("unchanged");
        var actualHistory = await history.GetHistoryAsync();
        actualHistory.Entries.ShouldBe(expectedHistory.Entries);
        actualHistory.CursorEntryId.ShouldBe(expectedHistory.CursorEntryId);
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);

        static string? ReadIntegrity(string path)
        {
            var logical = Path.GetFullPath(path);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = logical, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            };
            if (OperatingSystem.IsWindows())
            {
                builder.Vfs = "win32-longpath";
                builder.DataSource = logical.StartsWith(@"\\", StringComparison.Ordinal)
                    ? @"\\?\UNC\" + logical[2..]
                    : @"\\?\" + logical;
            }
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            SqliteCacheStore.RegisterCollations(connection);
            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            return integrity.ExecuteScalar() as string;
        }
    }

    [Fact]
    public async Task BackupFailureBlocksActivationAndCanResumeWithoutDeletingOriginals()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Existing item"));
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!);
        var preview = migration.Preview();
        var backupRoot = Path.Combine(fixture.Paths.TwigDir, "backups");
        await File.WriteAllTextAsync(backupRoot, "Fixture blocks backup directory creation");

        await Should.ThrowAsync<IOException>(() => migration.ApplyAsync(preview.Digest));
        new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        (await items.GetByIdAsync(42)).ShouldNotBeNull();
        File.Delete(backupRoot);
        (await migration.ApplyAsync(preview.Digest)).State.ShouldBe("active");
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task UnclassifiedTargetWriterIsRefusedWithoutClearingEitherView()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!, phase =>
        {
            if (phase != "before-activation") return;
            using var writer = store.GetConnection().CreateCommand();
            writer.CommandText = "INSERT INTO pending.workspace_context(key,value) VALUES('unknown-target','retained');";
            writer.ExecuteNonQuery();
        });
        var preview = migration.Preview();

        var error = await Should.ThrowAsync<InvalidOperationException>(() => migration.ApplyAsync(preview.Digest));
        error.Message.ShouldStartWith("preservation-target-unclassified:");
        new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        (await items.GetByIdAsync(42)).ShouldNotBeNull();
        using var retained = store.GetConnection().CreateCommand();
        retained.CommandText = "SELECT value FROM pending.workspace_context WHERE key='unknown-target';";
        retained.ExecuteScalar().ShouldBe("retained");
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task FailedPreparationCanBeExplicitlyCancelledAndReviewedAgainAfterOrdinaryEdits()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        await items.SaveAsync(NativeBrowserFixture.Item(84, "Forward"));
        var history = runtime.GetRequiredService<INavigationHistoryStore>();
        await history.RecordVisitAsync(42);
        await history.RecordVisitAsync(42);
        var failing = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!, phase =>
        {
            if (phase == "backup-qualified") throw new IOException("Simulated interruption");
        });
        var first = failing.Preview();
        await Should.ThrowAsync<IOException>(() => failing.ApplyAsync(first.Digest));
        var preparing = failing.Preview();
        preparing.IntentId.ShouldNotBeNull();
        await runtime.GetRequiredService<IContextStore>().SetValueAsync("custom.preference", "changed after backup");
        await history.RecordVisitAsync(84);
        var expectedHistory = await history.GetHistoryAsync();
        var recovery = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!);
        (await Should.ThrowAsync<InvalidOperationException>(() => recovery.ApplyAsync(first.Digest)))
            .Message.ShouldStartWith("preservation-source-changed:");
        Should.Throw<InvalidOperationException>(() => recovery.CancelPreparation(first.Digest, Guid.NewGuid().ToString("N")))
            .Message.ShouldStartWith("preservation-cancel-cas-failed:");
        recovery.CancelPreparation(first.Digest, preparing.IntentId!).State.ShouldBe("cancelled");
        File.Exists(Path.Combine(first.BackupDirectory, "manifest.json")).ShouldBeTrue();

        var reviewed = recovery.Preview();
        reviewed.Digest.ShouldNotBe(first.Digest);
        await Should.ThrowAsync<IOException>(() => failing.ApplyAsync(reviewed.Digest));
        var second = recovery.Preview();
        second.IntentId.ShouldNotBe(preparing.IntentId);
        Should.Throw<InvalidOperationException>(() => recovery.CancelPreparation(reviewed.Digest, preparing.IntentId!))
            .Message.ShouldStartWith("preservation-cancel-cas-failed:");
        (await recovery.ApplyAsync(reviewed.Digest)).State.ShouldBe("active");
        (await history.GetHistoryAsync()).Entries.ShouldBe(expectedHistory.Entries);
        (await history.GetHistoryAsync()).CursorEntryId.ShouldBe(expectedHistory.CursorEntryId);
        (await runtime.GetRequiredService<IContextStore>().GetValueAsync("custom.preference")).ShouldBe("changed after backup");
        using var retained = store.GetConnection().CreateCommand();
        retained.CommandText = "SELECT count(*) FROM pending.preservation_migrations WHERE intent_id=$id AND state='cancelled' AND slot IS NULL;";
        retained.Parameters.AddWithValue("$id", preparing.IntentId!);
        Convert.ToInt64(retained.ExecuteScalar()).ShouldBe(1);
        File.Exists(Path.Combine(first.BackupDirectory, "manifest.json")).ShouldBeTrue();
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData("UPDATE main.work_items SET fields_json='{invalid}' WHERE is_seed=1;")]
    [InlineData("UPDATE main.work_items SET seed_created_at='invalid-time' WHERE is_seed=1;")]
    [InlineData("UPDATE main.work_items SET last_synced_at='invalid-time' WHERE is_seed=1;")]
    [InlineData("UPDATE main.navigation_history SET visited_at='invalid-time';")]
    public async Task PayloadRejectedByExistingReaderCannotActivateAndRemainsUnmodified(string mutation)
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Existing"));
        var minted = await runtime.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
        var seed = new SeedFactory().CreateUnparented("Seed", WorkItemType.Task,
            AreaPath.Parse("Work").Value, IterationPath.Parse("Work").Value, minted);
        seed.IsSuccess.ShouldBeTrue(seed.Error);
        await items.SaveAsync(seed.Value);
        await runtime.GetRequiredService<INavigationHistoryStore>().RecordVisitAsync(seed.Value.Id);
        using (var corrupt = store.GetConnection().CreateCommand())
        {
            corrupt.CommandText = mutation;
            corrupt.ExecuteNonQuery();
        }
        var before = new StoreParityHarness(store, items.DurableScope!).Inspect();
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!);

        Should.Throw<InvalidOperationException>(() => migration.Preview()).Message
            .ShouldStartWith("preservation-source-payload-invalid:");

        var after = new StoreParityHarness(store, items.DurableScope!).Inspect();
        after.Classes.Select(entry => (entry.ClassName, entry.RowCount, entry.CanonicalDigest))
            .ShouldBe(before.Classes.Select(entry => (entry.ClassName, entry.RowCount, entry.CanonicalDigest)));
        new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task ActualReadersUseRetainedTablesAndRollbackRefusesDivergentOriginals()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Existing item"));
        var context = runtime.GetRequiredService<IContextStore>();
        var history = runtime.GetRequiredService<INavigationHistoryStore>();
        await context.SetActiveWorkItemIdAsync(42);
        await history.RecordVisitAsync(42);
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!);
        var preview = migration.Preview();
        await migration.ApplyAsync(preview.Digest);
        using (var writer = store.GetConnection().CreateCommand())
        {
            writer.CommandText = "DELETE FROM main.context; DELETE FROM main.navigation_history;";
            writer.ExecuteNonQuery();
        }

        (await context.GetActiveWorkItemIdAsync()).ShouldBe(42);
        (await history.GetHistoryAsync()).Entries.ShouldHaveSingleItem().WorkItemId.ShouldBe(42);
        Should.Throw<InvalidOperationException>(() => migration.Rollback(preview.Digest))
            .Message.ShouldStartWith("preservation-parity-failed:");
        new PreservedWorkspaceState(store).IsActive.ShouldBeTrue();
        runtime.Dispose();
        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => fixture.CreateRuntimeAsync(initializeBench: false));
        error.Message.ShouldStartWith("preservation-parity-failed:");
    }

    [Fact]
    public async Task ActualSeedReadersUseRetainedPayloadWithoutOriginalSeed()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Original parent"));
        var minted = await runtime.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
        var created = new SeedFactory().CreateUnparented("Seed reader proof", WorkItemType.Task,
            AreaPath.Parse(@"Work\Private").Value, IterationPath.Parse(@"Work\Later").Value,
            minted, "Author", parentId: 42);
        created.IsSuccess.ShouldBeTrue(created.Error);
        var seed = created.Value.WithSeedFields("Retained seed payload", new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>Durable seed reader proof</p>",
            ["Custom.Empty"] = "",
            ["Custom.Null"] = null,
            ["Custom.Value"] = "retained",
        });
        await items.SaveAsync(seed);
        var expected = await items.GetSnapshotByIdAsync(seed.Id);
        expected.ShouldNotBeNull();
        expected.IsSeed.ShouldBeTrue();
        expected.StagedIdentity.ShouldBe(minted.Identity.ToString());
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!);
        var preview = migration.Preview();
        (await migration.ApplyAsync(preview.Digest)).State.ShouldBe("active");
        var local = migration.FindSeedLocalId(minted.Identity);
        local.ShouldNotBeNull();
        local.Value.Value.ShouldNotBe(minted.Identity.Value);
        using (var original = store.GetConnection().CreateCommand())
        {
            original.CommandText = """
                DELETE FROM main.work_items WHERE id=$alias AND is_seed=1 AND staged_identity=$staged;
                """;
            original.Parameters.AddWithValue("$alias", seed.Id);
            original.Parameters.AddWithValue("$staged", minted.Identity.ToString());
            original.ExecuteNonQuery().ShouldBe(1);
        }
        using (var standing = store.GetConnection().CreateCommand())
        {
            standing.CommandText = "SELECT count(*) FROM main.work_items WHERE id=$alias;";
            standing.Parameters.AddWithValue("$alias", seed.Id);
            Convert.ToInt64(standing.ExecuteScalar()).ShouldBe(0);
            standing.CommandText = """
                SELECT local_id FROM pending.seed_work_items
                WHERE id=$alias AND is_seed=1 AND staged_identity=$staged;
                """;
            standing.Parameters.AddWithValue("$staged", minted.Identity.ToString());
            standing.ExecuteScalar().ShouldBe(local.Value.ToString());
        }

        var actual = await items.GetSnapshotByIdAsync(seed.Id);

        actual.ShouldNotBeNull();
        actual.Fields.ShouldBe(expected.Fields);
        (actual with { Fields = expected.Fields }).ShouldBe(expected);
        (await items.GetByIdAsync(seed.Id)).ShouldNotBeNull();
        (await items.GetSeedsAsync()).ShouldHaveSingleItem().Id.ShouldBe(seed.Id);
        migration.FindSeedLocalId(minted.Identity).ShouldBe(local);
        (await runtime.GetRequiredService<ILocalWorkItemStore>().GetAsync(local.Value)).ShouldNotBeNull();
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task LocalWritesRemainReversibleAndGenerationChangingTransitionIsBlockedBeforeIntent()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Existing item"));
        var context = runtime.GetRequiredService<IContextStore>();
        await context.SetActiveWorkItemIdAsync(42);
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!);
        var preview = migration.Preview();
        await migration.ApplyAsync(preview.Digest);
        await context.SetValueAsync("workspace_mode", "Manual");
        var history = runtime.GetRequiredService<INavigationHistoryStore>();
        await history.RecordVisitAsync(42);
        var generation = store.AdmittedGeneration;

        var blocked = await fixture.Transitions.PreviewAsync(
            fixture.Configuration, fixture.CurrentPaths(), fixture.SiblingBinding.BindingId);
        blocked.CanApply.ShouldBeFalse();
        blocked.Blockers.ShouldContain(reason => reason.StartsWith("binding-pending-work:", StringComparison.Ordinal));
        var apply = await fixture.Transitions.ApplyAsync(
            fixture.Configuration, fixture.CurrentPaths(), fixture.SiblingBinding.BindingId, blocked.Digest);
        apply.CanApply.ShouldBeFalse();
        store.ValidateAdmittedGeneration().ShouldBe(generation);
        (await context.GetActiveWorkItemIdAsync()).ShouldBe(42);
        var unchanged = await fixture.Transitions.PreviewAsync(fixture.Configuration, fixture.CurrentPaths(), null);
        unchanged.CanApply.ShouldBeTrue();
        unchanged.State.ShouldBe("unchanged");

        migration.Rollback(preview.Digest).State.ShouldBe("rolled-back");
        new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        (await context.GetValueAsync("workspace_mode")).ShouldBe("Manual");
        (await history.GetHistoryAsync()).Entries.ShouldHaveSingleItem().WorkItemId.ShouldBe(42);
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task GenerationRaceAtActivationPreservesOriginalAdmissionAndPreparingIntent()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Existing item"));
        var marker = Path.Combine(fixture.Paths.TwigDir, "cache", MirrorAdmission.MarkerFile);
        var original = await File.ReadAllTextAsync(marker);
        var document = JsonSerializer.Deserialize(original, TwigJsonContext.Default.MirrorAdmissionDocument)!;
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!, phase =>
        {
            if (phase == "before-activation")
                File.WriteAllText(marker, JsonSerializer.Serialize(
                    document with { Generation = Guid.NewGuid().ToString("N") }, TwigJsonContext.Default.MirrorAdmissionDocument));
        });
        var preview = migration.Preview();
        try
        {
            var error = await Should.ThrowAsync<InvalidOperationException>(() => migration.ApplyAsync(preview.Digest));
            error.Message.ShouldStartWith("binding-changed:");
        }
        finally { await File.WriteAllTextAsync(marker, original); }

        new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        (await items.GetByIdAsync(42)).ShouldNotBeNull();
        var recovery = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!);
        (await recovery.ApplyAsync(preview.Digest)).State.ShouldBe("active");
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task SeedEditsAndLocalPublicationCompositionKeepIndependentStableLocalIdentity()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Existing parent"));
        var minted = await runtime.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
        var created = new SeedFactory().CreateUnparented("Seed", WorkItemType.Task,
            AreaPath.Parse("Work").Value, IterationPath.Parse("Work").Value, minted, parentId: 42);
        created.IsSuccess.ShouldBeTrue(created.Error);
        await items.SaveAsync(created.Value);
        await runtime.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(created.Value.Id);
        var history = runtime.GetRequiredService<INavigationHistoryStore>();
        await history.RecordVisitAsync(created.Value.Id);
        await history.RecordVisitAsync(created.Value.Id);
        await history.RecordVisitAsync(42);
        await history.GoBackAsync();
        var originalHistory = await history.GetHistoryAsync();
        await runtime.GetRequiredService<IPendingChangeStore>()
            .AddChangeAsync(created.Value.Id, "note", null, null, "Retained staged note");
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!);
        var preview = migration.Preview();
        await migration.ApplyAsync(preview.Digest);
        var local = migration.FindSeedLocalId(minted.Identity);
        local.ShouldNotBeNull();
        var parentLocal = await new SqliteLocalWorkItemRepository(store).FindAsync(
            new ExternalWorkItemIdentity("ado", items.DurableScope!, "42"));
        parentLocal.ShouldNotBeNull();
        var relationships = new SqliteLocalRelationshipRepository(store);
        var now = DateTimeOffset.UtcNow;
        var edge = await relationships.UpsertAsync(new LocalRelationship(
            local.Value, parentLocal.Value, LinkKind.Related,
            EmulationStorage.LocalOnly, PublicationState.NotApplicable, 0, now, now), null);
        edge.Status.ShouldBe(LocalRelationshipWriteStatus.Written);
        var edited = created.Value.WithSeedFields("Retained edit", new Dictionary<string, string?>
        {
            ["Custom.Value"] = "after activation",
        });
        await items.SaveAsync(edited);
        (await items.GetByIdAsync(edited.Id))!.Fields["Custom.Value"].ShouldBe("after activation");
        migration.FindSeedLocalId(minted.Identity).ShouldBe(local);

        await runtime.GetRequiredService<SeedPublishOrchestrator>().CompleteLocalPublishAsync(
            edited, NativeBrowserFixture.Item(88, "Already observed published result"), 88, CancellationToken.None);

        migration.FindSeedLocalId(minted.Identity).ShouldBe(local);
        var external = await new SqliteLocalWorkItemRepository(store).FindAsync(
            new ExternalWorkItemIdentity("ado", items.DurableScope!, "88"));
        external.ShouldBe(local);
        (await items.GetSeedsAsync()).ShouldBeEmpty();
        (await runtime.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(88);
        var remappedHistory = await history.GetHistoryAsync();
        remappedHistory.CursorEntryId.ShouldBe(originalHistory.CursorEntryId);
        remappedHistory.Entries.Select(entry => entry.Id).ShouldBe(originalHistory.Entries.Select(entry => entry.Id));
        remappedHistory.Entries.Select(entry => entry.VisitedAt).ShouldBe(originalHistory.Entries.Select(entry => entry.VisitedAt));
        remappedHistory.Entries.Select(entry => entry.WorkItemId).ShouldBe(new[] { 88, 88, 42 });
        (await runtime.GetRequiredService<IPendingChangeStore>().GetChangesAsync(88))
            .ShouldHaveSingleItem().NewValue.ShouldBe("Retained staged note");
        (await relationships.GetAsync(local.Value, parentLocal.Value, LinkKind.Related)).ShouldBe(edge.Current);
        migration.VerifyOwnedParity();
        runtime.Dispose();
        var restarted = await fixture.CreateRuntimeAsync(initializeBench: false);
        var restartedStore = restarted.GetRequiredService<SqliteCacheStore>();
        var recovered = new PreservationMigrationService(restartedStore, fixture.CurrentPaths(), items.DurableScope!);
        recovered.FindSeedLocalId(minted.Identity).ShouldBe(local);
        (await restarted.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(88);
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }
}
