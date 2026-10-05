using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Claims;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.Persistence;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Spec #1103 switch/cold-cache/lifetime contracts through native management,
/// real migrated stores, command reads and a principal-private HTTP authority.
/// Verified journal history is produced only by authorized lifecycle apply.
/// </summary>
[Collection("ConsoleRedirect")]
[Trait("Category", "HostInventory")]
public sealed partial class ConnectionBindingTransitionConsumerTests : IAsyncLifetime
{
    private const string Organization = "fixture";
    private const string Project = "Work";
    private const string Actor = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Sibling = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string ActorPat = "fixture-actor-pat-v1";
    private const string RenewedActorPat = "fixture-actor-pat-v2";
    private const string SiblingPat = "fixture-sibling-pat";

    private readonly string _temp = CanonicalTempRoot.Create("twig-binding-transition-");
    private readonly PrincipalTransport _transport = new();
    private readonly FixtureRefresher _refresher = new();
    private readonly List<ServiceProvider> _runtimes = [];
    private HttpClient _http = null!;
    private ConnectionBindingService _bindings = null!;
    private ConnectionMigrationService _migration = null!;
    private ConnectionBindingTransitionService _transitions = null!;
    private TwigConfiguration _configuration = null!;
    private TwigPaths _paths = null!;
    private IdentityBinding _actorBinding = null!;
    private IdentityBinding _siblingBinding = null!;
    private string Home => Path.Combine(_temp, "home");

