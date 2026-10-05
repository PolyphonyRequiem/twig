using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Claims;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class ConnectionBindingTransitionConsumerTests
{
    [HostMigrationFact]
    public async Task CompoundDefaultChangesEveryUnpinnedCheckoutButAnOpenPinnedActorKeepsItsPrivateCacheAndRuntime()
    {
        var secondPaths = await AddDefaultCheckoutAsync("second");
        var pinnedPaths = await AddDefaultCheckoutAsync("pinned", _actorBinding.BindingId);
        var first = await CreateRuntimeAsync();
        var second = await CreateRuntimeAsync(secondPaths);
        var pinned = await CreateRuntimeAsync(pinnedPaths);
        foreach (var runtime in new[] { first, second, pinned })
            AssertTitle(await ShowAsync(runtime, 42, refresh: true), "Actor-only release plan");
        var pinnedBefore = await _bindings.ResolveAsync(_configuration, pinnedPaths);
        var pinnedAttachment = await File.ReadAllBytesAsync(ConnectionBindingTransitionService.AttachmentPath(pinnedPaths));
        var pinnedMarker = await File.ReadAllBytesAsync(DefaultMarkerPath(pinnedPaths));
        var credentials = await CredentialBytesAsync();
        using var defaults = new ConnectionDefaultTransitionService(Home, _bindings);
        var preview = await InvokeDefaultAsync(defaults, first, _siblingBinding.BindingId);
        preview.Exit.ShouldBe(0, preview.Error + preview.Output);
        using var document = JsonDocument.Parse(preview.Output);
        var digest = document.RootElement.GetProperty("digest").GetString()!;
        var members = document.RootElement.GetProperty("members").EnumerateArray().ToArray();
        members.Count(x => x.GetProperty("affected").GetBoolean()).ShouldBe(2);
        members.Single(x => !x.GetProperty("affected").GetBoolean()).GetProperty("worktreeRoot").GetString().ShouldBe(pinnedPaths.RepoRoot);
        (await InvokeDefaultAsync(defaults, first, _siblingBinding.BindingId, digest)).Exit.ShouldBe(0);
        var requests = _transport.WorkRequests;
        foreach (var runtime in new[] { first, second })
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => runtime.GetRequiredService<IAdoWorkItemService>().FetchAsync(42)))
                .Message.ShouldContain("binding-changed");
            await Should.ThrowAsync<InvalidOperationException>(() => runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42));
        }
        _transport.WorkRequests.ShouldBe(requests);
        (await _bindings.ResolveAsync(_configuration, pinnedPaths)).ShouldBe(pinnedBefore);
        (await pinned.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Actor-only release plan");
        AssertTitle(await ShowAsync(pinned, 42, refresh: true), "Actor-only release plan");
        (await File.ReadAllBytesAsync(ConnectionBindingTransitionService.AttachmentPath(pinnedPaths))).ShouldBe(pinnedAttachment);
        (await File.ReadAllBytesAsync(DefaultMarkerPath(pinnedPaths))).ShouldBe(pinnedMarker);
        foreach (var credential in credentials) (await File.ReadAllBytesAsync(credential.Key)).ShouldBe(credential.Value);
        foreach (var paths in new[] { CurrentPaths(), secondPaths })
        {
            var reconnected = await CreateRuntimeAsync(paths);
            (await reconnected.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42)).ShouldBeNull();
            (await ShowAsync(reconnected, 42, refresh: true)).Exit.ShouldBe(1);
            AssertTitle(await ShowAsync(reconnected, 73, refresh: true), "Sibling-only release plan");
        }
        _transport.Mutations.ShouldBe(0);
    }

    [HostMigrationTheory]
    [InlineData("field")]
    [InlineData("note")]
    [InlineData("seed")]
    [InlineData("unknown-patch")]
    [InlineData("unknown-create")]
    public async Task OneUnfinishedAffectedMemberPreventsAnyDefaultGenerationOrCacheChange(string unfinished)
    {
        var secondPaths = await AddDefaultCheckoutAsync("blocked-member");
        var first = await CreateRuntimeAsync();
        var second = await CreateRuntimeAsync(secondPaths);
        AssertTitle(await ShowAsync(first, 42, refresh: true), "Actor-only release plan");
        AssertTitle(await ShowAsync(second, 42, refresh: true), "Actor-only release plan");
        if (unfinished is "field" or "note")
            await second.GetRequiredService<IPendingChangeStore>().AddChangeAsync(42, unfinished,
                unfinished == "field" ? "System.Title" : null, null, "Blocked member's unpublished work");
        else if (unfinished == "seed") await StageSeedAsync(second, "Blocked unpublished seed");
        else if (unfinished == "unknown-patch")
        {
            _transport.LoseNextPatchResponse = true;
            await Should.ThrowAsync<Twig.Infrastructure.Ado.Exceptions.AdoOfflineException>(() => second.GetRequiredService<IAdoWorkItemService>()
                .PatchAsync(42, [new Twig.Domain.ValueObjects.FieldChange("System.Title", "Actor-only release plan", "Unknown accepted patch")], 1));
        }
        else
        {
            var seed = await StageSeedAsync(second, "Unknown accepted seed");
            _transport.LoseNextCreateResponse = true;
            await Should.ThrowAsync<Twig.Infrastructure.Ado.Exceptions.AdoOfflineException>(() =>
                second.GetRequiredService<Twig.Domain.Services.Seed.SeedPublishOrchestrator>().PublishAsync(seed.Id));
        }
        var beforeFirst = await _bindings.ResolveAsync(_configuration, CurrentPaths());
        var beforeSecond = await _bindings.ResolveAsync(_configuration, secondPaths);
        var markerFirst = await File.ReadAllBytesAsync(DefaultMarkerPath(CurrentPaths()));
        var markerSecond = await File.ReadAllBytesAsync(DefaultMarkerPath(secondPaths));
        var pendingBefore = await second.GetRequiredService<IPendingChangeReader>().GetAllChangesAsync();
        var seedsBefore = await second.GetRequiredService<IWorkItemRepository>().GetSeedsAsync();
        var mutations = _transport.Mutations;
        using var defaults = new ConnectionDefaultTransitionService(Home, _bindings);
        var preview = await defaults.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeFalse();
        preview.Blockers.ShouldContain(x => x.Contains("binding-", StringComparison.Ordinal));
        (await defaults.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest)).CanApply.ShouldBeFalse();
        (await _bindings.ResolveAsync(_configuration, CurrentPaths())).ShouldBe(beforeFirst);
        (await _bindings.ResolveAsync(_configuration, secondPaths)).ShouldBe(beforeSecond);
        (await File.ReadAllBytesAsync(DefaultMarkerPath(CurrentPaths()))).ShouldBe(markerFirst);
        (await File.ReadAllBytesAsync(DefaultMarkerPath(secondPaths))).ShouldBe(markerSecond);
        (await first.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Actor-only release plan");
        (await second.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Actor-only release plan");
        (await second.GetRequiredService<IPendingChangeReader>().GetAllChangesAsync()).ShouldBe(pendingBefore);
        (await second.GetRequiredService<IWorkItemRepository>().GetSeedsAsync()).Select(x => x.Id).ShouldBe(seedsBefore.Select(x => x.Id));
        using var registry = OpenRegistry();
        (await registry.ReadDefaultTransitionAsync(ConnectionRefResolver.Compute(_configuration))).Value.ShouldBeNull();
        _transport.Mutations.ShouldBe(mutations, "default refusal neither publishes nor reconciles an unknown native write");
    }

    [HostMigrationFact]
    public async Task NewerVerifiedProposalCannotHideAnOlderUnknownOutcomeOnAnotherAffectedMember()
    {
        var secondPaths = await AddDefaultCheckoutAsync("older-outcome");
        var first = await CreateRuntimeAsync();
        var second = await CreateRuntimeAsync(secondPaths);
        var lifecycle = second.GetRequiredService<IPlanLifecycleService>();
        var olderFile = await DefaultProposalAsync(secondPaths, "older-default", "older-op", 42, "Unknown earlier accepted title");
        var olderDigest = (await lifecycle.PreviewAsync(olderFile)).Digest!;
        _transport.LoseNextPatchResponse = true;
        _transport.HideLostPatchReadback = true;
        var unknown = await lifecycle.ApplyAsync(olderFile, olderDigest, Authorize(olderDigest));
        unknown.Failed.ShouldBeTrue();
        unknown.Operations.Single().State.ShouldBe(PlanOperationState.Indeterminate);
        var newerFile = await DefaultProposalAsync(secondPaths, "newer-default", "newer-op", 44, "Verified later unrelated work");
        var newerDigest = (await lifecycle.PreviewAsync(newerFile)).Digest!;
        (await lifecycle.ApplyAsync(newerFile, newerDigest, Authorize(newerDigest))).Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        var journal = second.GetRequiredService<IPlanJournalRepository>();
        var olderBefore = (await journal.GetAsync(olderDigest))!;
        var mutations = _transport.Mutations;
        using var defaults = new ConnectionDefaultTransitionService(Home, _bindings);
        var result = await InvokeDefaultAsync(defaults, first, _siblingBinding.BindingId);
        result.Exit.ShouldBe(1);
        var retained = (await journal.GetAsync(olderDigest))!;
        retained.Operations.Single().State.ShouldBe(olderBefore.Operations.Single().State);
        retained.CanonicalJson.ShouldBe(olderBefore.CanonicalJson);
        File.Exists(olderFile).ShouldBeTrue();
        _transport.Mutations.ShouldBe(mutations);
        (await _bindings.ResolveAsync(_configuration, CurrentPaths())).Binding.BindingId.ShouldBe(_actorBinding.BindingId);
        (await _bindings.ResolveAsync(_configuration, secondPaths)).Binding.BindingId.ShouldBe(_actorBinding.BindingId);
    }

    [HostMigrationTheory]
    [InlineData("intent-recorded")]
    [InlineData("central-committed")]
    [InlineData("mirror-reset:0")]
    [InlineData("member-completed:0")]
    public async Task InterruptedCompoundDefaultFencesTheWholeFamilyEvenAfterOneLocalMemberCompletesAndExactRecoveryPreservesHistory(string checkpoint)
    {
        var secondPaths = await AddDefaultCheckoutAsync("recover-member");
        var first = await CreateRuntimeAsync();
        var second = await CreateRuntimeAsync(secondPaths);
        AssertTitle(await ShowAsync(first, 42, refresh: true), "Actor-only release plan");
        AssertTitle(await ShowAsync(second, 42, refresh: true), "Actor-only release plan");
        var lifecycle = first.GetRequiredService<IPlanLifecycleService>();
        var file = await FieldProposalAsync("default-history", "history-op", 44, 1, "Verified preserved actor history");
        var historicalDigest = (await lifecycle.PreviewAsync(file)).Digest!;
        (await lifecycle.ApplyAsync(file, historicalDigest, Authorize(historicalDigest))).Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        var historyBytes = await File.ReadAllBytesAsync(file);
        var attachment = first.GetRequiredService<IPrimaryScopeAttachmentStore>();
        var versioned = (await attachment.ReadWithRevisionAsync()).Value;
        (await attachment.WriteAsync(versioned.Attachment.WithPrimaryScope(Scope(42)), versioned.Revision)).IsSuccess.ShouldBeTrue();
        var attachmentBytes = await File.ReadAllBytesAsync(AttachmentPath());
        var policy = await File.ReadAllBytesAsync(_paths.RepoConfigPath);
        var credentials = await CredentialBytesAsync();
        var mutations = _transport.Mutations;
        using var interrupted = new ConnectionDefaultTransitionService(Home, _bindings, step =>
        {
            if (step == checkpoint) throw new IOException("Fixture interrupted native compound default");
        });
        var preview = await interrupted.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeTrue(string.Join('\n', preview.Blockers));
        (await interrupted.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest)).CanApply.ShouldBeFalse();
        var requests = _transport.WorkRequests;
        foreach (var entry in new[] { (Runtime: first, Paths: CurrentPaths()), (Runtime: second, Paths: secondPaths) })
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => _bindings.ResolveAsync(_configuration, entry.Paths))).Message.ShouldContain("binding-default-transition-incomplete");
            await Should.ThrowAsync<InvalidOperationException>(() => entry.Runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42));
            await Should.ThrowAsync<InvalidOperationException>(() => entry.Runtime.GetRequiredService<IAdoWorkItemService>().FetchAsync(42));
        }
        _transport.WorkRequests.ShouldBe(requests);
        using var recovering = new ConnectionDefaultTransitionService(Home, _bindings);
        var resumedPreview = await recovering.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        resumedPreview.Digest.ShouldBe(preview.Digest);
        resumedPreview.CanApply.ShouldBeTrue(string.Join('\n', resumedPreview.Blockers));
        (await recovering.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, new string('0', 64))).CanApply.ShouldBeFalse();
        (await recovering.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest)).State.ShouldBe("completed");
        foreach (var paths in new[] { CurrentPaths(), secondPaths })
        {
            var reconnected = await CreateRuntimeAsync(paths);
            (await reconnected.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42)).ShouldBeNull();
            AssertTitle(await ShowAsync(reconnected, 73, refresh: true), "Sibling-only release plan");
        }
        (await File.ReadAllBytesAsync(AttachmentPath())).ShouldBe(attachmentBytes);
        (await File.ReadAllBytesAsync(_paths.RepoConfigPath)).ShouldBe(policy);
        (await File.ReadAllBytesAsync(file)).ShouldBe(historyBytes);
        foreach (var credential in credentials) (await File.ReadAllBytesAsync(credential.Key)).ShouldBe(credential.Value);
        var freshFirst = await CreateRuntimeAsync();
        (await freshFirst.GetRequiredService<IPlanJournalRepository>().GetAsync(historicalDigest))!.Operations.Single().State.ShouldBe(PlanOperationState.Verified);
        _transport.Mutations.ShouldBe(mutations);
    }

    [HostMigrationFact]
    public async Task PinnedCheckoutScopeChangesDoNotStrandAnInterruptedDefaultFamily()
    {
        var pinnedPaths = await AddDefaultCheckoutAsync("recovery-pinned", _actorBinding.BindingId);
        var pinned = await CreateRuntimeAsync(pinnedPaths);
        AssertTitle(await ShowAsync(pinned, 42, refresh: true), "Actor-only release plan");
        using var interrupted = new ConnectionDefaultTransitionService(Home, _bindings, step =>
        {
            if (step == "central-committed") throw new IOException("Interrupted native default family");
        });
        var preview = await interrupted.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeTrue(string.Join('\n', preview.Blockers));
        (await interrupted.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest)).CanApply.ShouldBeFalse();

        var attachment = pinned.GetRequiredService<IPrimaryScopeAttachmentStore>();
        var current = (await attachment.ReadWithRevisionAsync()).Value;
        (await attachment.WriteAsync(current.Attachment.WithPrimaryScope(Scope(43)), current.Revision)).IsSuccess.ShouldBeTrue();
        var preserved = await File.ReadAllBytesAsync(ConnectionBindingTransitionService.AttachmentPath(pinnedPaths));

        using var recovering = new ConnectionDefaultTransitionService(Home, _bindings);
        var resumed = await recovering.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        resumed.Digest.ShouldBe(preview.Digest);
        resumed.CanApply.ShouldBeTrue(string.Join('\n', resumed.Blockers));
        (await recovering.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest)).State.ShouldBe("completed");
        (await File.ReadAllBytesAsync(ConnectionBindingTransitionService.AttachmentPath(pinnedPaths))).ShouldBe(preserved);
        (await pinned.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Actor-only release plan");
        (await _bindings.ResolveAsync(_configuration, pinnedPaths)).Binding.BindingId.ShouldBe(_actorBinding.BindingId);
        _transport.Mutations.ShouldBe(0);
    }

    [HostMigrationTheory]
    [InlineData("attachment")]
    [InlineData("default")]
    [InlineData("binding")]
    [InlineData("membership")]
    public async Task ExactDefaultConfirmationCannotFollowAnyChangedMemberOrCentralCasRevision(string changed)
    {
        var secondPaths = await AddDefaultCheckoutAsync("cas-member");
        var first = await CreateRuntimeAsync();
        var second = await CreateRuntimeAsync(secondPaths);
        AssertTitle(await ShowAsync(first, 42, refresh: true), "Actor-only release plan");
        using var defaults = new ConnectionDefaultTransitionService(Home, _bindings);
        var preview = await defaults.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        preview.CanApply.ShouldBeTrue(string.Join('\n', preview.Blockers));
        if (changed == "attachment")
        {
            var attachment = second.GetRequiredService<IPrimaryScopeAttachmentStore>();
            var current = (await attachment.ReadWithRevisionAsync()).Value;
            (await attachment.WriteAsync(current.Attachment.WithPrimaryScope(Scope(42)), current.Revision)).IsSuccess.ShouldBeTrue();
        }
        else if (changed == "membership") await AddDefaultCheckoutAsync("new-member-after-review");
        else
        {
            using var db = new SqliteConnection($"Data Source={Path.Combine(Home, "system.db")};Pooling=False");
            await db.OpenAsync(); using var cmd = db.CreateCommand();
            cmd.CommandText = changed == "default" ? "UPDATE connection_defaults SET revision=revision+1 WHERE connection_ref=$key;"
                : "UPDATE connection_bindings SET revision=revision+1 WHERE binding_id=$key;";
            cmd.Parameters.AddWithValue("$key", changed == "default" ? ConnectionRefResolver.Compute(_configuration) : _siblingBinding.BindingId);
            (await cmd.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        (await defaults.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest)).CanApply.ShouldBeFalse();
        (await first.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Actor-only release plan");
        using var registry = OpenRegistry();
        (await registry.FindDefaultBindingAsync(ConnectionRefResolver.Compute(_configuration))).Value!.BindingId.ShouldBe(_actorBinding.BindingId);
        (await registry.ReadDefaultTransitionAsync(ConnectionRefResolver.Compute(_configuration))).Value.ShouldBeNull();
        _transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task InaccessibleRegisteredMemberAndActualLiveReadEachBlockTheEntireCompoundDefault()
    {
        var secondPaths = await AddDefaultCheckoutAsync("unreachable-member");
        var first = await CreateRuntimeAsync();
        using var defaults = new ConnectionDefaultTransitionService(Home, _bindings);
        var preview = await defaults.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        var unavailable = secondPaths.RepoRoot + ".unavailable";
        Directory.Move(secondPaths.RepoRoot, unavailable);
        try
        {
            var refused = await defaults.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest);
            refused.CanApply.ShouldBeFalse();
            refused.Blockers.ShouldContain(x => x.Contains("member-unreachable", StringComparison.Ordinal));
            (await _bindings.ResolveAsync(_configuration, CurrentPaths())).Binding.BindingId.ShouldBe(_actorBinding.BindingId);
        }
        finally { Directory.Move(unavailable, secondPaths.RepoRoot); }
        var second = await CreateRuntimeAsync(secondPaths);
        _transport.HoldNextRead();
        var read = ShowAsync(second, 42, refresh: true);
        try
        {
            await _transport.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            (await defaults.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest)).CanApply.ShouldBeFalse();
            (await _bindings.ResolveAsync(_configuration, CurrentPaths())).Binding.BindingId.ShouldBe(_actorBinding.BindingId);
        }
        finally { _transport.ReleaseRead(); }
        AssertTitle(await read, "Actor-only release plan");
        (await defaults.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest)).State.ShouldBe("completed");
        _transport.Mutations.ShouldBe(0);
        await Should.ThrowAsync<InvalidOperationException>(() => first.GetRequiredService<IWorkItemRepository>().GetByIdAsync(73));
    }

    [HostMigrationFact]
    public async Task SameDefaultAndLostFamilyCompletionAcknowledgementNeverResetNewReadDataOrPendingWork()
    {
        var secondPaths = await AddDefaultCheckoutAsync("receipt-member");
        using var defaults = new ConnectionDefaultTransitionService(Home, _bindings);
        var preview = await defaults.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        (await defaults.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest)).State.ShouldBe("completed");
        var runtime = await CreateRuntimeAsync();
        var second = await CreateRuntimeAsync(secondPaths);
        AssertTitle(await ShowAsync(runtime, 73, refresh: true), "Sibling-only release plan");
        AssertTitle(await ShowAsync(second, 73, refresh: true), "Sibling-only release plan");
        await second.GetRequiredService<IPendingChangeStore>().AddChangeAsync(73, "note", null, null, "New actor's unfinished note");
        var before = await _bindings.ResolveAsync(_configuration, secondPaths);
        (await defaults.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, preview.Digest)).State.ShouldBe("completed");
        var same = await defaults.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        same.State.ShouldBe("unchanged");
        (await defaults.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, same.Digest)).State.ShouldBe("unchanged");
        (await _bindings.ResolveAsync(_configuration, secondPaths)).ShouldBe(before);
        (await second.GetRequiredService<IWorkItemRepository>().GetByIdAsync(73))!.Title.ShouldBe("Sibling-only release plan");
        (await runtime.GetRequiredService<IWorkItemRepository>().GetByIdAsync(73))!.Title.ShouldBe("Sibling-only release plan");
        (await second.GetRequiredService<IPendingChangeStore>().GetChangesAsync(73)).ShouldHaveSingleItem().NewValue.ShouldBe("New actor's unfinished note");
        _transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task AnotherAffectedCheckoutActiveHolderBlocksDefaultWithoutImplicitReleaseOrScopeRewrite()
    {
        var secondPaths = await AddDefaultCheckoutAsync("held-member");
        var first = await CreateRuntimeAsync();
        var second = await CreateRuntimeAsync(secondPaths);
        var attachment = second.GetRequiredService<IPrimaryScopeAttachmentStore>();
        var versioned = (await attachment.ReadWithRevisionAsync()).Value;
        (await attachment.WriteAsync(versioned.Attachment.WithPrimaryScope(Scope(42)), versioned.Revision)).IsSuccess.ShouldBeTrue();
        var projection = second.GetRequiredService<IAdoClaimProjection>();
        var holder = (await second.GetRequiredService<ILocalClaimService>().MintAsync(new MintClaimInput(
            ConnectionRefResolver.Compute(_configuration), PrimaryScopeKinds.AdoWorkItem, "42",
            WorktreeFingerprintProvider.CanonicalJson(WorktreeAnchorDetector.Detect(secondPaths.StartDir)!.Value),
            "", "Alex Reader", null, null, projection))).ShouldBeOfType<ClaimMintOutcome.Succeeded>();
        var before = (await second.GetRequiredService<ISystemWorktreeRegistry>().FindClaimAsync(holder.Claim.ClaimId)).Value;
        var attachmentBytes = await File.ReadAllBytesAsync(ConnectionBindingTransitionService.AttachmentPath(secondPaths));
        var mutations = _transport.Mutations;
        using var defaults = new ConnectionDefaultTransitionService(Home, _bindings);
        (await InvokeDefaultAsync(defaults, first, _siblingBinding.BindingId)).Exit.ShouldBe(1);
        (await second.GetRequiredService<ISystemWorktreeRegistry>().FindClaimAsync(holder.Claim.ClaimId)).Value.ShouldBe(before);
        (await File.ReadAllBytesAsync(ConnectionBindingTransitionService.AttachmentPath(secondPaths))).ShouldBe(attachmentBytes);
        (await _bindings.ResolveAsync(_configuration, CurrentPaths())).Binding.BindingId.ShouldBe(_actorBinding.BindingId);
        _transport.Mutations.ShouldBe(mutations);
    }

    [HostMigrationFact]
    public async Task RealConcurrentDefaultAndLocalPinHaveOneReviewedCasWinnerAndNeverCrossActorAuthority()
    {
        var secondPaths = await AddDefaultCheckoutAsync("racing-member");
        using var defaults = new ConnectionDefaultTransitionService(Home, _bindings);
        var family = await defaults.PreviewAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId);
        var local = await _transitions.PreviewAsync(_configuration, secondPaths, _actorBinding.BindingId);
        family.CanApply.ShouldBeTrue(string.Join('\n', family.Blockers));
        local.CanApply.ShouldBeTrue(string.Join('\n', local.Blockers));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var defaultApply = Task.Run(async () =>
        {
            await start.Task;
            return (await defaults.ApplyAsync(_configuration, CurrentPaths(), _siblingBinding.BindingId, family.Digest)).CanApply;
        });
        var pinApply = Task.Run(async () =>
        {
            await start.Task;
            try { return (await _transitions.ApplyAsync(_configuration, secondPaths, _actorBinding.BindingId, local.Digest)).CanApply; }
            catch (InvalidOperationException) { return false; }
        });
        start.SetResult();
        var winners = await Task.WhenAll(defaultApply, pinApply);
        winners.Count(x => x).ShouldBe(1, "two actual native managers cannot both apply their old reviewed selection snapshots");
        var firstSelection = await _bindings.ResolveAsync(_configuration, CurrentPaths());
        var secondSelection = await _bindings.ResolveAsync(_configuration, secondPaths);
        if (winners[0])
        {
            firstSelection.Binding.BindingId.ShouldBe(_siblingBinding.BindingId);
            secondSelection.Binding.BindingId.ShouldBe(_siblingBinding.BindingId);
            secondSelection.SelectionSource.ShouldBe("connection-default-binding");
        }
        else
        {
            firstSelection.Binding.BindingId.ShouldBe(_actorBinding.BindingId);
            secondSelection.Binding.BindingId.ShouldBe(_actorBinding.BindingId);
            secondSelection.SelectionSource.ShouldBe("checkout-binding-pin");
        }
        _transport.Mutations.ShouldBe(0);
    }

    private async Task<TwigPaths> AddDefaultCheckoutAsync(string name, string? pin = null)
    {
        var root = Path.Combine(_temp, name); Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        var paths = new TwigPaths(Path.Combine(root, ".twig"), Path.Combine(root, ".twig", "config"),
            Path.Combine(root, ".twig", "cache", "twig.db"), root, Path.Combine(Home, "display.json"));
        await _configuration.SaveSplitAsync(paths);
        using (var attachment = new WorktreeLocalAttachmentStore(paths, _configuration, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        using (var registry = OpenRegistry())
        {
            var anchor = WorktreeAnchorDetector.Detect(root)!.Value;
            (await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), ConnectionRefResolver.Compute(_configuration), root)).IsSuccess.ShouldBeTrue();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DbPath)!);
        using (var legacy = new SqliteCacheStore($"Data Source={paths.DbPath}")) _ = legacy.GetConnection();
        var preview = await _migration.PreviewAsync(_configuration, paths, "actor", null);
        preview.CanApply.ShouldBeTrue(string.Join('\n', preview.Blockers));
        (await _migration.ApplyAsync(_configuration, paths, "actor", null, preview.Digest)).State.ShouldBe("active");
        paths = new TwigPaths(paths.TwigDir, paths.ConfigPath, MirrorAdmission.ResolveMirrorPath(paths.TwigDir), root, paths.GlobalDisplayPath);
        if (pin is not null)
        {
            var pinPreview = await _transitions.PreviewAsync(_configuration, paths, pin);
            pinPreview.CanApply.ShouldBeTrue(string.Join('\n', pinPreview.Blockers));
            (await _transitions.ApplyAsync(_configuration, paths, pin, pinPreview.Digest)).State.ShouldBe("completed");
        }
        return paths;
    }
    private async Task<CapturedCommand> InvokeDefaultAsync(IConnectionDefaultTransitionService service, ServiceProvider runtime, string? binding, string? digest = null)
        => await CaptureAsync(() => new ConnectionDefaultCommand(service, _configuration, CurrentPaths(),
            runtime.GetRequiredService<OutputFormatterFactory>(), runtime.GetRequiredService<RendererFactory>()).ExecuteAsync(binding, digest, "json"));
    private static string DefaultMarkerPath(TwigPaths paths) => Path.Combine(paths.TwigDir, "cache", MirrorAdmission.MarkerFile);
    private static async Task<string> DefaultProposalAsync(TwigPaths paths, string name, string operation, int itemId, string title)
    {
        var file = Path.Combine(paths.TwigDir, name + ".json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { version = 1,
            workspace = new { organization = Organization, project = Project }, operations = new[] { new {
                id = operation, kind = "batch", workItemId = itemId, expectedRevision = 1,
                fields = new Dictionary<string, string> { ["System.Title"] = title } } } }));
        return file;
    }
}
