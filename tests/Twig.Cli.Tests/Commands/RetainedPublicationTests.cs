using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Shouldly;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Aggregates;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Trait("Category", "HostInventory")]
public sealed class RetainedPublicationTests : IAsyncLifetime
{
    private const string WorkerRoot = "TWIG_RETAINED_PUBLICATION_WORKER_ROOT";
    private const string WorkerNonce = "TWIG_RETAINED_PUBLICATION_WORKER_NONCE";
    private const string WorkerPhase = "TWIG_RETAINED_PUBLICATION_WORKER_PHASE";
    private const string WorkerTempRoot = "TWIG_RETAINED_PUBLICATION_PARENT_TEMP";
    private readonly NativeBrowserFixture _fixture;
    private readonly bool _worker;

    public RetainedPublicationTests()
    {
        var root = Environment.GetEnvironmentVariable(WorkerRoot);
        _worker = root is not null;
        if (root is null)
        {
            root = CanonicalTempRoot.Create("twig-retained-publish-");
        }
        else
        {
            root = ValidateWorkerRoot(root);
        }
        _fixture = new NativeBrowserFixture(root);
    }

    public Task InitializeAsync() => _worker ? _fixture.OpenExistingAsync() : _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync().AsTask();

    [Fact]
    public async Task ExactRepeatKeepsFirstEvidenceAndContradictionsLeaveWholeStandingUnchanged()
    {
        var state = await CreateRetainedAsync();
        var intents = state.Runtime.GetRequiredService<IPublishIntentRepository>();
        var mappings = state.Runtime.GetRequiredService<IPublishIdMapRepository>();
        await intents.RecordIntentAsync(state.Identity.Identity, state.Seed.Title, state.Seed.Type.Value);
        await intents.CompleteIntentAsync(state.Identity.Identity, 88);
        await state.Runtime.GetRequiredService<SeedPublishOrchestrator>().CompleteLocalPublishAsync(
            state.Seed, NativeBrowserFixture.Item(88, state.Seed.Title), 88, CancellationToken.None);
        var before = Capture(state);

        await mappings.RecordMappingAsync(state.Identity.Identity, 88);
        await intents.CompleteIntentAsync(state.Identity.Identity, 88);
        Capture(state).ShouldBe(before);
        var intentError = await Should.ThrowAsync<InvalidOperationException>(
            () => intents.CompleteIntentAsync(state.Identity.Identity, 99));
        intentError.Message.ShouldStartWith("preservation-publication-conflict:");
        Capture(state).ShouldBe(before);
        var mappingError = await Should.ThrowAsync<InvalidOperationException>(
            () => mappings.RecordMappingAsync(state.Identity.Identity, 99));
        mappingError.Message.ShouldStartWith("preservation-publication-conflict:");
        Capture(state).ShouldBe(before);
        var completionError = await Should.ThrowAsync<InvalidOperationException>(
            () => state.Runtime.GetRequiredService<SeedPublishOrchestrator>().CompleteLocalPublishAsync(
                state.Seed, NativeBrowserFixture.Item(99, state.Seed.Title), 99, CancellationToken.None));
        completionError.Message.ShouldStartWith("preservation-publication-conflict:");
        Capture(state).ShouldBe(before);
        (await mappings.GetNewIdAsync(state.Identity.Identity)).ShouldBe(88);
        _fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task KnownMappingWithUnsettledIntentRefusesBeforeCreateOrIntentRewrite()
    {
        var state = await CreateRetainedAsync();
        var intents = state.Runtime.GetRequiredService<IPublishIntentRepository>();
        await intents.RecordIntentAsync(state.Identity.Identity, state.Seed.Title, state.Seed.Type.Value);
        await state.Runtime.GetRequiredService<IPublishIdMapRepository>()
            .RecordMappingAsync(state.Identity.Identity, 88);
        var before = Capture(state);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => state.Runtime.GetRequiredService<SeedPublishOrchestrator>().PublishAsync(state.Seed.Id, force: true));

        error.Message.ShouldStartWith("preservation-publication-recovery-required:");
        Capture(state).ShouldBe(before);
        (await intents.GetIntentAsync(state.Identity.Identity))!.PublishedId.ShouldBeNull();
        (await state.Items.GetByIdAsync(state.Seed.Id)).ShouldNotBeNull();
        _fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task ConflictingIntentCompletionPreservesPriorMapAndUnpublishedPayload()
    {
        var state = await CreateRetainedAsync();
        var intents = state.Runtime.GetRequiredService<IPublishIntentRepository>();
        await intents.RecordIntentAsync(state.Identity.Identity, state.Seed.Title, state.Seed.Type.Value);
        await state.Runtime.GetRequiredService<IPublishIdMapRepository>()
            .RecordMappingAsync(state.Identity.Identity, 88);
        var before = Capture(state);

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => intents.CompleteIntentAsync(state.Identity.Identity, 99));

        error.Message.ShouldStartWith("preservation-publication-conflict:");
        Capture(state).ShouldBe(before);
        (await intents.GetIntentAsync(state.Identity.Identity))!.PublishedId.ShouldBeNull();
        _fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task StaleNegativeSaveCannotRecreateACompletedRetainedSeed()
    {
        var state = await CreateRetainedAsync();
        var intents = state.Runtime.GetRequiredService<IPublishIntentRepository>();
        await intents.RecordIntentAsync(state.Identity.Identity, state.Seed.Title, state.Seed.Type.Value);
        await intents.CompleteIntentAsync(state.Identity.Identity, 88);
        var orchestrator = state.Runtime.GetRequiredService<SeedPublishOrchestrator>();
        await orchestrator.CompleteLocalPublishAsync(
            state.Seed, NativeBrowserFixture.Item(88, state.Seed.Title), 88, CancellationToken.None);
        var before = Capture(state);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => state.Items.SaveAsync(state.Seed));

        error.Message.ShouldStartWith("preservation-seed-already-published:");
        Capture(state).ShouldBe(before);
        (await state.Items.GetSeedsAsync()).ShouldBeEmpty();
        (await state.Runtime.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(88);
        _fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public Task CrashAfterPublishMapKeepsKnownOutcomeAndStableIdentity()
        => VerifyProcessCrashAsync("publish-map");

    [Fact]
    public Task CrashAfterIdentityBindingKeepsKnownOutcomeAndStableIdentity()
        => VerifyProcessCrashAsync("identity-binding");

    [Fact]
    public Task CrashAfterPayloadDeletionKeepsKnownOutcomeAndStableIdentity()
        => VerifyProcessCrashAsync("payload-delete");

    [CrashWorkerFact]
    public async Task CrashWorker()
    {
        var phase = Environment.GetEnvironmentVariable(WorkerPhase)
            ?? throw new InvalidOperationException("A crash phase is required.");
        var root = Environment.GetEnvironmentVariable(WorkerRoot)!;
        var runtime = await _fixture.CreateRuntimeAsync(initializeBench: false);
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var seed = (await runtime.GetRequiredService<IWorkItemRepository>().GetSeedsAsync()).ShouldHaveSingleItem();
        var intent = await runtime.GetRequiredService<IPublishIntentRepository>().GetIntentAsync(seed.StagedIdentity!.Value);
        intent.ShouldNotBeNull();
        var positiveId = intent.PublishedId
            ?? throw new InvalidOperationException("The owned worker needs the preexisting acknowledged positive ID.");
        positiveId.ShouldBe(88);
        var trigger = phase switch
        {
            "publish-map" => "AFTER INSERT ON pending.publish_id_map WHEN NEW.new_id=88",
            "identity-binding" => "AFTER INSERT ON pending.external_identities WHEN NEW.provider_kind='ado' AND NEW.native_id='88'",
            "payload-delete" => $"AFTER DELETE ON pending.seed_work_items WHEN OLD.id={seed.Id}",
            _ => throw new InvalidOperationException("Unknown owned crash phase."),
        };
        store.GetConnection().CreateFunction<int>("retained_publication_crash", () =>
        {
            using var process = Process.GetCurrentProcess();
            var marker = Path.Combine(root, $"crash-hit-{phase}.txt");
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                $"phase={phase}\npid={process.Id}\nstart-utc={process.StartTime.ToUniversalTime():O}\nwork-mutations={_fixture.Transport.WorkMutationAttempts}\n");
            using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            process.Kill();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        });
        using (var install = store.GetConnection().CreateCommand())
        {
            install.CommandText = $"CREATE TEMP TRIGGER owned_retained_crash {trigger} BEGIN SELECT retained_publication_crash(); END;";
            install.ExecuteNonQuery();
        }
        await runtime.GetRequiredService<SeedPublishOrchestrator>().CompleteLocalPublishAsync(
            seed, NativeBrowserFixture.Item(positiveId, seed.Title), positiveId, CancellationToken.None);
        throw new InvalidOperationException("The real production transaction did not reach its required crash boundary.");
    }

    private async Task VerifyProcessCrashAsync(string phase)
    {
        var state = await CreateRetainedAsync();
        var intents = state.Runtime.GetRequiredService<IPublishIntentRepository>();
        await intents.RecordIntentAsync(state.Identity.Identity, state.Seed.Title, state.Seed.Type.Value);
        await intents.CompleteIntentAsync(state.Identity.Identity, 88);
        var local = new PreservationMigrationService(state.Store, _fixture.CurrentPaths(), state.Scope)
            .FindSeedLocalId(state.Identity.Identity);
        local.ShouldNotBeNull();
        var before = Capture(state);
        var expectedHistory = await state.Runtime.GetRequiredService<INavigationHistoryStore>().GetHistoryAsync();
        var root = Path.GetDirectoryName(_fixture.Paths.RepoRoot)!;
        var nonce = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(root, "crash-owner.txt"), nonce);
        state.Runtime.Dispose();

        var result = await RunCrashWorkerAsync(root, nonce, phase);
        result.ExitCode.ShouldNotBe(0, result.Output);
        result.Output.ToLowerInvariant().ShouldContain("test host process crashed");
        var marker = await File.ReadAllTextAsync(Path.Combine(root, $"crash-hit-{phase}.txt"));
        marker.ShouldContain($"phase={phase}");
        marker.ShouldContain("work-mutations=0");

        var restarted = await _fixture.CreateRuntimeAsync(initializeBench: false);
        state = state with
        {
            Runtime = restarted,
            Store = restarted.GetRequiredService<SqliteCacheStore>(),
            Items = (SqliteWorkItemRepository)restarted.GetRequiredService<IWorkItemRepository>(),
        };
        Capture(state).ShouldBe(before);
        var known = await restarted.GetRequiredService<IPublishIntentRepository>().GetIntentAsync(state.Identity.Identity);
        known.ShouldNotBeNull();
        var positiveId = known.PublishedId
            ?? throw new InvalidOperationException("The process interruption lost its acknowledged positive evidence.");
        positiveId.ShouldBe(88);
        var recovery = new PreservationMigrationService(state.Store, _fixture.CurrentPaths(), state.Scope);
        recovery.FindSeedLocalId(state.Identity.Identity).ShouldBe(local);
        await restarted.GetRequiredService<SeedPublishOrchestrator>().CompleteLocalPublishAsync(
            state.Seed, NativeBrowserFixture.Item(positiveId, state.Seed.Title), positiveId, CancellationToken.None);
        recovery.FindSeedLocalId(state.Identity.Identity).ShouldBe(local);
        var external = await new SqliteLocalWorkItemRepository(state.Store)
            .FindAsync(new ExternalWorkItemIdentity("ado", state.Scope, "88"));
        external.ShouldBe(local);
        (await state.Items.GetSeedsAsync()).ShouldBeEmpty();
        (await restarted.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(88);
        var history = await restarted.GetRequiredService<INavigationHistoryStore>().GetHistoryAsync();
        history.CursorEntryId.ShouldBe(expectedHistory.CursorEntryId);
        history.Entries.Select(entry => entry.Id).ShouldBe(expectedHistory.Entries.Select(entry => entry.Id));
        history.Entries.Select(entry => entry.VisitedAt).ShouldBe(expectedHistory.Entries.Select(entry => entry.VisitedAt));
        history.Entries.Select(entry => entry.WorkItemId).ShouldBe(new[] { 88, 88, 42 });
        (await restarted.GetRequiredService<IPendingChangeStore>().GetChangesAsync(88))
            .ShouldHaveSingleItem().NewValue.ShouldBe("Retained pending note");
        _fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    private static async Task<(int ExitCode, string Output)> RunCrashWorkerAsync(
        string root, string nonce, string phase)
    {
        var settingsRoot = new DirectoryInfo(Path.GetDirectoryName(typeof(RetainedPublicationTests).Assembly.Location)!);
        while (!File.Exists(Path.Combine(settingsRoot.FullName, "test.runsettings")))
            settingsRoot = settingsRoot.Parent
                ?? throw new InvalidOperationException("The repository runsettings must be available for the crash worker.");
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = settingsRoot.FullName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("vstest");
        info.ArgumentList.Add(typeof(RetainedPublicationTests).Assembly.Location);
        info.ArgumentList.Add("--Settings:" + Path.Combine(settingsRoot.FullName, "test.runsettings"));
        info.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=Twig.Cli.Tests.Commands.RetainedPublicationTests.CrashWorker");
        info.Environment[WorkerRoot] = root;
        info.Environment[WorkerNonce] = nonce;
        info.Environment[WorkerPhase] = phase;
        info.Environment[WorkerTempRoot] = CanonicalTempRoot.Canonicalize(Path.GetTempPath());
        info.Environment["TWIG_TEST_TRACE"] = Path.Combine(root, "worker-trace-" + phase);
        info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("The owned crash worker could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Exception? primaryFailure = null;
        (int ExitCode, string Output)? result = null;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var text = await output.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token)
                + "\n" + await error.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(root, "worker-" + phase + ".log"), text)
                .WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            result = (process.ExitCode, text);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
        }
        var cleanupFailures = new List<Exception>();
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception failure)
        {
            cleanupFailures.Add(new InvalidOperationException("Owned worker termination or bounded root reap failed; descendant cleanup is unconfirmed.", failure));
        }
        if (primaryFailure is not null)
        {
            foreach (var drain in new[] { output, error })
            {
                try { _ = await drain.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception failure)
                {
                    cleanupFailures.Add(new InvalidOperationException("Owned worker redirected output did not drain within its cleanup bound.", failure));
                }
            }
            var failures = new List<Exception> { primaryFailure };
            failures.AddRange(cleanupFailures);
            throw new AggregateException(
                "Owned crash-worker execution failed; root exit is not descendant-reap proof and cleanup uncertainty remains explicit.",
                failures);
        }
        if (cleanupFailures.Count != 0)
            throw new AggregateException("Owned crash-worker cleanup failed; no process-tree cleanup claim is made.", cleanupFailures);
        return result ?? throw new InvalidOperationException("The owned worker produced no terminal result.");
    }

    private static string ValidateWorkerRoot(string root)
    {
        var expectedTemp = Environment.GetEnvironmentVariable(WorkerTempRoot);
        var nonce = Environment.GetEnvironmentVariable(WorkerNonce);
        if (!Path.IsPathFullyQualified(root) || expectedTemp is null
            || !Path.IsPathFullyQualified(expectedTemp) || !Guid.TryParseExact(nonce, "N", out _))
            throw new InvalidOperationException("The crash worker requires its parent's owned canonical temp root and nonce.");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var fullRoot = Path.GetFullPath(root);
        var canonicalTemp = CanonicalTempRoot.Canonicalize(Path.GetTempPath());
        if (!comparer.Equals(Path.GetFullPath(expectedTemp), canonicalTemp)
            || !comparer.Equals(Path.GetDirectoryName(fullRoot), canonicalTemp)
            || !comparer.Equals(CanonicalTempRoot.Canonicalize(fullRoot), fullRoot)
            || !Path.GetFileName(fullRoot).StartsWith("twig-retained-publish-", StringComparison.Ordinal))
            throw new InvalidOperationException("The worker root is not the exact direct child of its parent's owned canonical temp root.");
        for (DirectoryInfo? ancestor = new DirectoryInfo(fullRoot); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A crash-worker root cannot traverse a reparse point or junction.");
        }
        var marker = Path.Combine(fullRoot, "crash-owner.txt");
        if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(marker) != nonce)
            throw new InvalidOperationException("The owned crash-worker nonce marker does not match.");
        return fullRoot;
    }

    private async Task<State> CreateRetainedAsync()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Parent"));
        var identity = await runtime.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
        var created = new SeedFactory().CreateUnparented("Retained publish",
            WorkItemType.Task, AreaPath.Parse("Work").Value, IterationPath.Parse("Work").Value,
            identity, parentId: 42);
        created.IsSuccess.ShouldBeTrue(created.Error);
        await items.SaveAsync(created.Value);
        await runtime.GetRequiredService<IPendingChangeStore>()
            .AddChangeAsync(created.Value.Id, "note", null, null, "Retained pending note");
        await runtime.GetRequiredService<ISeedLinkRepository>().AddLinkAsync(
            new SeedLink(created.Value.Id, 42, SeedLinkTypes.Related, DateTimeOffset.UtcNow));
        await runtime.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(created.Value.Id);
        var history = runtime.GetRequiredService<INavigationHistoryStore>();
        await history.RecordVisitAsync(created.Value.Id);
        await history.RecordVisitAsync(created.Value.Id);
        await history.RecordVisitAsync(42);
        await history.GoBackAsync();
        var migration = new PreservationMigrationService(store, _fixture.CurrentPaths(), items.DurableScope!);
        await migration.ApplyAsync(migration.Preview().Digest);
        var local = migration.FindSeedLocalId(identity.Identity);
        local.ShouldNotBeNull();
        var parent = await new SqliteLocalWorkItemRepository(store).FindAsync(
            new ExternalWorkItemIdentity("ado", items.DurableScope!, "42"));
        parent.ShouldNotBeNull();
        var now = DateTimeOffset.UtcNow;
        (await new SqliteLocalRelationshipRepository(store).UpsertAsync(new LocalRelationship(
            local.Value, parent.Value, LinkKind.Related, EmulationStorage.LocalOnly,
            PublicationState.NotApplicable, 0, now, now), null)).Status.ShouldBe(LocalRelationshipWriteStatus.Written);
        return new(runtime, store, items, created.Value, identity, items.DurableScope!);
    }

    private static string[] Capture(State state)
    {
        var report = new StoreParityHarness(state.Store, state.Scope, preserveOpaqueContext: true).Inspect();
        report.IsConsistent.ShouldBeTrue();
        return report.Classes.Select(entry => $"{entry.ClassName}:{entry.RowCount}:{entry.CanonicalDigest}").ToArray();
    }

    private sealed record State(ServiceProvider Runtime, SqliteCacheStore Store,
        SqliteWorkItemRepository Items, WorkItem Seed, StagedSeedIdentity Identity, string Scope);

    public sealed class CrashWorkerFactAttribute : FactAttribute
    {
        public CrashWorkerFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(WorkerRoot) is null)
                Skip = "Dedicated process-kill worker; requires its parent's owned root and nonce.";
        }
    }
}