    public async Task InitializeAsync()
    {
        var root = Path.Combine(_temp, "checkout");
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        _configuration = new TwigConfiguration { Organization = Organization, Project = Project };
        _configuration.Display.Hints = false;
        _paths = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), _configuration, root);
        _paths = new TwigPaths(_paths.TwigDir, _paths.ConfigPath, _paths.DbPath, root, Path.Combine(Home, "display.json"));
        await _configuration.SaveSplitAsync(_paths);
        using (var attachment = new WorktreeLocalAttachmentStore(_paths, _configuration, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        using (var registry = OpenRegistry())
        {
            var anchor = WorktreeAnchorDetector.Detect(root)!.Value;
            var connection = ConnectionRefResolver.Compute(_configuration);
            (await registry.UpsertConnectionAsync(connection, Organization, Project, null)).IsSuccess.ShouldBeTrue();
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, root)).IsSuccess.ShouldBeTrue();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.DbPath)!);
        using (var legacy = new SqliteCacheStore($"Data Source={_paths.DbPath}"))
        {
            var repository = new SqliteWorkItemRepository(legacy, new WorkItemMapper());
            var old = new WorkItem { Id = 99, Type = WorkItemType.Task, Title = "Legacy private cache" };
            old.MarkSynced(1);
            await repository.SaveAsync(old);
        }
        _http = new HttpClient(_transport);
        _bindings = new ConnectionBindingService(Home, _refresher, patHttpClient: _http);
        await _bindings.RegisterPatIdentityAsync("actor", Organization, ActorPat);
        await _bindings.RegisterPatIdentityAsync("sibling", Organization, SiblingPat);
        _actorBinding = await _bindings.CreateBindingAsync(Organization, Project, "actor", makeDefault: true);
        _siblingBinding = await _bindings.CreateBindingAsync(Organization, Project, "sibling", makeDefault: false);
        _migration = new ConnectionMigrationService(Home, _bindings);
        var preview = await _migration.PreviewAsync(_configuration, _paths, "actor", null);
        preview.CanApply.ShouldBeTrue(string.Join("\n", preview.Blockers));
        (await _migration.ApplyAsync(_configuration, _paths, "actor", null, preview.Digest)).State.ShouldBe("active");
        _paths = CurrentPaths();
        _transitions = new ConnectionBindingTransitionService(Home, _bindings);
    }

    public Task DisposeAsync()
    {
        _transport.ReleaseRead();
        foreach (var runtime in _runtimes) runtime.Dispose();
        _transitions.Dispose();
        _migration.Dispose();
        _bindings.Dispose();
        _http.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    [HostMigrationFact]
    public async Task PinBeatsDefaultAndUnpinRestoresDefaultWithoutReturningEitherFormerActorsPrivateCache()
    {
        var original = await CreateRuntimeAsync();
        var oldRepository = original.GetRequiredService<IWorkItemRepository>();
        var first = await ShowAsync(original, 42, refresh: true);
        AssertTitle(first, "Actor-only release plan");
        var staleItem = (await oldRepository.GetByIdAsync(42))!;
        var portable = await File.ReadAllBytesAsync(_paths.RepoConfigPath);
        var pinPreview = await InvokePinAsync(original, _siblingBinding.BindingId, remove: false);
        pinPreview.Exit.ShouldBe(0, pinPreview.Error);
        using var previewJson = JsonDocument.Parse(pinPreview.Output);
        previewJson.RootElement.GetProperty("canApply").GetBoolean().ShouldBeTrue();
        var confirmed = previewJson.RootElement.GetProperty("digest").GetString()!;
        var applied = await InvokePinAsync(original, _siblingBinding.BindingId, remove: false, confirm: confirmed);
        applied.Exit.ShouldBe(0, applied.Error);

        var requestCount = _transport.WorkRequests;
        await Should.ThrowAsync<InvalidOperationException>(() => oldRepository.GetByIdAsync(42));
        await Should.ThrowAsync<InvalidOperationException>(() => oldRepository.SaveAsync(staleItem));
        await Should.ThrowAsync<InvalidOperationException>(() => original.GetRequiredService<IAdoWorkItemService>().FetchAsync(42));
        _transport.WorkRequests.ShouldBe(requestCount, "a stale live runtime must stop before work HTTP, not silently replace its provider");

        var reconnected = await CreateRuntimeAsync();
        var selected = await _bindings.ResolveAsync(_configuration, CurrentPaths());
        selected.Binding.BindingId.ShouldBe(_siblingBinding.BindingId);
        selected.SelectionSource.ShouldBe("checkout-binding-pin");
        (await reconnected.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42)).ShouldBeNull();
        var denied = await ShowAsync(reconnected, 42, refresh: true);
        denied.Exit.ShouldBe(1, denied.Error);
        denied.Output.ShouldNotContain("Actor-only release plan");
        (await reconnected.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42)).ShouldBeNull();
        AssertTitle(await ShowAsync(reconnected, 73, refresh: true), "Sibling-only release plan");
        using (var registry = OpenRegistry())
            (await registry.FindDefaultBindingAsync(ConnectionRefResolver.Compute(_configuration))).Value!.BindingId.ShouldBe(_actorBinding.BindingId);

        var unpinPreview = await InvokePinAsync(reconnected, null, remove: true);
        unpinPreview.Exit.ShouldBe(0, unpinPreview.Error);
        using var unpinJson = JsonDocument.Parse(unpinPreview.Output);
        (await InvokePinAsync(reconnected, null, remove: true, confirm: unpinJson.RootElement.GetProperty("digest").GetString())).Exit.ShouldBe(0);
        var restored = await CreateRuntimeAsync();
        var defaultSelection = await _bindings.ResolveAsync(_configuration, CurrentPaths());
        defaultSelection.Binding.BindingId.ShouldBe(_actorBinding.BindingId);
        defaultSelection.SelectionSource.ShouldBe("connection-default-binding");
        using (var attachment = new WorktreeLocalAttachmentStore(CurrentPaths(), _configuration, TimeProvider.System))
            (await attachment.ReadWithRevisionAsync()).Value.Attachment.BindingPin.ShouldBeNull();
        (await restored.GetRequiredService<IWorkItemRepository>().GetByIdAsync(73)).ShouldBeNull();
        var siblingDenied = await ShowAsync(restored, 73, refresh: true);
        siblingDenied.Exit.ShouldBe(1);
        siblingDenied.Output.ShouldNotContain("Sibling-only release plan");
        AssertTitle(await ShowAsync(restored, 42, refresh: true), "Actor-only release plan");
        (await File.ReadAllBytesAsync(_paths.RepoConfigPath)).ShouldBe(portable);
        _transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task RepeatingTheSamePinWithPendingWorkPreservesItsAdmittedRuntimeAndReadGeneration()
    {
        await ApplyPinAsync(_siblingBinding.BindingId);
        var runtime = await CreateRuntimeAsync();
        AssertTitle(await ShowAsync(runtime, 73, refresh: true), "Sibling-only release plan");
        var before = await _bindings.ResolveAsync(_configuration, CurrentPaths());
        var pending = runtime.GetRequiredService<IPendingChangeStore>();
        await pending.AddChangeAsync(73, "note", null, null, "Sibling unfinished note");
        var preview = await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeTrue(string.Join("\n", preview.Blockers));
        var repeat = await _transitions.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest);
        repeat.CanApply.ShouldBeTrue(string.Join("\n", repeat.Blockers));
        var after = await _bindings.ResolveAsync(_configuration, CurrentPaths());
        after.StorageGeneration.ShouldBe(before.StorageGeneration);
        after.Operation.AttachmentRevision.ShouldBe(before.Operation.AttachmentRevision);
        var requests = _transport.WorkRequests;
        AssertTitle(await ShowAsync(runtime, 73, refresh: false), "Sibling-only release plan");
        (await pending.GetChangesAsync(73)).ShouldHaveSingleItem().NewValue.ShouldBe("Sibling unfinished note");
        _transport.WorkRequests.ShouldBe(requests);
        _transport.Mutations.ShouldBe(0);
    }

    [HostMigrationTheory]
    [InlineData("field")]
    [InlineData("note")]
    [InlineData("seed")]
    [InlineData("open-intent")]
    [InlineData("planned")]
    [InlineData("confirmed")]
    [InlineData("applying")]
    [InlineData("applied")]
    [InlineData("indeterminate")]
    public async Task EveryDurableUnfinishedCategoryRefusesTransitionWithoutPublishingDiscardingOrChangingSelection(string category)
    {
        var runtime = await CreateRuntimeAsync();
        AssertTitle(await ShowAsync(runtime, 42, refresh: true), "Actor-only release plan");
        var pending = runtime.GetRequiredService<IPendingChangeStore>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        string? digest = null;
        StagedIdentity? intent = null;
        int? alias = null;
        if (category is "field" or "note")
            await pending.AddChangeAsync(42, category, category == "field" ? "System.Title" : null,
                category == "field" ? "Actor-only release plan" : null, "Unfinished actor work");
        else if (category == "seed")
            alias = (await StageSeedAsync(runtime, "Unpublished actor draft")).Id;
        else if (category == "open-intent")
        {
            intent = StagedIdentity.New();
            await runtime.GetRequiredService<IPublishIntentRepository>().RecordIntentAsync(intent.Value, "Unacknowledged create", "Task");
        }
        else
        {
            var proposal = await FieldProposalAsync("unfinished", "unfinished-op", 42, 1, "Unpublished proposal title");
            digest = (await runtime.GetRequiredService<IPlanLifecycleService>().PreviewAsync(proposal)).Digest!;
            if (category == "confirmed")
                await journal.ConfirmAsync(digest, DateTimeOffset.UtcNow);
            else if (category is "applying" or "applied")
            {
                (await journal.TryTransitionOperationAsync(digest, "unfinished-op", PlanOperationState.Planned,
                    PlanOperationState.Applying, DateTimeOffset.UtcNow)).ShouldBeTrue();
                if (category == "applied")
                    (await journal.TryTransitionOperationAsync(digest, "unfinished-op", PlanOperationState.Applying,
                        PlanOperationState.Applied, DateTimeOffset.UtcNow)).ShouldBeTrue();
            }
            else if (category == "indeterminate")
                await journal.SaveOperationErrorAsync(digest, "unfinished-op", "Native outcome not acknowledged",
                    PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);
        }
        var beforeAttachment = await File.ReadAllBytesAsync(AttachmentPath());
        var beforePolicy = await File.ReadAllBytesAsync(_paths.RepoConfigPath);
        var beforeJournal = digest is null ? null : await journal.GetAsync(digest);
        var beforeRequests = _transport.WorkRequests;
        var selection = await _bindings.ResolveAsync(_configuration, CurrentPaths());
        var preview = await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeFalse("each unfinished category independently blocks an identity change");
        preview.Blockers.ShouldNotBeEmpty();
        var refused = await _transitions.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest);
        refused.CanApply.ShouldBeFalse();
        (await File.ReadAllBytesAsync(AttachmentPath())).ShouldBe(beforeAttachment);
        (await File.ReadAllBytesAsync(_paths.RepoConfigPath)).ShouldBe(beforePolicy);
        (await _bindings.ResolveAsync(_configuration, CurrentPaths())).ShouldBe(selection);
        _transport.WorkRequests.ShouldBe(beforeRequests);
        _transport.Mutations.ShouldBe(0, "transition refusal is never permission to publish or discard unfinished work");
        if (category is "field" or "note")
            (await pending.GetChangesAsync(42)).ShouldHaveSingleItem().NewValue.ShouldBe("Unfinished actor work");
        if (alias is not null)
            (await runtime.GetRequiredService<IWorkItemRepository>().GetSeedsAsync()).ShouldHaveSingleItem().Id.ShouldBe(alias.Value);
        if (intent is not null)
            (await runtime.GetRequiredService<IPublishIntentRepository>().GetIntentAsync(intent.Value))!.IsOpen.ShouldBeTrue();
        if (digest is not null)
        {
            var retained = (await journal.GetAsync(digest))!;
            retained.State.ShouldBe(beforeJournal!.State);
            retained.Operations.Single().State.ShouldBe(beforeJournal.Operations.Single().State);
            retained.CanonicalJson.ShouldBe(beforeJournal.CanonicalJson);
            File.Exists(retained.SourcePath).ShouldBeTrue();
        }
        (await runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Actor-only release plan");
    }

    [HostMigrationFact]
    public async Task ANewAuthorizedVerifiedDigestCannotHideAnOlderUnacknowledgedOutcomeDuringPinning()
    {
        var runtime = await CreateRuntimeAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var olderFile = await FieldProposalAsync("older", "older-op", 42, 1, "Unknown older outcome");
        var olderDigest = (await lifecycle.PreviewAsync(olderFile)).Digest!;
        await journal.SaveOperationErrorAsync(olderDigest, "older-op", "Unacknowledged earlier call", PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);
        var newerFile = await FieldProposalAsync("newer", "newer-op", 44, 1, "Verified current title");
        var newerDigest = (await lifecycle.PreviewAsync(newerFile)).Digest!;
        var verified = await lifecycle.ApplyAsync(newerFile, newerDigest, Authorize(newerDigest));
        verified.Failed.ShouldBeFalse(verified.Error);
        verified.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        var mutations = _transport.Mutations;
        var requests = _transport.WorkRequests;
        var attachment = await File.ReadAllBytesAsync(AttachmentPath());
        var preview = await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeFalse();
        (await _transitions.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest)).CanApply.ShouldBeFalse();
        (await journal.GetUnresolvedAsync()).ShouldHaveSingleItem().Digest.ShouldBe(olderDigest);
        (await journal.GetAsync(olderDigest))!.Operations.Single().State.ShouldBe(PlanOperationState.Indeterminate);
        (await journal.GetAsync(newerDigest))!.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        (await File.ReadAllBytesAsync(AttachmentPath())).ShouldBe(attachment);
        _transport.Mutations.ShouldBe(mutations);
        _transport.WorkRequests.ShouldBe(requests);
    }

    [HostMigrationFact]
    public async Task ActiveNativeHolderMustBeReleasedByItsOrdinaryLifecycleBeforeAnotherActorCanPin()
    {
        var runtime = await CreateRuntimeAsync();
        var attachment = runtime.GetRequiredService<IPrimaryScopeAttachmentStore>();
        var initial = (await attachment.ReadWithRevisionAsync()).Value;
        (await attachment.WriteAsync(initial.Attachment.WithPrimaryScope(Scope(42)), initial.Revision)).IsSuccess.ShouldBeTrue();
        var anchor = WorktreeAnchorDetector.Detect(_paths.StartDir!)!.Value;
        var claims = runtime.GetRequiredService<ILocalClaimService>();
        var projection = runtime.GetRequiredService<IAdoClaimProjection>();
        var minted = (await claims.MintAsync(new MintClaimInput(ConnectionRefResolver.Compute(_configuration),
            PrimaryScopeKinds.AdoWorkItem, "42", WorktreeFingerprintProvider.CanonicalJson(anchor),
            "", "Alex Reader", null, null, projection))).ShouldBeOfType<ClaimMintOutcome.Succeeded>();
        var originalAttachment = await File.ReadAllBytesAsync(AttachmentPath());
        var rowBefore = (await runtime.GetRequiredService<ISystemWorktreeRegistry>().FindClaimAsync(minted.Claim.ClaimId)).Value;
        var mutations = _transport.Mutations;
        var preview = await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeFalse();
        (await _transitions.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest)).CanApply.ShouldBeFalse();
        (await runtime.GetRequiredService<ISystemWorktreeRegistry>().FindClaimAsync(minted.Claim.ClaimId)).Value.ShouldBe(rowBefore);
        (await File.ReadAllBytesAsync(AttachmentPath())).ShouldBe(originalAttachment);
        _transport.Mutations.ShouldBe(mutations, "pinning must not release, remint or publish a holder implicitly");
        (await claims.ReleaseAsync(new ReleaseClaimInput(minted.Claim.ClaimId, projection))).ShouldBeOfType<ClaimReleaseOutcome.Succeeded>();
        await ApplyPinAsync(_siblingBinding.BindingId);
        var reconnected = await CreateRuntimeAsync();
        AssertTitle(await ShowAsync(reconnected, 73, refresh: true), "Sibling-only release plan");
        var preserved = (await reconnected.GetRequiredService<IPrimaryScopeAttachmentStore>().ReadWithRevisionAsync()).Value.Attachment;
        preserved.PrimaryScope!.Value.WorkItemId.ShouldBe(42);
        preserved.ActiveClaim.ShouldBeNull();
        (await reconnected.GetRequiredService<ISystemWorktreeRegistry>().FindClaimAsync(minted.Claim.ClaimId)).Value.ShouldNotBeNull();
    }

    [HostMigrationTheory]
    [InlineData("attachment")]
    [InlineData("default")]
    [InlineData("binding")]
    public async Task ConfirmedPreviewCannotOverwriteAConcurrentAttachmentOrCentralRevision(string changedStore)
    {
        var runtime = await CreateRuntimeAsync();
        AssertTitle(await ShowAsync(runtime, 42, refresh: true), "Actor-only release plan");
        var preview = await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeTrue(string.Join("\n", preview.Blockers));
        if (changedStore == "attachment")
        {
            var attachment = runtime.GetRequiredService<IPrimaryScopeAttachmentStore>();
            var versioned = (await attachment.ReadWithRevisionAsync()).Value;
            (await attachment.WriteAsync(versioned.Attachment.WithPrimaryScope(Scope(42)), versioned.Revision)).IsSuccess.ShouldBeTrue();
        }
        else
        {
            // Simulate a competing registry writer at the durable CAS boundary, not a fake manager response.
            using var database = new SqliteConnection($"Data Source={Path.Combine(Home, "system.db")};Pooling=False");
            await database.OpenAsync();
            using var update = database.CreateCommand();
            update.CommandText = changedStore == "default"
                ? "UPDATE connection_defaults SET revision=revision+1 WHERE connection_ref=$key;"
                : "UPDATE connection_bindings SET revision=revision+1 WHERE binding_id=$key;";
            update.Parameters.AddWithValue("$key", changedStore == "default" ? ConnectionRefResolver.Compute(_configuration) : _siblingBinding.BindingId);
            (await update.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        var attachmentAfterConcurrentChange = await File.ReadAllBytesAsync(AttachmentPath());
        var marker = await File.ReadAllBytesAsync(MarkerPath());
        var requests = _transport.WorkRequests;
        await Should.ThrowAsync<InvalidOperationException>(() => _transitions.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest));
        (await File.ReadAllBytesAsync(AttachmentPath())).ShouldBe(attachmentAfterConcurrentChange);
        (await File.ReadAllBytesAsync(MarkerPath())).ShouldBe(marker);
        _transport.WorkRequests.ShouldBe(requests);
        _transport.Mutations.ShouldBe(0);
        var fresh = await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        fresh.Digest.ShouldNotBe(preview.Digest, "a saved confirmation cannot follow a revised selection/CAS snapshot");
    }

    [HostMigrationFact]
    public async Task ActualInFlightPrivateReadAndCacheFillExcludeTheManagementTransitionUntilTheySettle()
    {
        var runtime = await CreateRuntimeAsync();
        var preview = await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeTrue(string.Join("\n", preview.Blockers));
        var attachment = await File.ReadAllBytesAsync(AttachmentPath());
        _transport.HoldNextRead();
        var read = ShowAsync(runtime, 42, refresh: true);
        try
        {
            await _transport.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            read.IsCompleted.ShouldBeFalse("the response really is in flight while management attempts its exclusive gate");
            var active = await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
            active.CanApply.ShouldBeFalse();
            active.Blockers.ShouldContain(x => x.Contains("binding-operation-active", StringComparison.Ordinal));
            var refused = await _transitions.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest);
            refused.CanApply.ShouldBeFalse();
            refused.Blockers.ShouldContain(x => x.Contains("binding-operation-active", StringComparison.Ordinal));
            (await File.ReadAllBytesAsync(AttachmentPath())).ShouldBe(attachment);
            _transport.Mutations.ShouldBe(0);
        }
        finally
        {
            _transport.ReleaseRead();
            await read; // Restore process-wide Console writers even when a race assertion fails.
        }
        AssertTitle(await read, "Actor-only release plan");
        (await runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Actor-only release plan");
        await ApplyPinAsync(_siblingBinding.BindingId);
        var reconnected = await CreateRuntimeAsync();
        (await reconnected.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42)).ShouldBeNull();
        (await ShowAsync(reconnected, 42, refresh: true)).Exit.ShouldBe(1);
        AssertTitle(await ShowAsync(reconnected, 73, refresh: true), "Sibling-only release plan");
    }

    [HostMigrationFact]
    public async Task CompletedPrivateHttpResponseStillExcludesTransitionWhileItsRealCacheSaveIsPaused()
    {
        var runtime = await CreateRuntimeAsync();
        var actual = runtime.GetRequiredService<IWorkItemRepository>();
        var preview = await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeTrue(string.Join("\n", preview.Blockers));
        var attachmentBytes = await File.ReadAllBytesAsync(AttachmentPath());
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A scheduling decorator only: every value, save and lease comes from the admitted store.
        var scheduled = Substitute.For<IWorkItemRepository>();
        scheduled.AcquireOperation().Returns(_ => actual.AcquireOperation());
        scheduled.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => actual.GetByIdAsync(call.ArgAt<int>(0), call.ArgAt<CancellationToken>(1)));
        scheduled.SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var item = call.ArgAt<WorkItem>(0);
            var ct = call.ArgAt<CancellationToken>(1);
            saveEntered.TrySetResult();
            await releaseSave.Task.WaitAsync(ct);
            await actual.SaveAsync(item, ct);
        });
        var fetcher = new WorkItemFetcher(scheduled, runtime.GetRequiredService<IAdoWorkItemService>());
        var fill = fetcher.FetchWithFallbackAsync(42, CancellationToken.None);
        try
        {
            await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _transport.WorkRequests.ShouldBe(1, "the real principal-private HTTP response has completed before the delayed real save");
            fill.IsCompleted.ShouldBeFalse();
            (await actual.GetByIdAsync(42)).ShouldBeNull();
            var refused = await _transitions.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest);
            refused.CanApply.ShouldBeFalse("a cache-fill lease must outlive HTTP response disposal and the best-effort save gap");
            refused.Blockers.ShouldContain(x => x.Contains("binding-operation-active", StringComparison.Ordinal));
            (await File.ReadAllBytesAsync(AttachmentPath())).ShouldBe(attachmentBytes);
            _transport.Mutations.ShouldBe(0);
        }
        finally
        {
            releaseSave.TrySetResult();
            await fill;
        }
        var settled = await fill;
        settled.Error.ShouldBeNull();
        settled.Item.ShouldNotBeNull();
        settled.Item!.Title.ShouldBe("Actor-only release plan");
        (await actual.GetByIdAsync(42))!.Title.ShouldBe("Actor-only release plan");
        await ApplyPinAsync(_siblingBinding.BindingId);
        var reconnected = await CreateRuntimeAsync();
        (await reconnected.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42)).ShouldBeNull();
        var denied = await ShowAsync(reconnected, 42, refresh: true);
        denied.Exit.ShouldBe(1);
        denied.Output.ShouldNotContain("Actor-only release plan");
        AssertTitle(await ShowAsync(reconnected, 73, refresh: true), "Sibling-only release plan");
        _transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task CentralCommitCrashFencesEveryReadAndRecoveryResumesTheOriginalNativeIntentWithoutLosingAuthorityOrHistory()
    {
        var runtime = await CreateRuntimeAsync();
        var lifecycle = runtime.GetRequiredService<IPlanLifecycleService>();
        var journal = runtime.GetRequiredService<IPlanJournalRepository>();
        var verifiedFile = await FieldProposalAsync("historical-verified", "verified-op", 44, 1, "Verified actor history");
        var verifiedDigest = (await lifecycle.PreviewAsync(verifiedFile)).Digest!;
        var verified = await lifecycle.ApplyAsync(verifiedFile, verifiedDigest, Authorize(verifiedDigest));
        verified.Failed.ShouldBeFalse(verified.Error);
        verified.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        var retiredFile = await FieldProposalAsync("historical-retired", "retired-op", 42, 1, "Never applied historical intent");
        var retiredDigest = (await lifecycle.PreviewAsync(retiredFile)).Digest!;
        await journal.ConfirmAsync(retiredDigest, DateTimeOffset.UtcNow);
        (await lifecycle.ReconcileAsync(retiredFile, retiredDigest, "retired-op", PlanOutcomeKind.Retired, Authorize(retiredDigest))).Settled.ShouldBeTrue();
        var retiredBefore = (await journal.GetAsync(retiredDigest))!;
        var seed = await StageSeedAsync(runtime, "Published actor history");
        var descriptor = (await lifecycle.DescribeSeedAsync(seed.Id))!;
        var seedFile = await SeedProposalAsync(descriptor);
        var seedDigest = (await lifecycle.PreviewAsync(seedFile)).Digest!;
        var published = await lifecycle.ApplyAsync(seedFile, seedDigest, Authorize(seedDigest));
        published.Failed.ShouldBeFalse(published.Error);
        published.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        var intentBefore = (await runtime.GetRequiredService<IPublishIntentRepository>().GetIntentAsync(descriptor.Identity))!;
        intentBefore.IsOpen.ShouldBeFalse();
        AssertTitle(await ShowAsync(runtime, 42, refresh: true), "Actor-only release plan");
        var staleItem = (await runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!;
        var attachment = runtime.GetRequiredService<IPrimaryScopeAttachmentStore>();
        var versioned = (await attachment.ReadWithRevisionAsync()).Value;
        (await attachment.WriteAsync(versioned.Attachment.WithPrimaryScope(Scope(42)), versioned.Revision)).IsSuccess.ShouldBeTrue();
        var originalAttachment = (await attachment.ReadWithRevisionAsync()).Value;
        var attachmentBytes = await File.ReadAllBytesAsync(AttachmentPath());
        var policy = await File.ReadAllBytesAsync(_paths.RepoConfigPath);
        var historyBytes = await File.ReadAllBytesAsync(retiredFile);
        var credentials = await CredentialBytesAsync();
        var mutations = _transport.Mutations;
        using var crashing = new ConnectionBindingTransitionService(Home, _bindings, checkpoint =>
        {
            if (checkpoint == "central-committed") throw new IOException("Fixture interruption after native selection commit");
        });
        var preview = await crashing.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeTrue(string.Join("\n", preview.Blockers));
        await Should.ThrowAsync<IOException>(() => crashing.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest));
        var scopeWrite = await Should.ThrowAsync<InvalidOperationException>(() => attachment.WriteAsync(
            originalAttachment.Attachment.WithPrimaryScope(Scope(73)), originalAttachment.Revision));
        scopeWrite.Message.ShouldContain("binding-transition-incomplete");
        (await File.ReadAllBytesAsync(AttachmentPath())).ShouldBe(attachmentBytes,
            "an ordinary scope mutation must not overwrite the native transition's exact recoverable attachment snapshot");
        var requests = _transport.WorkRequests;
        await Should.ThrowAsync<InvalidOperationException>(() => _bindings.ResolveAsync(_configuration, CurrentPaths()));
        await Should.ThrowAsync<InvalidOperationException>(() => runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42));
        await Should.ThrowAsync<InvalidOperationException>(() => runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(staleItem));
        await Should.ThrowAsync<InvalidOperationException>(() => runtime.GetRequiredService<IAdoWorkItemService>().FetchAsync(42));
        await Should.ThrowAsync<InvalidOperationException>(() => ShowAsync(runtime, 42, refresh: false));
        _transport.WorkRequests.ShouldBe(requests);
        _transport.Mutations.ShouldBe(mutations);
        var conflicting = await _transitions.ApplyAsync(_configuration, CurrentPaths(), _actorBinding.BindingId, preview.Digest);
        conflicting.CanApply.ShouldBeFalse();
        conflicting.Blockers.ShouldContain(x => x.Contains("binding-transition-selector-conflict", StringComparison.Ordinal));
        await Should.ThrowAsync<InvalidOperationException>(() => _transitions.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, new string('0', 64)));
        var recovering = await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        recovering.Digest.ShouldBe(preview.Digest, "recovery is the exact original native intent, not another selection plan");
        var resumed = await _transitions.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest);
        resumed.CanApply.ShouldBeTrue(string.Join("\n", resumed.Blockers));
        var reconnected = await CreateRuntimeAsync();
        (await reconnected.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42)).ShouldBeNull();
        (await reconnected.GetRequiredService<IWorkItemRepository>().GetByIdAsync(intentBefore.PublishedId!.Value)).ShouldBeNull();
        AssertTitle(await ShowAsync(reconnected, 73, refresh: true), "Sibling-only release plan");
        (await ShowAsync(reconnected, 42, refresh: true)).Exit.ShouldBe(1);
        (await File.ReadAllBytesAsync(_paths.RepoConfigPath)).ShouldBe(policy);
        (await File.ReadAllBytesAsync(retiredFile)).ShouldBe(historyBytes);
        foreach (var credential in credentials)
            (await File.ReadAllBytesAsync(credential.Key)).ShouldBe(credential.Value);
        var preservedAttachment = (await reconnected.GetRequiredService<IPrimaryScopeAttachmentStore>().ReadWithRevisionAsync()).Value.Attachment;
        preservedAttachment.PrimaryScope!.Value.WorkItemId.ShouldBe(42);
        preservedAttachment.BindingPin.ShouldBe(_siblingBinding.BindingId);
        var preservedJournal = reconnected.GetRequiredService<IPlanJournalRepository>();
        (await preservedJournal.GetAsync(verifiedDigest))!.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        var retainedRetired = (await preservedJournal.GetAsync(retiredDigest))!;
        retainedRetired.State.ShouldBe(retiredBefore.State);
        retainedRetired.Operations.Single().State.ShouldBe(retiredBefore.Operations.Single().State);
        retainedRetired.CanonicalJson.ShouldBe(retiredBefore.CanonicalJson);
        (await preservedJournal.GetUnresolvedAsync()).ShouldBeEmpty();
        (await reconnected.GetRequiredService<IPublishIntentRepository>().GetIntentAsync(descriptor.Identity)).ShouldBe(intentBefore);
        // Preserved Confirmed history must stay retired at the native execution seam.
        var replay = await reconnected.GetRequiredService<IPlanLifecycleService>().ApplyAsync(retiredFile, retiredDigest, Authorize(retiredDigest));
        replay.Failed.ShouldBeTrue();
        _transport.Mutations.ShouldBe(mutations);
    }

    [HostMigrationFact]
    public async Task LostCompletionAcknowledgementReturnsTheOriginalReceiptWithoutResettingNewActorsReadCacheOrPendingWork()
    {
        using var interrupted = new ConnectionBindingTransitionService(Home, _bindings, checkpoint =>
        {
            if (checkpoint == "completed") throw new IOException("Fixture lost final completion acknowledgement");
        });
        var preview = await interrupted.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeTrue(string.Join("\n", preview.Blockers));
        await Should.ThrowAsync<IOException>(() => interrupted.ApplyAsync(
            _configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest));

        var reconnected = await CreateRuntimeAsync();
        AssertTitle(await ShowAsync(reconnected, 73, refresh: true), "Sibling-only release plan");
        var pending = reconnected.GetRequiredService<IPendingChangeStore>();
        await pending.AddChangeAsync(73, "note", null, null, "New actor work after completed transition");
        var admitted = await _bindings.ResolveAsync(_configuration, CurrentPaths());
        var attachmentBytes = await File.ReadAllBytesAsync(AttachmentPath());
        var admissionBytes = await File.ReadAllBytesAsync(MarkerPath());
        var requests = _transport.WorkRequests;

        var acknowledged = await _transitions.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest);
        acknowledged.CanApply.ShouldBeTrue(string.Join("\n", acknowledged.Blockers));
        acknowledged.State.ShouldBe("completed");
        acknowledged.Digest.ShouldBe(preview.Digest);
        var repeated = await interrupted.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest);
        repeated.State.ShouldBe("completed");
        repeated.Digest.ShouldBe(preview.Digest);
        (await _bindings.ResolveAsync(_configuration, CurrentPaths())).ShouldBe(admitted);
        (await File.ReadAllBytesAsync(AttachmentPath())).ShouldBe(attachmentBytes);
        (await File.ReadAllBytesAsync(MarkerPath())).ShouldBe(admissionBytes);
        AssertTitle(await ShowAsync(reconnected, 73, refresh: false), "Sibling-only release plan");
        (await pending.GetChangesAsync(73)).ShouldHaveSingleItem().NewValue.ShouldBe("New actor work after completed transition");
        (await reconnected.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42)).ShouldBeNull();
        _transport.WorkRequests.ShouldBe(requests, "a repeated completion acknowledgement must not rerun publication or read-cache refill");
        _transport.Mutations.ShouldBe(0);
    }

    [HostMigrationTheory]
    [InlineData("pat")]
    [InlineData("aad")]
    public async Task VerifiedSamePrincipalRenewalOnAnOpenMigratedRuntimeKeepsPendingWorkAndCachedReadsWithoutReconnect(string method)
    {
        if (method == "aad")
        {
            await _bindings.RegisterAadIdentityAsync("aad-actor", RefreshEntry("fixture-refresh-v1"));
            var aad = await _bindings.CreateBindingAsync(Organization, Project, "aad-actor", makeDefault: false);
            await ApplyPinAsync(aad.BindingId);
        }
        var runtime = await CreateRuntimeAsync();
        AssertTitle(await ShowAsync(runtime, 42, refresh: true), "Actor-only release plan");
        AssertTitle(await ShowAsync(runtime, 43, refresh: true), "Actor clean snapshot v1");
        var pending = runtime.GetRequiredService<IPendingChangeStore>();
        await pending.AddChangeAsync(42, "note", null, null, "Keep actor pending note");
        await pending.AddChangeAsync(42, "field", "System.Description", null, "Keep actor draft description");
        var before = await _bindings.ResolveAsync(_configuration, CurrentPaths());
        var siblingIdentity = (await _bindings.ListIdentitiesAsync()).Single(x => x.Name == "sibling");
        var siblingCredential = Path.Combine(Home, "credentials", siblingIdentity.CredentialRef + ".json");
        var siblingBytes = await File.ReadAllBytesAsync(siblingCredential);
        if (method == "pat")
            await _bindings.RegisterPatIdentityAsync("actor", Organization, RenewedActorPat);
        else
        {
            await _bindings.RegisterAadIdentityAsync("aad-actor", RefreshEntry("fixture-refresh-v2"));
            runtime.GetRequiredService<IAuthenticationProvider>().InvalidateToken();
        }
        var after = await _bindings.ResolveAsync(_configuration, CurrentPaths());
        after.Binding.ShouldBe(before.Binding);
        after.SelectionRevision.ShouldBe(before.SelectionRevision);
        after.StorageGeneration.ShouldBe(before.StorageGeneration);
        AssertTitle(await ShowAsync(runtime, 43, refresh: true), "Actor clean snapshot v2");
        var workRequests = _transport.WorkRequests;
        AssertTitle(await ShowAsync(runtime, 42, refresh: false), "Actor-only release plan");
        (await pending.GetChangesAsync(42)).Select(x => x.NewValue).ShouldBe(new[] { "Keep actor pending note", "Keep actor draft description" });
        (await File.ReadAllBytesAsync(siblingCredential)).ShouldBe(siblingBytes);
        _transport.WorkRequests.ShouldBe(workRequests, "cached reads remain available without an identity transition or reconnect");
        _transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task PinCommandRejectsMissingBindingBeforeChangingTheCheckoutOrCallingWorkHttp()
    {
        var runtime = await CreateRuntimeAsync();
        var attachment = await File.ReadAllBytesAsync(AttachmentPath());
        var result = await InvokePinAsync(runtime, null, remove: false);
        result.Exit.ShouldNotBe(0);
        (await File.ReadAllBytesAsync(AttachmentPath())).ShouldBe(attachment);
        _transport.WorkRequests.ShouldBe(0);
        _transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task LostCommentResponseSurvivesProviderDisposalAndPendingDiscardAndCannotBeReplayedOrSettledByMatchingText()
    {
        const string note = "Actor note accepted before its response was lost";
        var origin = await CreateRuntimeAsync();
        await origin.GetRequiredService<IPendingChangeStore>().AddChangeAsync(42, "note", null, null, note);
        _transport.LoseNextCommentResponse = true;
        await Should.ThrowAsync<Twig.Infrastructure.Ado.Exceptions.AdoOfflineException>(() => origin.GetRequiredService<IAdoWorkItemService>().AddCommentAsync(42, note));
        _transport.CommentCount.ShouldBe(1);
        _transport.Mutations.ShouldBe(1);
        origin.Dispose();
        _runtimes.Remove(origin);

        var reconnected = await CreateRuntimeAsync();
        using var reconciliation = new ConnectionRemoteWriteReconciliationService(Home, _bindings, _http);
        var unknown = (await reconciliation.InspectAsync(_configuration, CurrentPaths())).ShouldHaveSingleItem();
        unknown.State.ShouldBe("unknown");
        unknown.Request.EffectKind.ShouldBe("comment-add");
        unknown.Receipt.ShouldBeNull();
        unknown.Observations.ShouldBeEmpty();
        unknown.CanReconcile.ShouldBeFalse();
        var inspected = await InvokeWritesAsync(reconnected, reconciliation, reconcile: false);
        inspected.Exit.ShouldBe(0, inspected.Error);
        using (var json = JsonDocument.Parse(inspected.Output))
        {
            json.RootElement[0].GetProperty("intentId").GetString().ShouldBe(unknown.IntentId);
            json.RootElement[0].GetProperty("state").GetString().ShouldBe("unknown");
            AssertNoReceipt(json.RootElement[0]);
        }
        var pending = reconnected.GetRequiredService<IPendingChangeStore>();
        await pending.ClearChangesAsync(42);
        (await pending.GetChangesAsync(42)).ShouldBeEmpty();
        var beforeAttachment = await File.ReadAllBytesAsync(AttachmentPath());
        var blocked = await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        blocked.CanApply.ShouldBeFalse("disposing the physical operation and discarding a pending note do not settle its native remote outcome");
        (await _transitions.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, blocked.Digest)).CanApply.ShouldBeFalse();
        var beforeRequests = _transport.WorkRequests;
        var replay = await Should.ThrowAsync<InvalidOperationException>(() => reconnected.GetRequiredService<IAdoWorkItemService>().AddCommentAsync(42, note));
        replay.Message.ShouldContain("remote-write-outcome-unknown");
        _transport.WorkRequests.ShouldBe(beforeRequests, "the native semantic request fence must refuse before a duplicate POST");
        var authorizer = (await _bindings.ListIdentitiesAsync()).Single(x => x.Name == "actor").IdentityId;
        var refused = await InvokeWritesAsync(reconnected, reconciliation, reconcile: true,
            intent: unknown.IntentId, confirm: unknown.Digest, authorizer: authorizer);
        refused.Exit.ShouldBe(1, refused.Error);
        using (var json = JsonDocument.Parse(refused.Output))
        {
            json.RootElement.GetProperty("state").GetString().ShouldBe("unknown");
            AssertNoReceipt(json.RootElement);
            json.RootElement.GetProperty("blockers").EnumerateArray().ShouldContain(x =>
                x.GetString()!.Contains("remote-write-attribution-unavailable", StringComparison.Ordinal));
        }
        var retained = (await reconciliation.InspectAsync(_configuration, CurrentPaths())).ShouldHaveSingleItem();
        retained.IntentId.ShouldBe(unknown.IntentId);
        retained.Digest.ShouldBe(unknown.Digest);
        retained.Request.ShouldBe(unknown.Request);
        retained.Receipt.ShouldBeNull();
        retained.Observations.ShouldBeEmpty();
        (await File.ReadAllBytesAsync(AttachmentPath())).ShouldBe(beforeAttachment);
        _transport.WorkRequests.ShouldBe(beforeRequests, "unrevisioned matching text/actor is never readback evidence permitting settlement");
        _transport.Mutations.ShouldBe(1);
        _transport.CommentCount.ShouldBe(1);
    }

    [HostMigrationFact]
    public async Task ActualCommentAcknowledgementLeavesNativeHistorySettledAndPermitsACleanColdPin()
    {
        var runtime = await CreateRuntimeAsync();
        AssertTitle(await ShowAsync(runtime, 42, refresh: true), "Actor-only release plan");
        await runtime.GetRequiredService<IAdoWorkItemService>().AddCommentAsync(42, "Acknowledged actor note");
        using var reconciliation = new ConnectionRemoteWriteReconciliationService(Home, _bindings, _http);
        var acknowledged = (await reconciliation.InspectAsync(_configuration, CurrentPaths())).ShouldHaveSingleItem();
        acknowledged.State.ShouldBe("settled");
        acknowledged.Receipt.ShouldNotBeNull();
        acknowledged.Receipt!.Kind.ShouldBe("acknowledged");
        acknowledged.Observations.ShouldHaveSingleItem().Response.StatusCode.ShouldBe(200);
        _transport.CommentCount.ShouldBe(1);
        await ApplyPinAsync(_siblingBinding.BindingId);
        var reconnected = await CreateRuntimeAsync();
        (await reconnected.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42)).ShouldBeNull();
        AssertTitle(await ShowAsync(reconnected, 73, refresh: true), "Sibling-only release plan");
        var retained = (await reconciliation.InspectAsync(_configuration, CurrentPaths())).ShouldHaveSingleItem();
        retained.IntentId.ShouldBe(acknowledged.IntentId);
        retained.Receipt.ShouldBe(acknowledged.Receipt);
        _transport.Mutations.ShouldBe(1, "switching preserves the real acknowledgement without replaying the original POST");
    }

    [HostMigrationFact]
    public async Task LostCasPatchSettlesOnlyUnderOriginalActorWithItsImmutableFirstPostCasRevisionNotTheLaterCurrentTitle()
    {
        var origin = await CreateRuntimeAsync();
        var ado = origin.GetRequiredService<IAdoWorkItemService>();
        _transport.LoseNextPatchResponse = true;
        await Should.ThrowAsync<Twig.Infrastructure.Ado.Exceptions.AdoOfflineException>(() => ado.PatchAsync(42,
            [new FieldChange("System.Title", "Actor-only release plan", "Original exhausted CAS effect")], 1));
        origin.Dispose();
        _runtimes.Remove(origin);
        var reconnected = await CreateRuntimeAsync();
        using var reconciliation = new ConnectionRemoteWriteReconciliationService(Home, _bindings, _http);
        var unknown = (await reconciliation.InspectAsync(_configuration, CurrentPaths())).ShouldHaveSingleItem();
        unknown.State.ShouldBe("unknown");
        unknown.CanReconcile.ShouldBeTrue();
        unknown.Receipt.ShouldBeNull();
        unknown.Observations.ShouldBeEmpty();
        var blocked = await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        blocked.CanApply.ShouldBeFalse();

        // A genuinely later acknowledged write changes current state; only immutable revision 2 proves the earlier effect.
        (await reconnected.GetRequiredService<IAdoWorkItemService>().PatchAsync(42,
            [new FieldChange("System.Title", "Original exhausted CAS effect", "Later acknowledged actor title")], 2)).ShouldBe(3);
        var current = await reconnected.GetRequiredService<IAdoWorkItemService>().FetchAsync(42);
        current.Title.ShouldBe("Later acknowledged actor title");
        var siblingAuthorizer = (await _bindings.ListIdentitiesAsync()).Single(x => x.Name == "sibling").IdentityId;
        var wrongActor = await InvokeWritesAsync(reconnected, reconciliation, reconcile: true,
            intent: unknown.IntentId, confirm: unknown.Digest, authorizer: siblingAuthorizer);
        wrongActor.Exit.ShouldBe(1);
        using (var json = JsonDocument.Parse(wrongActor.Output))
            AssertNoReceipt(json.RootElement);
        var originalAuthorizer = (await _bindings.ListIdentitiesAsync()).Single(x => x.Name == "actor").IdentityId;
        var settled = await InvokeWritesAsync(reconnected, reconciliation, reconcile: true,
            intent: unknown.IntentId, confirm: unknown.Digest, authorizer: originalAuthorizer);
        settled.Exit.ShouldBe(0, settled.Error + settled.Output);
        using (var json = JsonDocument.Parse(settled.Output))
        {
            json.RootElement.GetProperty("digest").GetString().ShouldBe(unknown.Digest);
            json.RootElement.GetProperty("state").GetString().ShouldBe("settled");
            json.RootElement.GetProperty("receipt").GetProperty("kind").GetString().ShouldBe("exhausted-cas-readback");
        }
        var retained = (await reconciliation.InspectAsync(_configuration, CurrentPaths())).Single(x => x.IntentId == unknown.IntentId);
        retained.Request.ShouldBe(unknown.Request);
        retained.Observations.ShouldBeEmpty("readback adds an attributable receipt, not an invented acknowledgement to the lost HTTP response");
        retained.Receipt!.AuthorizerIdentity.ShouldBe(originalAuthorizer);
        await ApplyPinAsync(_siblingBinding.BindingId);
        var switched = await CreateRuntimeAsync();
        (await switched.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42)).ShouldBeNull();
        AssertTitle(await ShowAsync(switched, 73, refresh: true), "Sibling-only release plan");
        _transport.Mutations.ShouldBe(2, "native readback and transition never replay either PATCH");
    }

    [HostMigrationFact]
    public async Task LostSeedCreateSettlesOnlyAfterExactNativePublishRecoveryAndOriginalTaggedCreationRevisionReadback()
    {
        const string title = "Seed whose create response was lost";
        var origin = await CreateRuntimeAsync();
        var seed = await StageSeedAsync(origin, title);
        var identity = seed.StagedIdentity!.Value;
        _transport.LoseNextCreateResponse = true;
        await Should.ThrowAsync<Twig.Infrastructure.Ado.Exceptions.AdoOfflineException>(() =>
            origin.GetRequiredService<SeedPublishOrchestrator>().PublishAsync(seed.Id));
        _transport.CreateCount.ShouldBe(1);
        var publishIntent = (await origin.GetRequiredService<IPublishIntentRepository>().GetIntentAsync(identity))!;
        publishIntent.IsOpen.ShouldBeTrue();
        var correlation = new SeedPublishCorrelation(identity, publishIntent.RecordedAt);
        origin.Dispose();
        _runtimes.Remove(origin);

        var reconnected = await CreateRuntimeAsync();
        using var reconciliation = new ConnectionRemoteWriteReconciliationService(Home, _bindings, _http);
        var unknown = (await reconciliation.InspectAsync(_configuration, CurrentPaths())).ShouldHaveSingleItem();
        unknown.Request.SeedCorrelation.ShouldBe(correlation);
        unknown.Request.EffectKind.ShouldBe("workitem-create");
        unknown.Receipt.ShouldBeNull();
        var authorizer = (await _bindings.ListIdentitiesAsync()).Single(x => x.Name == "actor").IdentityId;
        var premature = await reconciliation.ReconcileAsync(_configuration, CurrentPaths(), unknown.IntentId,
            unknown.Digest, authorizer, "Native publication has not yet recovered");
        premature.Receipt.ShouldBeNull("server creation alone does not complete this exact native seed intent/map");
        (await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId)).CanApply.ShouldBeFalse();

        // A same-title/type/time candidate with only the legacy constant tag cannot adopt this seed.
        _transport.AddLegacySeedCandidate(title);
        _transport.HideCorrelatedSeedFromQueries = true;
        await Should.ThrowAsync<InvalidOperationException>(() =>
            reconnected.GetRequiredService<SeedPublishOrchestrator>().PublishAsync(seed.Id));
        (await reconnected.GetRequiredService<IPublishIdMapRepository>().GetNewIdAsync(identity)).ShouldBeNull();
        (await reconnected.GetRequiredService<IPublishIntentRepository>().GetIntentAsync(identity))!.IsOpen.ShouldBeTrue();
        _transport.CreateCount.ShouldBe(1, "unsupported constant-tag evidence cannot permit a second create");
        _transport.HideCorrelatedSeedFromQueries = false;

        var recovered = await reconnected.GetRequiredService<SeedPublishOrchestrator>().PublishAsync(seed.Id);
        recovered.Status.ShouldBe(SeedPublishStatus.Created, recovered.ErrorMessage);
        var completed = (await reconnected.GetRequiredService<IPublishIntentRepository>().GetIntentAsync(identity))!;
        completed.IsOpen.ShouldBeFalse();
        completed.RecordedAt.ShouldBe(publishIntent.RecordedAt);
        completed.PublishedId.ShouldBe(recovered.NewId);
        (await reconnected.GetRequiredService<IPublishIdMapRepository>().GetNewIdAsync(identity)).ShouldBe(recovered.NewId);
        (await reconnected.GetRequiredService<IWorkItemRepository>().GetSeedsAsync()).ShouldBeEmpty();
        _transport.CreateCount.ShouldBe(1, "native recovery finds the original exact server tag and finishes its durable mapping without create replay");
        (await _transitions.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId)).CanApply.ShouldBeFalse(
            "the completed seed ledger alone cannot silently settle a distinct native HTTP uncertainty record");

        var settled = await InvokeWritesAsync(reconnected, reconciliation, reconcile: true,
            intent: unknown.IntentId, confirm: unknown.Digest, authorizer: authorizer);
        settled.Exit.ShouldBe(0, settled.Error + settled.Output);
        var receipt = (await reconciliation.InspectAsync(_configuration, CurrentPaths()))
            .Single(x => x.IntentId == unknown.IntentId).Receipt!;
        receipt.Kind.ShouldBe("native-published-seed-readback");
        receipt.RequestDigest.ShouldBe(unknown.Digest);
        using (var evidence = JsonDocument.Parse(receipt.EvidenceJson))
            evidence.RootElement.GetProperty("correlationTag").GetString().ShouldBe(correlation.Tag);
        await ApplyPinAsync(_siblingBinding.BindingId);
        var switched = await CreateRuntimeAsync();
        (await switched.GetRequiredService<IWorkItemRepository>().GetByIdAsync(recovered.NewId)).ShouldBeNull();
        (await switched.GetRequiredService<IPublishIntentRepository>().GetIntentAsync(identity)).ShouldBe(completed);
        (await switched.GetRequiredService<IPublishIdMapRepository>().GetNewIdAsync(identity)).ShouldBe(recovered.NewId);
        AssertTitle(await ShowAsync(switched, 73, refresh: true), "Sibling-only release plan");
        _transport.CreateCount.ShouldBe(1);
    }

    private static void AssertNoReceipt(JsonElement report) =>
        (report.TryGetProperty("receipt", out var receipt) && receipt.ValueKind != JsonValueKind.Null).ShouldBeFalse();

    private Task<CapturedCommand> InvokeWritesAsync(ServiceProvider runtime,
        ConnectionRemoteWriteReconciliationService reconciliation, bool reconcile,
        string? intent = null, string? confirm = null, string? authorizer = null)
    {
        var command = new ConnectionWritesCommand(reconciliation, _configuration, CurrentPaths(),
            runtime.GetRequiredService<OutputFormatterFactory>(), runtime.GetRequiredService<RendererFactory>());
        return CaptureAsync(() => command.ExecuteAsync(reconcile, intent, confirm, authorizer,
            reconcile ? "Isolated original-actor native evidence proof" : null, "json", CancellationToken.None));
    }

    private SqliteSystemWorktreeRegistry OpenRegistry() => new(Path.Combine(Home, "system.db"), TimeProvider.System);
    private string AttachmentPath() => Path.Combine(_paths.TwigDir, WorktreeLocalAttachmentStore.AttachmentFileName);
    private string MarkerPath() => Path.Combine(_paths.TwigDir, "cache", MirrorAdmission.MarkerFile);
    private TwigPaths CurrentPaths()
    {
        var paths = TwigPaths.BuildPaths(_paths.TwigDir, _configuration, _paths.StartDir);
        return new TwigPaths(paths.TwigDir, paths.ConfigPath, paths.DbPath, paths.StartDir, Path.Combine(Home, "display.json"));
    }

    private async Task<ServiceProvider> CreateRuntimeAsync(TwigPaths? pathsOverride = null)
    {
        var paths = pathsOverride ?? CurrentPaths();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddConnectionServices(_configuration, paths.TwigDir, paths.StartDir);
        services.AddSingleton(paths);
        services.AddSingleton<ISystemWorktreeRegistry>(_ => OpenRegistry());
        services.AddSingleton(_http);
        services.AddTwigNetworkServices(_configuration);
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        var runtime = services.BuildServiceProvider();
        _runtimes.Add(runtime);
        await runtime.GetRequiredService<IProcessTypeStore>().SaveAsync(new ProcessTypeRecord
        {
            TypeName = "Task",
            States = [new("To Do", StateCategory.Proposed, null), new("Doing", StateCategory.InProgress, null), new("Done", StateCategory.Completed, null)],
            DefaultChildType = "Task",
            ValidChildTypes = ["Task"],
        });
        return runtime;
    }

    private async Task ApplyPinAsync(string? binding)
    {
        var preview = await _transitions.PreviewAsync(_configuration, CurrentPaths(), binding);
        preview.CanApply.ShouldBeTrue(string.Join("\n", preview.Blockers));
        var applied = await _transitions.ApplyAsync(_configuration, CurrentPaths(), binding, preview.Digest);
        applied.CanApply.ShouldBeTrue(string.Join("\n", applied.Blockers));
    }

    private static PrimaryScope Scope(int id) => new(id,
        $"https://dev.azure.com/{Organization}/{Project}/_workitems/edit/{id}", DateTimeOffset.UtcNow);

    private async Task<Dictionary<string, byte[]>> CredentialBytesAsync()
    {
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var identity in await _bindings.ListIdentitiesAsync())
        {
            var path = Path.Combine(Home, "credentials", identity.CredentialRef + ".json");
            bytes.Add(path, await File.ReadAllBytesAsync(path));
        }
        return bytes;
    }

    private static async Task<WorkItem> StageSeedAsync(ServiceProvider runtime, string title)
    {
        var minted = await runtime.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
        var created = new SeedFactory().CreateUnparented(title, WorkItemType.Task,
            AreaPath.Parse(Project).Value, IterationPath.Parse(Project).Value, minted);
        created.IsSuccess.ShouldBeTrue(created.Error);
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(created.Value);
        return created.Value;
    }

    private async Task<string> FieldProposalAsync(string name, string operation, int id, int revision, string title)
    {
        var path = Path.Combine(_paths.TwigDir, name + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = 1,
            workspace = new { organization = Organization, project = Project },
            operations = new[] { new { id = operation, kind = "batch", workItemId = id, expectedRevision = revision,
                fields = new Dictionary<string, string> { ["System.Title"] = title } } },
        }));
        return path;
    }

    private async Task<string> SeedProposalAsync(PlanSeedDescriptor descriptor)
    {
        var path = Path.Combine(_paths.TwigDir, "historical-seed.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            version = 1,
            workspace = new { organization = Organization, project = Project },
            operations = new[] { new { id = "seed-op", kind = "publish-seed", stagedIdentity = descriptor.Identity.ToString(), expectedFingerprint = descriptor.Fingerprint } },
        }));
        return path;
    }

    private static ProposalAuthorization Authorize(string digest) => new()
    {
        Digest = digest,
        Mode = ProposalAuthorizationMode.Human,
        AuthorizerIdentity = "Fixture scope owner",
        Rationale = "Isolated transition consumer proof",
        AuthorizedAt = DateTimeOffset.UtcNow,
    };

    private async Task<CapturedCommand> InvokePinAsync(ServiceProvider runtime, string? binding, bool remove, string? confirm = null)
    {
        var command = new ConnectionPinCommand(_transitions, _configuration, CurrentPaths(),
            runtime.GetRequiredService<OutputFormatterFactory>(), runtime.GetRequiredService<RendererFactory>());
        return await CaptureAsync(() => command.ExecuteAsync(binding, remove, confirm, "json", CancellationToken.None));
    }

    private static Task<CapturedCommand> ShowAsync(ServiceProvider runtime, int id, bool refresh) =>
        CaptureAsync(() => new TwigCommands(runtime).Show(id, "json", refresh: refresh));

    private static async Task<CapturedCommand> CaptureAsync(Func<Task<int>> action)
    {
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exit = await action();
            return new CapturedCommand(exit, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
    }

    private static void AssertTitle(CapturedCommand result, string expected)
    {
        result.Exit.ShouldBe(0, result.Error + result.Output);
        using var document = JsonDocument.Parse(result.Output);
        document.RootElement.GetProperty("title").GetString().ShouldBe(expected);
    }

    private static TwigRefreshTokenStoreEntry RefreshEntry(string refresh) => new()
    {
        RefreshToken = refresh,
        ClientId = "fixture-client",
        TenantId = Tenant,
        ObjectId = Actor,
        AuthorityHost = "login.microsoftonline.com",
        Source = "fixture",
    };

    private static string Token(string renewal)
    {
        var payload = JsonSerializer.Serialize(new
        {
            aud = "499b84ac-1321-427f-aa17-267ca6975798",
            tid = Tenant,
            oid = Actor,
            iss = "https://sts.windows.net/" + Tenant + "/",
            exp = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
            renewal,
        });
        return "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
    }

    private sealed class FixtureRefresher : ITokenRefresher
    {
        public Task<(string? AccessToken, string? RefreshToken, bool IsInvalidGrant)> TryRefreshAsync(
            string refreshToken, string clientId, string tenantId, string authorityHost, CancellationToken ct = default) =>
            Task.FromResult<(string?, string?, bool)>((Token(refreshToken.EndsWith("v2", StringComparison.Ordinal) ? "v2" : "v1"), null, false));
    }

    private sealed record CapturedCommand(int Exit, string Output, string Error);

    private sealed class PrincipalTransport : HttpMessageHandler
    {
        private readonly Dictionary<int, StoredItem> _items = new()
        {
            [42] = NewItem("Actor-only release plan", Actor),
            [43] = NewItem("Actor clean snapshot v1", Actor),
            [44] = NewItem("Actor history target", Actor),
            [73] = NewItem("Sibling-only release plan", Sibling),
        };
        private readonly Dictionary<(int Id, int Revision), StoredItem> _revisions = new();
        private readonly List<(int Id, int WorkItemId, string Principal, string Text)> _comments = [];
        internal bool LoseNextCommentResponse { get; set; }
        internal bool LoseNextPatchResponse { get; set; }
        internal bool HideLostPatchReadback { get; set; }
        private int? _hiddenLostPatchItem;
        internal bool LoseNextCreateResponse { get; set; }
        internal bool HideCorrelatedSeedFromQueries { get; set; }
        internal int CreateCount { get; private set; }
        internal void AddLegacySeedCandidate(string title)
        {
            var candidate = NewItem(title, Actor);
            candidate.Fields["System.Tags"] = PublishIntent.IntentTag;
            candidate.Fields["System.CreatedDate"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            _items[401] = candidate;
        }
        internal int CommentCount => _comments.Count;
        private int _workRequests;
        private int _mutations;
        private int _holdNext;
        private readonly TaskCompletionSource _releaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int WorkRequests => Volatile.Read(ref _workRequests);
        internal int Mutations => Volatile.Read(ref _mutations);
        internal void HoldNextRead() => Volatile.Write(ref _holdNext, 1);
        internal void ReleaseRead() => _releaseRead.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var header = request.Headers.Authorization;
            string? principal = null;
            var renewed = false;
            if (header?.Scheme == "Basic")
            {
                var secret = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!))[1..];
                principal = secret is ActorPat or RenewedActorPat ? Actor : secret == SiblingPat ? Sibling : null;
                renewed = secret == RenewedActorPat;
            }
            else if (header?.Scheme == "Bearer")
            {
                var info = JwtAccessTokenInspector.TryDecode(header.Parameter);
                if (info?.ObjectId == Actor && info.TenantId == Tenant && info.IsValidAdoAudience) principal = Actor;
                var payload = header.Parameter!.Split('.')[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
                renewed = claims.RootElement.GetProperty("renewal").GetString() == "v2";
            }
            if (principal is null) return Reply(HttpStatusCode.Unauthorized, new { message = "Unknown fixture principal" });
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/_apis/connectionData", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { authenticatedUser = new { id = principal, providerDisplayName = "Alex Reader" } });
            if (path.Contains("/_apis/profile/profiles/me", StringComparison.Ordinal))
                return Reply(HttpStatusCode.OK, new { displayName = "Alex Reader", emailAddress = principal + "@fixture.example" });
            if (path.Contains("/_apis/wit/wiql", StringComparison.OrdinalIgnoreCase))
            {
                using var queryBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var query = queryBody.RootElement.GetProperty("query").GetString()!;
                var tags = System.Text.RegularExpressions.Regex.Matches(query, "\\[System\\.Tags\\] CONTAINS '([^']+)'")
                    .Select(match => match.Groups[1].Value).ToArray();
                var titleMatch = System.Text.RegularExpressions.Regex.Match(query, "\\[System\\.Title\\] = '([^']+)'");
                var candidates = _items.Where(pair => pair.Value.Principal == principal
                    && (!titleMatch.Success || pair.Value.Fields.GetValueOrDefault("System.Title") as string == titleMatch.Groups[1].Value)
                    && pair.Value.Fields.GetValueOrDefault("System.Tags") is string itemTags
                    && tags.All(tag => itemTags.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Contains(tag, StringComparer.Ordinal))
                    && (!HideCorrelatedSeedFromQueries || !itemTags.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Any(tag => tag.StartsWith("twig-publishing-", StringComparison.Ordinal))));
                return Reply(HttpStatusCode.OK, new { queryType = "flat", workItems = candidates.Select(pair => new { id = pair.Key }).ToArray() });
            }
            if (!path.Contains("/_apis/wit/workitems/", StringComparison.OrdinalIgnoreCase))
                return Reply(HttpStatusCode.OK, new { count = 0, value = Array.Empty<object>() });

            Interlocked.Increment(ref _workRequests);
            if (request.Method == HttpMethod.Get && Interlocked.Exchange(ref _holdNext, 0) == 1)
            {
                ReadStarted.TrySetResult();
                await _releaseRead.Task.WaitAsync(ct);
            }
            var suffix = path[(path.IndexOf("/_apis/wit/workitems/", StringComparison.OrdinalIgnoreCase) + "/_apis/wit/workitems/".Length)..].Split('/')[0];
            if (path.Contains("/comments", StringComparison.Ordinal))
            {
                if (!int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var commentItemId)
                    || !_items.TryGetValue(commentItemId, out var commentItem) || commentItem.Principal != principal)
                    return Reply(HttpStatusCode.NotFound, new { message = "Comment item is not readable under this principal" });
                if (request.Method == HttpMethod.Post)
                {
                    using var comment = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    var text = comment.RootElement.GetProperty("text").GetString()!;
                    var commentId = 100 + _comments.Count;
                    _comments.Add((commentId, commentItemId, principal, text));
                    Interlocked.Increment(ref _mutations);
                    if (LoseNextCommentResponse)
                    {
                        LoseNextCommentResponse = false;
                        throw new HttpRequestException("Fixture response lost after server accepted comment");
                    }
                    return Reply(HttpStatusCode.OK, new { id = commentId, text, createdBy = new { id = principal } });
                }
                return Reply(HttpStatusCode.OK, new { count = _comments.Count, comments = _comments.Where(x => x.WorkItemId == commentItemId)
                    .Select(x => new { id = x.Id, text = x.Text, createdBy = new { id = x.Principal } }).ToArray() });
            }
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _mutations);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var id = 500 + Mutations;
                var created = NewItem("Published actor history", principal);
                ApplyFields(created.Fields, body.RootElement);
                _items.Add(id, created);
                CreateCount++;
                created.Fields["System.CreatedDate"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                _revisions[(id, 1)] = created with { Fields = new Dictionary<string, object?>(created.Fields) };
                if (LoseNextCreateResponse)
                {
                    LoseNextCreateResponse = false;
                    throw new HttpRequestException("Fixture response lost after server created exact correlated seed");
                }
                return Item(id, created);
            }
            if (!int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var workId)
                || !_items.TryGetValue(workId, out var item) || item.Principal != principal)
                return Reply(HttpStatusCode.NotFound, new { message = "Not readable under the selected principal" });
            if (request.Method == HttpMethod.Get && _hiddenLostPatchItem == workId)
                return Reply(HttpStatusCode.NotFound, new { message = "Accepted write's original readback remains unavailable" });
            var revisionSegment = path.IndexOf("/revisions/", StringComparison.Ordinal);
            if (request.Method == HttpMethod.Get && revisionSegment >= 0)
            {
                var revisionText = path[(revisionSegment + "/revisions/".Length)..];
                if (int.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentRevision)
                    && currentRevision == item.Revision)
                    return Item(workId, item);
                return int.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedRevision)
                    && _revisions.TryGetValue((workId, requestedRevision), out var snapshot)
                    ? Item(workId, snapshot)
                    : Reply(HttpStatusCode.NotFound, new { message = "Immutable revision not found" });
            }
            if (request.Method == HttpMethod.Patch)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                foreach (var operation in body.RootElement.EnumerateArray())
                    if (operation.GetProperty("path").GetString() == "/rev" && operation.GetProperty("value").GetInt32() != item.Revision)
                        return Reply(HttpStatusCode.PreconditionFailed, new { message = "Revision mismatch" });
                Interlocked.Increment(ref _mutations);
                _revisions.TryAdd((workId, item.Revision), item with { Fields = new Dictionary<string, object?>(item.Fields) });
                ApplyFields(item.Fields, body.RootElement);
                item = item with { Revision = item.Revision + 1 };
                _items[workId] = item;
                _revisions[(workId, item.Revision)] = item with { Fields = new Dictionary<string, object?>(item.Fields) };
                if (LoseNextPatchResponse)
                {
                    LoseNextPatchResponse = false;
                    if (HideLostPatchReadback) _hiddenLostPatchItem = workId;
                    throw new HttpRequestException("Fixture response lost after server applied CAS patch");
                }
            }
            var fields = new Dictionary<string, object?>(item.Fields);
            if (workId == 43) fields["System.Title"] = renewed ? "Actor clean snapshot v2" : "Actor clean snapshot v1";
            return Item(workId, item with { Fields = fields });
        }

        private static StoredItem NewItem(string title, string principal) => new(principal, 1, new Dictionary<string, object?>
        {
            ["System.Title"] = title,
            ["System.WorkItemType"] = "Task",
            ["System.State"] = "To Do",
            ["System.AreaPath"] = Project,
            ["System.IterationPath"] = Project,
            ["System.AssignedTo"] = principal + "@fixture.example",
        });

        private static void ApplyFields(Dictionary<string, object?> fields, JsonElement operations)
        {
            foreach (var operation in operations.EnumerateArray())
            {
                var path = operation.GetProperty("path").GetString()!;
                if (!path.StartsWith("/fields/", StringComparison.Ordinal)) continue;
                if (operation.GetProperty("op").GetString() == "remove") fields.Remove(path[8..]);
                else if (operation.TryGetProperty("value", out var value))
                    fields[path[8..]] = value.ValueKind == JsonValueKind.Null ? null : value.ToString();
            }
        }

        private static HttpResponseMessage Item(int id, StoredItem item)
        {
            var fields = new Dictionary<string, object?>(item.Fields);
            if (fields.TryGetValue("System.AssignedTo", out var assigned) && assigned is string name)
                fields["System.AssignedTo"] = new { displayName = "Alex Reader", uniqueName = name };
            return Reply(HttpStatusCode.OK, new { id, rev = item.Revision, fields, relations = Array.Empty<object>() });
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, object body) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

        private sealed record StoredItem(string Principal, int Revision, Dictionary<string, object?> Fields);
    }
}
