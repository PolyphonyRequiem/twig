using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Cli.Tests.TestSupport;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeWholePlanConvergedNoEffectSettlesAfterRestartWithoutInventingRemoteReceipt(bool coreSettledBeforeLoss)
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var form = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42"))).Value;
            (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set("System.Title", form.Values["System.Title"]))).IsSuccess.ShouldBeTrue();
        }
        status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        var review = (await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default)).Value;
        review.Operations.ShouldBeEmpty();
        review.CoreDigests.ShouldBeEmpty();
        var reference = (await user.ReadPlanReviewAsync(review.Digest, default)).Value;
        (await user.AdvancePlanReviewAsync(reference, true, 0, 1, default)).IsSuccess.ShouldBeTrue();
        if (coreSettledBeforeLoss)
        {
            using var services = EditorServices(host.Root);
            using var editor = (await NativeEditor.OpenAsync(services.GetRequiredService<CoreWorkspaceFactory>(), user, host.Bench, host.Stamp, default)).Value;
            var draft = (await editor.Workspace.SnapshotAsync(default)).Value.Drafts.Single();
            (await editor.Workspace.SaveDraftSetAsync([new(draft, null)], null, null, default)).IsSuccess.ShouldBeTrue();
        }
        var restarted = (await NativePlanReview.OpenAsync(host.Root, review.Digest, ConfigureHumanEditor, default)).Value;
        var delivered = await restarted.ContinueDeliveryAsync(restarted.Digest);
        delivered.IsSuccess.ShouldBeTrue(delivered.IsSuccess ? "" : delivered.Error.Message);
        delivered.Value.Status.ShouldBe("Complete", delivered.Value.Error?.Message);
        delivered.Value.VerifiedScopes.ShouldBe(0);
        delivered.Value.VerifiedOperations.ShouldBe(0);
        HumanPatchAttempts.ShouldBe(0);
        using var reopened = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        (await reopened.ReadPlanDraftsAsync()).Value.ShouldBeEmpty();
        (await user.ReadEditorCapturesAsync(default)).Value.ShouldAllBe(capture => capture.Settled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task NativeWholePlanAdmissionWithoutAnyCoreAttemptCanBeFreshlyReviewed(int admittedScopes)
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var form = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42"))).Value;
            (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Fresh reviewed after interrupted admission")))).IsSuccess.ShouldBeTrue();
        }
        status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        var review = (await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default)).Value;
        var reference = (await user.ReadPlanReviewAsync(review.Digest, default)).Value;
        (await user.AdvancePlanReviewAsync(reference, true, 0, admittedScopes, default)).IsSuccess.ShouldBeTrue();
        (await user.ReadPlanReviewAsync(review.Digest, default)).Value.Admitted.ShouldBe(admittedScopes);
        HumanPatchAttempts.ShouldBe(0);
        (await review.ApplyAsync(review.Digest)).Value.Status.ShouldNotBe("Complete");
        var fresh = await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default);
        fresh.IsSuccess.ShouldBeTrue(fresh.IsSuccess ? "" : fresh.Error.Message);
        (await fresh.Value.ApplyAsync(fresh.Value.Digest)).Value.Status.ShouldBe("Complete");
        HumanPatchAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task NativeWholePlanCompletedScopeCanPublishNewIntentToSameItemWithoutReplayingOldEffect()
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        var digests = new HashSet<string>();
        for (var index = 0; index < 2; index++)
        {
            using var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
            var form = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42"))).Value;
            (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Distinct Plan effect " + index)))).IsSuccess.ShouldBeTrue();
            var reviewed = await NativePlanReview.ReviewAsync(host.Root, host.Bench,
                (await user.ReadPlanStatusAsync(host.Bench)).Value.Sources, ConfigureHumanEditor, default);
            reviewed.IsSuccess.ShouldBeTrue(reviewed.IsSuccess ? "" : reviewed.Error.Message);
            digests.Add(reviewed.Value.Digest).ShouldBeTrue();
            (await reviewed.Value.ApplyAsync(reviewed.Value.Digest)).Value.Status.ShouldBe("Complete");
        }
        HumanPatchAttempts.ShouldBe(2);
        _transport.CommittedWrites.ShouldBe(2);
        _transport.TitleOf(42).ShouldBe("Distinct Plan effect 1");
    }

    [Fact]
    public async Task NativeWholePlanLastScopeDriftStopsBothScopesBeforeDispatch()
    {
        var fixture = await TwoScopePlanAsync();
        var user = UserBenchStore.ForRegistry(fixture.Registry);
        var status = (await user.ReadPlanStatusAsync(fixture.Bench)).Value;
        status.Sources.Length.ShouldBe(2);
        status.Sources.Select(source => source.Source.Connection).Distinct().Count().ShouldBe(2);
        var reviewed = await NativePlanReview.ReviewAsync(fixture.Root, fixture.Bench, status.Sources, ConfigureHumanEditor, default);
        reviewed.IsSuccess.ShouldBeTrue(reviewed.IsSuccess ? "" : reviewed.Error.Message);
        reviewed.Value.CoreDigests.Length.ShouldBe(2);
        var last = status.Sources.OrderBy(source => source.Id).Last();
        var key = last.Source == fixture.Second ? fixture.SecondKey : fixture.FirstKey;
        using (var editor = (await OpenHumanHostAsync(last.OriginRoot, fixture.Bench, last.Source, default)).Value)
        {
            var form = (await editor.SelectAsync(key)).Value;
            (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Later scope changed")))).IsSuccess.ShouldBeTrue();
        }
        (await reviewed.Value.ApplyAsync(reviewed.Value.Digest)).IsSuccess.ShouldBeFalse();
        HumanPatchAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task NativeWholePlanTwoScopesProtectVerifiedPrefixAndNeverReplayAfterRestart()
    {
        var fixture = await TwoScopePlanAsync();
        var user = UserBenchStore.ForRegistry(fixture.Registry);
        var status = (await user.ReadPlanStatusAsync(fixture.Bench)).Value;
        status.Sources.Length.ShouldBe(2);
        var reviewed = await NativePlanReview.ReviewAsync(fixture.Root, fixture.Bench, status.Sources, ConfigureHumanEditor, default);
        reviewed.IsSuccess.ShouldBeTrue(reviewed.IsSuccess ? "" : reviewed.Error.Message);
        _transport.AfterCommit = () =>
        {
            _transport.RejectNextPatchStatus = System.Net.HttpStatusCode.ServiceUnavailable;
            return Task.CompletedTask;
        };
        var applied = await reviewed.Value.ApplyAsync(reviewed.Value.Digest);
        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? "" : applied.Error.Message);
        applied.Value.Status.ShouldBe("PartialProtected", applied.Value.Error?.Message);
        applied.Value.VerifiedScopes.ShouldBe(1);
        applied.Value.DeliveredScopes.ShouldBe(1);
        applied.Value.VerifiedOperations.ShouldBe(1);
        HumanPatchAttempts.ShouldBe(2);
        _transport.CommittedWrites.ShouldBe(1);
        var reopened = (await NativePlanReview.OpenAsync(fixture.Root, reviewed.Value.Digest, ConfigureHumanEditor, default)).Value;
        (await reopened.ObserveAsync()).Value.Status.ShouldBe("PartialProtected");
        (await reopened.ApplyAsync(reopened.Digest)).Value.Status.ShouldBe("PartialProtected");
        (await reopened.ContinueDeliveryAsync(reopened.Digest)).Value.Status.ShouldBe("PartialProtected");
        HumanPatchAttempts.ShouldBe(2);
        _transport.CommittedWrites.ShouldBe(1);
    }

    [Fact]
    public async Task NativeWholePlanVerifiedPrefixDoesNotBrickProvenUntouchedLaterScope()
    {
        var fixture = await TwoScopePlanAsync();
        var user = UserBenchStore.ForRegistry(fixture.Registry);
        var status = (await user.ReadPlanStatusAsync(fixture.Bench)).Value;
        var reviewed = (await NativePlanReview.ReviewAsync(fixture.Root, fixture.Bench, status.Sources, ConfigureHumanEditor, default)).Value;
        _transport.CloseGateEnabled.ShouldBeTrue();
        _transport.BeforeReadback = () => { _transport.CloseGateEnabled = false; return Task.CompletedTask; };
        var interrupted = (await reviewed.ApplyAsync(reviewed.Digest)).Value;
        interrupted.Status.ShouldBe("PartialProtected", interrupted.Error?.Message);
        interrupted.DeliveredScopes.ShouldBe(1);
        interrupted.VerifiedOperations.ShouldBe(1);
        (await user.ReadPlanReviewAsync(reviewed.Digest, default)).Value.Admitted.ShouldBe(1);
        HumanPatchAttempts.ShouldBe(1);
        _transport.CloseGateEnabled = true;
        var remaining = await NativePlanReview.ReviewAsync(fixture.Root, fixture.Bench,
            (await user.ReadPlanStatusAsync(fixture.Bench)).Value.Sources, ConfigureHumanEditor, default);
        remaining.IsSuccess.ShouldBeTrue(remaining.IsSuccess ? "" : remaining.Error.Message);
        remaining.Value.CoreDigests.Length.ShouldBe(1);
        (await remaining.Value.ApplyAsync(remaining.Value.Digest)).Value.Status.ShouldBe("Complete");
        HumanPatchAttempts.ShouldBe(2);
        _transport.CommittedWrites.ShouldBe(2);
    }

    private async Task<(string Root, string Registry, Guid Bench, NativeSourceStamp Second, ItemKey FirstKey, ItemKey SecondKey)> TwoScopePlanAsync()
    {
        var first = await CreateHumanHostAsync();
        var root = Path.Combine(_temp, "second-plan-root");
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        var config = new TwigConfiguration { Organization = "fixture-two", Project = "Project" };
        var paths = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), config, root);
        await config.SaveSplitAsync(paths);
        using (var attachment = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        var anchor = WorktreeAnchorDetector.Detect(root)!.Value;
        (await _systemRegistry.UpsertConnectionAsync(ConnectionRefResolver.Compute(config), config.Organization, config.Project, null))
            .IsSuccess.ShouldBeTrue();
        (await _systemRegistry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor),
            ConnectionRefResolver.Compute(config), root)).IsSuccess.ShouldBeTrue();
        await _bindings.RegisterPatIdentityAsync("actor-two", config.Organization, ActorPat);
        await _bindings.CreateBindingAsync(config.Organization, config.Project, "actor-two", true);
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new Connection(first.Stamp.Connection.Provider, first.Stamp.Connection.ResourceScope,
            first.Stamp.Connection.Destination, CleanCoreAdoAccess.Identity(origin, Actor));
        connection.ShouldNotBe(first.Stamp.Connection);
        var database = Path.Combine(paths.TwigDir, "core.db");
        var store = (await Twig.Store.Sqlite.SqliteStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        await ActivateNativeSafetyFixtureAsync(paths);
        using var services = EditorServices(root);
        using var second = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var firstKey = ItemKey.Remote(first.Stamp.Connection.Provider, first.Stamp.Connection.ResourceScope, "42");
        var secondKey = ItemKey.Remote(connection.Provider, connection.ResourceScope, "43");
        var user = UserBenchStore.ForRegistry(first.Registry);
        var bench = (await user.ReadAsync()).Value.Benches.Single();
        (await user.SaveAsync(new(bench.Id, bench.Name, bench.Selectors.Append(new(SelectorKind.Pin, secondKey, connection: connection))),
            bench.Revision)).IsSuccess.ShouldBeTrue();
        var status = (await user.ReadPlanStatusAsync(first.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, first.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        foreach (var source in new[] { (Root: first.Root, Stamp: first.Stamp, Key: firstKey), (Root: root, Stamp: second.Source, Key: secondKey) })
        {
            using var editor = (await OpenHumanHostAsync(source.Root, first.Bench, source.Stamp, default)).Value;
            var form = (await editor.SelectAsync(source.Key)).Value;
            (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Plan source " + source.Key.NativeId))))
                .IsSuccess.ShouldBeTrue();
        }
        HumanPatchAttempts.ShouldBe(0);
        return (first.Root, first.Registry, first.Bench, second.Source, firstKey, secondKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeWholePlanOriginalReceiptsFenceReplayAndAllowDeliveryOnly(bool uncertain)
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
            var form = (await editor.SelectAsync(key)).Value;
            (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Protected original effect")))).IsSuccess.ShouldBeTrue();
        }
        status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        var review = await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default);
        review.IsSuccess.ShouldBeTrue(review.IsSuccess ? "" : review.Error.Message);
        _transport.FailCurrentReadsAfterCommit = !uncertain;
        _transport.FailReadbackAfterCommit = uncertain;
        var applied = await review.Value.ApplyAsync(review.Value.Digest);
        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? "" : applied.Error.Message);
        applied.Value.Status.ShouldBe(uncertain ? "Protected" : "VerifiedDeliveryPending", applied.Value.Error?.Message);
        applied.Value.DeliveredScopes.ShouldBe(0);
        if (!uncertain) _transport.ReadbacksAfterCommit.ShouldBe(2, "Both actual Core verification reads must finish before the failed origin delivery.");
        HumanPatchAttempts.ShouldBe(1);
        var reopened = (await NativePlanReview.OpenAsync(host.Root, review.Value.Digest, ConfigureHumanEditor, default)).Value;
        (await reopened.ObserveAsync()).Value.Status.ShouldBe(uncertain ? "Protected" : "VerifiedDeliveryPending");
        (await reopened.ApplyAsync(reopened.Digest)).Value.Status.ShouldNotBe("Complete");
        _transport.FailCurrentReadsAfterCommit = _transport.FailReadbackAfterCommit = false;
        var continued = await reopened.ContinueDeliveryAsync(reopened.Digest);
        continued.IsSuccess.ShouldBeTrue(continued.IsSuccess ? "" : continued.Error.Message);
        continued.Value.Status.ShouldBe(uncertain ? "Protected" : "Complete", continued.Value.Error?.Message);
        HumanPatchAttempts.ShouldBe(1);
        (await reopened.ContinueDeliveryAsync(reopened.Digest)).Value.Status.ShouldBe(uncertain ? "Protected" : "Complete");
        HumanPatchAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task NativeWholePlanSeedDeliveryKeepsItsOwnedUuidAndAddsOnlyVerifiedRemotePin()
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        ItemKey seed;
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var existing = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42"))).Value;
            var created = await editor.CreateSeedAsync(existing.Type);
            created.IsSuccess.ShouldBeTrue(created.IsSuccess ? "" : created.Error.Message);
            seed = created.Value.Item;
            (await editor.EvaluateAsync(created.Value.Ticket, FieldValues.Empty)).Value.Evaluation.Errors.ShouldNotBeEmpty(
                "Native creation requiredness must not be bypassed by the local-seed close-gate branch.");
            var incomplete = await NativePlanReview.ReviewAsync(host.Root, host.Bench,
                (await user.ReadPlanStatusAsync(host.Bench)).Value.Sources, ConfigureHumanEditor, default);
            incomplete.IsSuccess.ShouldBeFalse();
            _transport.PostCount.ShouldBe(0);
            (await editor.StageAsync(created.Value.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Owned new seed"))
                .Set(existing.StateField, existing.Values[existing.StateField]))).IsSuccess.ShouldBeTrue();
        }
        status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        var reviewed = await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default);
        reviewed.IsSuccess.ShouldBeTrue(reviewed.IsSuccess ? "" : reviewed.Error.Message);
        _transport.PostCount.ShouldBe(0);
        var result = await reviewed.Value.ApplyAsync(reviewed.Value.Digest);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.Status.ShouldBe("Complete", result.Value.Error?.Message);
        _transport.PostCount.ShouldBe(1);
        var scope = (await user.ReadPlanAsync(host.Bench)).Value.Scopes.Single();
        var store = (await Twig.Store.Sqlite.SqliteStore.OpenAsync(scope.DatabasePath, default, scope.StoreInstance)).Value;
        var snapshot = (await store.ReadAsync(host.Stamp.Connection, default)).Value;
        var mapping = snapshot.Mappings.Single();
        mapping.Local.ShouldBe(seed);
        (await user.ReadPlanSeedOwnerAsync(seed.LocalId)).Value.ScopeId.ShouldBe(scope.Id);
        (await user.ReadAsync()).Value.Benches.Single().Selectors.ShouldContain(selector => selector.Item == mapping.Remote);
        (await reviewed.Value.ApplyAsync(reviewed.Value.Digest)).Value.Status.ShouldBe("Complete");
        _transport.PostCount.ShouldBe(1);
    }

    [Fact]
    public async Task NativeWholePlanRetainsExactCoreDigestsAndDeliversWithoutFlushingNormalIntent()
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        using var services = EditorServices(host.Root);
        using var origin = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var normalKey = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        (await origin.Workspace.StageEditAsync(normalKey,
            FieldValues.Empty.Set("System.Title", FieldValue.String("Normal remains local")), default)).IsSuccess.ShouldBeTrue();
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
            foreach (var id in new[] { "42", "43" })
            {
                var form = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, id))).Value;
                (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Plan " + id)))).IsSuccess.ShouldBeTrue();
            }
        status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        var reviewed = await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default);
        reviewed.IsSuccess.ShouldBeTrue(reviewed.IsSuccess ? "" : reviewed.Error.Message);
        reviewed.Value.Operations.Length.ShouldBe(2);
        reviewed.Value.CoreDigests.Length.ShouldBe(1);
        HumanPatchAttempts.ShouldBe(0);
        (await reviewed.Value.ApplyAsync(new string('0', 64))).Error.Code.ShouldBe(ErrorCode.DigestMismatch);
        HumanPatchAttempts.ShouldBe(0);
        var reopened = await NativePlanReview.OpenAsync(host.Root, reviewed.Value.Digest, ConfigureHumanEditor, default);
        reopened.IsSuccess.ShouldBeTrue(reopened.IsSuccess ? "" : reopened.Error.Message);
        reopened.Value.CoreDigests.ShouldBe(reviewed.Value.CoreDigests);
        var applied = await reopened.Value.ApplyAsync(reviewed.Value.Digest);
        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? "" : applied.Error.Message);
        applied.Value.Status.ShouldBe("Complete", applied.Value.Error?.Message);
        applied.Value.VerifiedScopes.ShouldBe(1);
        applied.Value.DeliveredScopes.ShouldBe(1);
        HumanPatchAttempts.ShouldBe(2);
        var repeated = await reopened.Value.ApplyAsync(reviewed.Value.Digest);
        repeated.IsSuccess.ShouldBeTrue();
        HumanPatchAttempts.ShouldBe(2);
        (await user.ReadEditorCapturesAsync(default)).Value.ShouldAllBe(capture => capture.Settled);
        var snapshot = (await origin.Workspace.SnapshotAsync(default)).Value;
        snapshot.Items.Single(value => value.Key.NativeId == "42").Fields["System.Title"].Text.ShouldBe("Plan 42");
        snapshot.Journal.ShouldBeEmpty();
        snapshot.Drafts.Single().Changes["System.Title"].Text.ShouldBe("Normal remains local");
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("draft")]
    [InlineData("schema")]
    [InlineData("actor")]
    public async Task NativeWholePlanStaleReviewRefusesBeforeFirstDispatch(string change)
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        using var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var form = (await editor.SelectAsync(key)).Value;
        (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Reviewed")))).IsSuccess.ShouldBeTrue();
        status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        var review = await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default);
        review.IsSuccess.ShouldBeTrue(review.IsSuccess ? "" : review.Error.Message);
        if (change == "mode")
            (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, false)).IsSuccess.ShouldBeTrue();
        else if (change == "draft")
        {
            form = (await editor.SelectAsync(key)).Value;
            (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Newer intent")))).IsSuccess.ShouldBeTrue();
        }
        else if (change == "schema") _transport.CloseGateEnabled = !_transport.CloseGateEnabled;
        else HumanActorDenied = true;
        var applied = await review.Value.ApplyAsync(review.Value.Digest);
        applied.IsSuccess.ShouldBeFalse();
        HumanPatchAttempts.ShouldBe(0);
    }
}
