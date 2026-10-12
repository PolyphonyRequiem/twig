using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Browser;

[Trait("Category", "HostInventory")]
public sealed class BrowserReadModuleTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [HostMigrationFact]
    public async Task ReturnedDetailReflowsAfterRealStoresChangeAndRuntimeRetiresWithoutHoldingIdleLeases()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        var item = NativeBrowserFixture.Item(42, "Captured title");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>Captured description</p>",
            ["Custom.Body"] = "<p>Retained metadata body</p><p>BODY-TAIL</p>",
        });
        await repository.SaveAsync(item);
        var definitions = runtime.GetRequiredService<IFieldDefinitionStore>();
        await definitions.SaveBatchAsync([new("Custom.Body", "Captured body label", "html", false)]);
        await runtime.GetRequiredService<IWorkItemLinkRepository>().SaveLinksAsync(42, [new(42, 99, LinkTypes.Related)]);
        var module = _fixture.CreateModule(runtime);
        var result = await module.ReadDetailAsync(new(42, Width: 120, Ansi: false));
        var observation = Success(result);
        observation.InitialPresentation.Ansi.ShouldContain("BODY-TAIL");
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Replacement cache title"));
        await definitions.SaveBatchAsync([new("Custom.Body", "Replacement label", "string", false)]);
        await runtime.GetRequiredService<IWorkItemLinkRepository>().SaveLinksAsync(42, []);
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
        var rejected = await module.ReadDetailAsync(new(42, Captured: observation.Capture));
        Failure(rejected, BrowserReadFailureKind.AuthorityInvalidated);
        runtime.Dispose();
        using var exclusive = ConnectionOperationGate.Acquire(_fixture.Paths.StartDir!, exclusive: true);
        var compact = await observation.RenderAsync(40, ansi: false);
        compact.Title.ShouldBe("Captured title");
        compact.Ansi.ShouldContain("Captured description");
        compact.Ansi.ShouldContain("BODY-TAIL");
        compact.Ansi.ShouldContain("#99");
        compact.Ansi.ShouldNotContain("Replacement cache title");
        compact.Ansi.ShouldNotContain("Replacement label");
        compact.Ansi.ShouldNotContain("\u001b");
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
    }

    [HostMigrationFact]
    public async Task InFlightMetadataRetainsRealAuthorityLeaseAndChangedBenchCannotPublishItsDetail()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Private original"));
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var replacement = (await benches.CreateAsync("replacement"))!;
        var barrier = new MetadataBarrier(runtime.GetRequiredService<IFieldDefinitionStore>());
        var module = _fixture.CreateModule(runtime, barrier.Store);
        var read = module.ReadDetailAsync(new(42));
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            read.IsCompleted.ShouldBeFalse();
            var transition = await _fixture.Transitions.PreviewAsync(_fixture.Configuration, _fixture.CurrentPaths(), _fixture.SiblingBinding.BindingId);
            transition.CanApply.ShouldBeFalse();
            transition.Blockers.ShouldContain(blocker => blocker.Contains("binding-operation-active", StringComparison.Ordinal));
            await benches.SetCurrentAsync(replacement.Id);
        }
        finally { barrier.Release.TrySetResult(); }
        Failure(await read, BrowserReadFailureKind.AuthorityInvalidated);
        (await benches.GetCurrentAsync())!.Id.ShouldBe(replacement.Id);
        Success(await _fixture.CreateModule(runtime).ReadDetailAsync(new(42))).Title.ShouldBe("Private original");
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
    }

    [HostMigrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameBenchSelectorMutationDuringReadInvalidatesCaptureBeforeDetailCanBeReturned(bool changeQuery)
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Captured private detail"));
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var captured = (await benches.CreateAsync("captured"))!;
        await benches.AddSelectorAsync(captured.Id, BenchSelector.ForCurrentSprintCanonical("original@example.test"));
        await benches.AddSelectorAsync(captured.Id, BenchSelector.ForItem(42));
        await benches.SetCurrentAsync(captured.Id);
        captured = (await benches.GetCurrentAsync())!;
        var barrier = new MetadataBarrier(runtime.GetRequiredService<IFieldDefinitionStore>());
        var read = _fixture.CreateModule(runtime, barrier.Store).ReadDetailAsync(new(42));
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var expectedSettings = Twig.Domain.Services.Workspace.BenchQueryRule.SettingsDigest(captured.Selectors);
            if (changeQuery)
                (await benches.TryReplaceQuerySelectorsAsync(captured.Id, expectedSettings,
                    [BenchSelector.ForCurrentSprintCanonical("replacement@example.test")])).ShouldBeTrue();
            else
                (await benches.TryUpdatePinsAsync(captured.Id, 42, includeSubtree: true, remove: false,
                    expectedSettingsDigest: expectedSettings)).ShouldBeTrue();
            (await benches.GetCurrentAsync())!.Id.ShouldBe(captured.Id);
        }
        finally { barrier.Release.TrySetResult(); }
        Failure(await read, BrowserReadFailureKind.AuthorityInvalidated);
        var after = (await benches.GetCurrentAsync())!;
        after.Selectors.ShouldContain(changeQuery
            ? BenchSelector.ForCurrentSprintCanonical("replacement@example.test")
            : BenchSelector.ForSubtree(42));
        Success(await _fixture.CreateModule(runtime).ReadDetailAsync(new(42))).Title.ShouldBe("Captured private detail");
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
    }

    [HostMigrationTheory]
    [InlineData("binding-revision")]
    [InlineData("selection-source")]
    [InlineData("selection-revision")]
    [InlineData("operation")]
    [InlineData("storage-generation")]
    public async Task SameIdsCannotAdmitChangedNativeAuthorityEvidenceDuringAnAsyncRead(string changedEvidence)
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Private original"));
        var original = await _fixture.Bindings.ResolveAsync(_fixture.Configuration, _fixture.CurrentPaths());
        var current = original;
        var bindings = Substitute.For<IConnectionBindingService>();
        bindings.ResolveAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>()).Returns(_ => current);
        var barrier = new MetadataBarrier(runtime.GetRequiredService<IFieldDefinitionStore>());
        var read = _fixture.CreateModule(runtime, barrier.Store, bindings).ReadDetailAsync(new(42));
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            current = changedEvidence switch
            {
                "binding-revision" => original with { Binding = original.Binding with { Revision = original.Binding.Revision + 1 } },
                "selection-source" => original with { SelectionSource = original.SelectionSource + "-changed" },
                "selection-revision" => original with { SelectionRevision = original.SelectionRevision + 1 },
                "operation" => original with { Operation = original.Operation with { Team = "new-team" } },
                "storage-generation" => original with { StorageGeneration = Guid.NewGuid().ToString("N") },
                _ => throw new ArgumentOutOfRangeException(nameof(changedEvidence)),
            };
        }
        finally { barrier.Release.TrySetResult(); }
        Failure(await read, BrowserReadFailureKind.AuthorityInvalidated);
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
    }

    [HostMigrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFailureDistinguishesSameOriginServiceErrorFromAuthorityInvalidation(bool invalidate)
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Cached original"));
        var original = await _fixture.Bindings.ResolveAsync(_fixture.Configuration, _fixture.CurrentPaths());
        var current = original;
        var bindings = Substitute.For<IConnectionBindingService>();
        bindings.ResolveAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>()).Returns(_ => current);
        var metadata = Substitute.For<IFieldDefinitionStore>();
        metadata.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (invalidate) current = original with { SelectionRevision = original.SelectionRevision + 1 };
            return Task.FromException<IReadOnlyList<FieldDefinition>>(new IOException("Metadata store unavailable"));
        });
        var result = await _fixture.CreateModule(runtime, metadata, bindings).ReadDetailAsync(new(42));
        Failure(result, invalidate ? BrowserReadFailureKind.AuthorityInvalidated : BrowserReadFailureKind.Unavailable);
        (await runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Cached original");
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
    }

    [HostMigrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InitialSameOriginBenchReadFailureRemainsUnavailableAndCanBeRetried(bool failCurrentLookup)
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Retryable private cache"));
        var actual = runtime.GetRequiredService<IBenchRepository>();
        var scheduled = Substitute.For<IBenchRepository>();
        var failNext = true;
        scheduled.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (failCurrentLookup && failNext)
            {
                failNext = false;
                return Task.FromException<Twig.Domain.Aggregates.Bench?>(new IOException("Temporary Bench storage failure"));
            }
            return actual.GetCurrentAsync(call.ArgAt<CancellationToken>(0));
        });
        scheduled.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (!failCurrentLookup && failNext)
            {
                failNext = false;
                return Task.FromException<Twig.Domain.Aggregates.Bench?>(new IOException("Temporary default Bench storage failure"));
            }
            return actual.GetByNameAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1));
        });
        var module = _fixture.CreateModule(runtime, benches: scheduled);
        Failure(await module.ReadDetailAsync(new(42)), BrowserReadFailureKind.Unavailable);
        Success(await module.ReadDetailAsync(new(42))).Title.ShouldBe("Retryable private cache");
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
    }

    [HostMigrationFact]
    public async Task ActualNativeOperationContentionRetainsCapturedReadForRetryButNotChangedAuthority()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Private cached detail"));
        var module = _fixture.CreateModule(runtime);
        var observation = Success(await module.ReadDetailAsync(new(42)));
        using (ConnectionOperationGate.Acquire(_fixture.Paths.StartDir!, exclusive: true))
        {
            Task<BrowserDetailResult> blocked;
            // Model another actor rather than inheriting this actor's reentrant exclusive scope.
            using (ExecutionContext.SuppressFlow())
                blocked = Task.Run(() => module.ReadDetailAsync(new(42, Captured: observation.Capture)));
            Failure(await blocked, BrowserReadFailureKind.Unavailable);
            (await observation.RenderAsync(80, ansi: false)).Ansi.ShouldContain("Private cached detail");
        }

        Success(await module.ReadDetailAsync(new(42, Captured: observation.Capture))).Title.ShouldBe("Private cached detail");
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
        Failure(await module.ReadDetailAsync(new(42, Captured: observation.Capture)), BrowserReadFailureKind.AuthorityInvalidated);
    }

    [HostMigrationFact]
    public async Task LockedNativeAdditiveRegistryOpenIsUnavailableAndRetryPreservesAdmittedOrigin()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Private cached content"));
        var original = await _fixture.Bindings.ResolveAsync(_fixture.Configuration, _fixture.CurrentPaths());
        await using var database = new SqliteConnection($"Data Source={_fixture.Bindings.RegistryPath}");
        await database.OpenAsync();
        using (var version = database.CreateCommand())
        {
            // A supported native v8 additive boundary requires a writer during cold open.
            // Existing identity, selection and mirror evidence is unchanged; v9's additive
            // tables may already exist, which the canonical upgrade explicitly tolerates.
            version.CommandText = "UPDATE layout_meta SET version=8 WHERE id=1;";
            (await version.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        using var http = new HttpClient(_fixture.Transport, disposeHandler: false);
        using var coldBindings = new ConnectionBindingService(_fixture.Home, Substitute.For<ITokenRefresher>(), patHttpClient: http);
        var module = _fixture.CreateModule(runtime, bindings: coldBindings);
        // BEGIN IMMEDIATE is the real barrier: ordinary WAL SELECTs remain readable, but
        // native cold-open admission cannot complete its required additive writer. Allow
        // the existing native 30-second busy policy to expire; no sleeps or fake timeout.
        using (var writer = database.BeginTransaction(deferred: false))
        {
            try { Failure(await module.ReadDetailAsync(new(42)), BrowserReadFailureKind.Unavailable); }
            finally { writer.Rollback(); }
        }
        var retried = Success(await module.ReadDetailAsync(new(42)));
        retried.Title.ShouldBe("Private cached content");
        retried.Capture.Origin.Binding.ShouldBe(original.Binding);
        retried.Capture.StorageGeneration.ShouldBe(original.StorageGeneration);
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
    }

    [HostMigrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPortableAuthorityChangeDuringMetadataReadCannotUseFrozenCommandConfiguration(bool changeProject)
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Original endpoint private cache"));
        var originalTeam = _fixture.Configuration.Team;
        var originalProject = _fixture.Configuration.Project;
        var originalOrigin = await _fixture.Bindings.ResolveAsync(_fixture.Configuration, _fixture.CurrentPaths());
        var barrier = new MetadataBarrier(runtime.GetRequiredService<IFieldDefinitionStore>());
        var read = _fixture.CreateModule(runtime, barrier.Store).ReadDetailAsync(new(42));
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var edited = await TwigConfiguration.LoadSplitAsync(_fixture.Paths);
            if (changeProject) edited.Project = "ReplacementProject";
            else edited.Team = "ReplacementTeam";
            await edited.SaveSplitAsync(_fixture.Paths);
            _fixture.Configuration.Project.ShouldBe(originalProject);
            _fixture.Configuration.Team.ShouldBe(originalTeam);
            barrier.Release.TrySetResult();
            Failure(await read, BrowserReadFailureKind.AuthorityInvalidated);
        }
        finally
        {
            barrier.Release.TrySetResult();
            await read;
            await _fixture.Configuration.SaveSplitAsync(_fixture.Paths);
        }
        var restoredOrigin = await _fixture.Bindings.ResolveAsync(_fixture.Configuration, _fixture.CurrentPaths());
        restoredOrigin.Binding.ShouldBe(originalOrigin.Binding);
        Success(await _fixture.CreateModule(runtime).ReadDetailAsync(new(42))).Title.ShouldBe("Original endpoint private cache");
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
    }

    [HostMigrationFact]
    public async Task InitialAdmittedCacheMissRetainsRefusalOnlyCaptureSoRefreshAndSyncCannotRebindAfterBenchSwitch()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var original = (await benches.CreateAsync("original"))!;
        var replacement = (await benches.CreateAsync("replacement"))!;
        await benches.SetCurrentAsync(original.Id);
        var module = _fixture.CreateModule(runtime);
        var missing = await module.ReadDetailAsync(new(42));
        Failure(missing, BrowserReadFailureKind.Unavailable);
        var capture = missing.Capture.ShouldNotBeNull();
        await benches.SetCurrentAsync(replacement.Id);
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Replacement Bench content"));
        Failure(await module.ReadDetailAsync(new(42, Captured: capture)), BrowserReadFailureKind.AuthorityInvalidated);
        Failure(await module.ReadDetailAsync(new(42, Sync: true, Captured: capture)), BrowserReadFailureKind.AuthorityInvalidated);
        Success(await module.ReadDetailAsync(new(42))).Title.ShouldBe("Replacement Bench content");
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
    }

    [HostMigrationFact]
    public async Task CanceledMetadataReadCannotReturnLateObservationAndReleasesItsNativeLeases()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Never returned"));
        var barrier = new MetadataBarrier(runtime.GetRequiredService<IFieldDefinitionStore>());
        using var cancellation = new CancellationTokenSource();
        var read = _fixture.CreateModule(runtime, barrier.Store).ReadDetailAsync(new(42), cancellation.Token);
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
        }
        finally { barrier.Release.TrySetResult(); }
        var canceled = await read;
        Failure(canceled, BrowserReadFailureKind.Canceled);
        var capture = canceled.Capture.ShouldNotBeNull();
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var replacement = (await benches.CreateAsync("after-cancel"))!;
        await benches.SetCurrentAsync(replacement.Id);
        var module = _fixture.CreateModule(runtime);
        Failure(await module.ReadDetailAsync(new(42, Captured: capture)), BrowserReadFailureKind.AuthorityInvalidated);
        Failure(await module.ReadDetailAsync(new(42, Sync: true, Captured: capture)), BrowserReadFailureKind.AuthorityInvalidated);
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
    }

    [HostMigrationFact]
    public async Task CanceledSelectedSyncCannotPublishLateHttpResponseOrReplaceCachedRootAndEdges()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        var links = runtime.GetRequiredService<IWorkItemLinkRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Cached before cancel"));
        await links.SaveLinksAsync(42, [new(42, 88, LinkTypes.Related)]);
        var verified = await links.GetLinksVerifiedAtAsync(42);
        _fixture.Transport.HoldNext = true;
        using var cancellation = new CancellationTokenSource();
        var read = _fixture.CreateModule(runtime).ReadDetailAsync(new(42, Sync: true), cancellation.Token);
        try
        {
            await _fixture.Transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            read.IsCompleted.ShouldBeFalse();
            cancellation.Cancel();
        }
        finally { _fixture.Transport.Release.TrySetResult(); }
        Failure(await read, BrowserReadFailureKind.Canceled);
        (await repository.GetByIdAsync(42))!.Title.ShouldBe("Cached before cancel");
        (await links.GetLinksAsync(42)).ShouldBe([new WorkItemLink(42, 88, LinkTypes.Related)]);
        (await links.GetLinksVerifiedAtAsync(42)).ShouldBe(verified);
        _fixture.Transport.Mutations.ShouldBe(0);
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
    }

    [HostMigrationFact]
    public async Task ExplicitSelectedSyncPullsOnlyRootAndEdgeMetadataWithoutChangingActiveItemOrFetchingTargets()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(80, "Unrelated cached work"));
        var context = runtime.GetRequiredService<IContextStore>();
        var active = await context.GetActiveWorkItemIdAsync();
        var observation = Success(await _fixture.CreateModule(runtime).ReadDetailAsync(new(42, Ansi: false, Sync: true)));
        observation.Title.ShouldBe("Remote selected item");
        observation.InitialPresentation.Ansi.ShouldContain("Remote description");
        observation.InitialPresentation.Ansi.ShouldContain("#99");
        observation.LinksVerifiedAt.ShouldNotBeNull();
        (await runtime.GetRequiredService<IWorkItemLinkRepository>().GetLinksAsync(42))
            .ShouldBe([new WorkItemLink(42, 99, LinkTypes.Related)]);
        (await repository.GetByIdAsync(99)).ShouldBeNull();
        (await repository.GetByIdAsync(80))!.Title.ShouldBe("Unrelated cached work");
        (await context.GetActiveWorkItemIdAsync()).ShouldBe(active);
        _fixture.Transport.RequestedItems.ShouldBe([42]);
        _fixture.Transport.Mutations.ShouldBe(0);
        Failure(await _fixture.CreateModule(runtime).ReadDetailAsync(new(-3, Sync: true)), BrowserReadFailureKind.Usage);
        _fixture.Transport.RequestedItems.ShouldBe([42]);
    }

    [HostMigrationFact]
    public async Task SelectedSyncPreservesRealPendingAndDirtyFieldsWhileRefreshingOnlyEdgeVerification()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        var item = NativeBrowserFixture.Item(42, "Protected local title");
        item.UpdateField("System.Description", "<p>Unpublished description</p>");
        await repository.SaveAsync(item);
        var pending = runtime.GetRequiredService<IPendingChangeStore>();
        await pending.AddChangeAsync(42, "field", "System.Description", "Old description", "<p>Unpublished description</p>");
        await pending.AddChangeAsync(42, "note", null, null, "Unpublished note");
        var before = await pending.GetChangesAsync(42);
        var observation = Success(await _fixture.CreateModule(runtime).ReadDetailAsync(new(42, Ansi: false, Sync: true)));
        observation.HasLocalChanges.ShouldBeTrue();
        observation.InitialPresentation.Ansi.ShouldContain("Protected local title");
        observation.InitialPresentation.Ansi.ShouldContain("Unpublished description");
        observation.InitialPresentation.Ansi.ShouldNotContain("Remote description");
        var retained = (await repository.GetByIdAsync(42))!;
        retained.IsDirty.ShouldBeTrue();
        retained.Revision.ShouldBe(3);
        retained.Fields["System.Description"].ShouldBe("<p>Unpublished description</p>");
        (await pending.GetChangesAsync(42)).ShouldBe(before);
        observation.LinksVerifiedAt.ShouldNotBeNull();
        (await repository.GetByIdAsync(99)).ShouldBeNull();
        _fixture.Transport.RequestedItems.ShouldBe([42]);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task SamePrincipalCredentialRenewalKeepsCapturedNativeGenerationUsable()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var module = _fixture.CreateModule(runtime);
        var initial = Success(await module.ReadDetailAsync(new(42, Sync: true)));
        await _fixture.Bindings.RegisterPatIdentityAsync("actor", NativeBrowserFixture.Organization, NativeBrowserFixture.RenewedPat);
        var renewed = Success(await module.ReadDetailAsync(new(42, Sync: true, Captured: initial.Capture)));
        renewed.Title.ShouldBe("Renewed selected item");
        renewed.Capture.StorageGeneration.ShouldBe(initial.Capture.StorageGeneration);
        _fixture.Transport.RequestedItems.ShouldBe([42, 42]);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task RealDefaultRevisionChangeInvalidatesCaptureEvenWhenEffectiveBindingIdsAreUnchanged()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Private cache"));
        var module = _fixture.CreateModule(runtime);
        var initial = Success(await module.ReadDetailAsync(new(42)));
        await using var database = new SqliteConnection($"Data Source={_fixture.Bindings.RegistryPath}");
        await database.OpenAsync();
        using var change = database.CreateCommand();
        change.CommandText = "UPDATE connection_defaults SET revision=revision+1 WHERE connection_ref=$key;";
        change.Parameters.AddWithValue("$key", _fixture.ActorBinding.ConnectionRef);
        (await change.ExecuteNonQueryAsync()).ShouldBe(1);
        Failure(await module.ReadDetailAsync(new(42, Captured: initial.Capture)), BrowserReadFailureKind.AuthorityInvalidated);
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
    }

    [HostMigrationFact]
    public async Task UnfinishedNativeTransitionRefusesCachedReadUntilExactRecoveryAndNewGenerationReconnect()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Old private generation"));
        var module = _fixture.CreateModule(runtime);
        var initial = Success(await module.ReadDetailAsync(new(42)));
        using var interrupted = new ConnectionBindingTransitionService(_fixture.Home, _fixture.Bindings, checkpoint =>
        {
            if (checkpoint == "central-committed") throw new IOException("Interrupted native transition");
        });
        var preview = await interrupted.PreviewAsync(_fixture.Configuration, _fixture.CurrentPaths(), _fixture.SiblingBinding.BindingId);
        preview.CanApply.ShouldBeTrue(string.Join("\n", preview.Blockers));
        await Should.ThrowAsync<IOException>(() => interrupted.ApplyAsync(_fixture.Configuration, _fixture.CurrentPaths(),
            _fixture.SiblingBinding.BindingId, preview.Digest));
        Failure(await module.ReadDetailAsync(new(42, Captured: initial.Capture)), BrowserReadFailureKind.AuthorityInvalidated);
        var recovered = await _fixture.Transitions.ApplyAsync(_fixture.Configuration, _fixture.CurrentPaths(),
            _fixture.SiblingBinding.BindingId, preview.Digest);
        recovered.CanApply.ShouldBeTrue(string.Join("\n", recovered.Blockers));
        var reconnected = await _fixture.CreateRuntimeAsync();
        (await reconnected.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42)).ShouldBeNull();
        var newModule = _fixture.CreateModule(reconnected);
        Failure(await newModule.ReadDetailAsync(new(42, Captured: initial.Capture)), BrowserReadFailureKind.AuthorityInvalidated);
        Failure(await newModule.ReadDetailAsync(new(42)), BrowserReadFailureKind.Unavailable);
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
    }

    [HostMigrationFact]
    public async Task ReplacedNativeMirrorAdmissionDuringReadCannotReturnOldGenerationContent()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Old generation content"));
        var module = _fixture.CreateModule(runtime);
        var initial = Success(await module.ReadDetailAsync(new(42)));
        var markerPath = Path.Combine(_fixture.Paths.TwigDir, "cache", MirrorAdmission.MarkerFile);
        var originalBytes = await File.ReadAllBytesAsync(markerPath);
        var marker = MirrorAdmission.ReadDocument(markerPath);
        var barrier = new MetadataBarrier(runtime.GetRequiredService<IFieldDefinitionStore>());
        var read = _fixture.CreateModule(runtime, barrier.Store).ReadDetailAsync(new(42, Captured: initial.Capture));
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Simulate an out-of-band replacement, which native admission must fail closed
            // even though this reader excludes supported management transitions.
            var replaced = marker with { Generation = Guid.NewGuid().ToString("N") };
            await File.WriteAllTextAsync(markerPath, System.Text.Json.JsonSerializer.Serialize(replaced,
                Twig.Infrastructure.Serialization.TwigJsonContext.Default.MirrorAdmissionDocument));
            barrier.Release.TrySetResult();
            Failure(await read, BrowserReadFailureKind.AuthorityInvalidated);
        }
        finally
        {
            barrier.Release.TrySetResult();
            await read;
            await File.WriteAllBytesAsync(markerPath, originalBytes);
        }
        (await repository.GetByIdAsync(42))!.Title.ShouldBe("Old generation content");
        _fixture.Transport.RequestedItems.ShouldBeEmpty();
        await _fixture.SwitchAsync(_fixture.SiblingBinding.BindingId);
    }

    private static BrowserDetailObservation Success(BrowserDetailResult result)
    {
        result.Failure.ShouldBeNull();
        result.Observation.ShouldNotBeNull();
        return result.Observation;
    }

    private static void Failure(BrowserDetailResult result, BrowserReadFailureKind kind)
    {
        result.Observation.ShouldBeNull();
        result.Failure.ShouldNotBeNull().Kind.ShouldBe(kind);
    }

    private sealed class MetadataBarrier
    {
        internal IFieldDefinitionStore Store { get; } = Substitute.For<IFieldDefinitionStore>();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal MetadataBarrier(IFieldDefinitionStore actual)
        {
            Store.GetAllAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            {
                var definitions = await actual.GetAllAsync(call.ArgAt<CancellationToken>(0));
                Entered.TrySetResult();
                // This scheduling seam intentionally ignores cancellation to test late-result rejection.
                await Release.Task;
                return definitions;
            });
        }
    }
}
