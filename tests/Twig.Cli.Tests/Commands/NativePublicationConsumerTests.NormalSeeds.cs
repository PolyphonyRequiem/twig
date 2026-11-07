using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task NativeNormalSeedLifecycleUsesOneOwnedUuidAndVerifiedRemoteBenchPin()
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var before = (await user.ReadAsync()).Value;
        var observation = (await ReadHumanHostAsync(host.Root, host.Stamp, true, default)).Value;
        observation.Schema!.IsSuccess.ShouldBeTrue();
        var type = observation.Schema.Value.Types.Single(value => value.Reference == "Fixture.Task");
        _transport.PostCount.ShouldBe(0);
        ItemKey seed;
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            editor.PlanMode.ShouldBeFalse();
            editor.StoreInstance.ShouldBe(host.Stamp.StoreInstance);
            var created = await editor.CreateSeedAsync(type.Reference);
            created.IsSuccess.ShouldBeTrue(created.IsSuccess ? "" : created.Error.Message);
            seed = created.Value.Item;
            seed.IsLocal.ShouldBeTrue();
            seed.LocalId.Version.ShouldBe(7);
            created.Value.Values.Count.ShouldBe(0, "Creation must not copy a current item's fields.");
            created.Value.Evaluation.Errors.ShouldNotBeEmpty();
            (await editor.SaveAsync(created.Value.Ticket, FieldValues.Empty)).IsSuccess.ShouldBeFalse();
            _transport.PostCount.ShouldBe(0, "Unmet connected creation requiredness must refuse before POST.");
            var staged = await editor.StageAsync(created.Value.Ticket,
                FieldValues.Empty.Set("System.Title", FieldValue.String("Explicit native seed"))
                    .Set(type.StateField, FieldValue.String(type.States.Single(value => value.Category == StateCategory.Proposed).Name)));
            staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
            (await user.ReadAsync()).Value.Benches.Single().Selectors.ShouldBe(before.Benches.Single().Selectors,
                "An owned temporary seed must never become a permanent local-UUID Bench pin.");
            _transport.PostCount.ShouldBe(0);
        }
        using (var reopened = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var selected = await reopened.SelectAsync(seed);
            selected.IsSuccess.ShouldBeTrue(selected.IsSuccess ? "" : selected.Error.Message);
            selected.Value.Item.ShouldBe(seed);
            selected.Value.Values["System.Title"].Text.ShouldBe("Explicit native seed");
            var saved = await reopened.SaveAsync(selected.Value.Ticket, FieldValues.Empty);
            saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
            saved.Value.Verified.ShouldBeTrue(saved.Value.Error?.Message);
            saved.Value.DraftRetained.ShouldBeFalse();
        }
        var complete = (await ReadHumanHostAsync(host.Root, host.Stamp, false, default)).Value.Snapshot;
        complete.Drafts.ShouldBeEmpty();
        var mapping = complete.Mappings.ShouldHaveSingleItem();
        mapping.Local.ShouldBe(seed);
        var proposal = complete.Proposals.ShouldHaveSingleItem();
        proposal.Operations.ShouldHaveSingleItem().Item.ShouldBe(seed);
        mapping.ProposalDigest.ShouldBe(proposal.Digest);
        complete.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Verified);
        var bench = (await user.ReadAsync()).Value.Benches.Single();
        bench.Selectors.ShouldContain(selector => selector.Item == mapping.Remote && selector.Connection == host.Stamp.Connection);
        bench.Selectors.ShouldNotContain(selector => selector.Item == seed);
        (await user.ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem().Settled.ShouldBeTrue();
        _transport.PostCount.ShouldBe(1);
        HumanPatchAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("pin")]
    public async Task NativeNormalSeedVerifiedDeliveryReopensOriginalReferenceWithoutAnotherCreate(string failure)
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        NativeSeedPublication reference;
        ItemKey seed;
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var form = (await editor.CreateSeedAsync("Fixture.Task")).Value;
            seed = form.Item;
            var fields = NormalSeedFields(form, "Verified normal seed");
            if (failure == "origin") _transport.FailCurrentReadsAfterCommit = true;
            else _transport.AfterCommit = () => SeedPinFailureAsync(host.Registry, true);
            var result = await editor.SaveAsync(form.Ticket, fields);
            result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
            result.Value.Verified.ShouldBeTrue(result.Value.Error?.Message);
            result.Value.DraftRetained.ShouldBeTrue();
            result.Value.Error.ShouldNotBeNull();
            reference = result.Value.SeedPublication.ShouldNotBeNull();
            reference.Status.ShouldBe("VerifiedDeliveryPending");
            reference.Item.ShouldBe(seed);
            (await editor.ObserveSeedAsync(reference.CaptureId)).Value.Status.ShouldBe("VerifiedDeliveryPending");
        }
        _transport.ReadbacksAfterCommit.ShouldBeGreaterThanOrEqualTo(2, "Real Core verification must precede the local delivery failure.");
        var before = (await ReadHumanHostAsync(host.Root, host.Stamp, false, default)).Value.Snapshot;
        before.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Verified);
        before.Proposals.ShouldHaveSingleItem().Digest.ShouldBe(reference.Digest);
        before.Mappings.ShouldHaveSingleItem().Local.ShouldBe(seed);
        var capture = (await user.ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem();
        capture.Id.ShouldBe(reference.CaptureId);
        capture.Settled.ShouldBeFalse("Delivery failure must not retire the only original reference.");
        var nativeBench = (await user.ReadAsync()).Value.Benches.Single();
        nativeBench.Selectors.ShouldNotContain(selector => selector.Item == seed || selector.Item == before.Mappings.Single().Remote);
        _transport.PostCount.ShouldBe(1);
        _transport.FailCurrentReadsAfterCommit = false;
        if (failure == "pin") await SeedPinFailureAsync(host.Registry, false);
        var extra = new Selector(SelectorKind.Pin, ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "44"),
            connection: host.Stamp.Connection);
        (await user.SaveAsync(new(nativeBench.Id, nativeBench.Name, nativeBench.Selectors.Add(extra)), nativeBench.Revision)).IsSuccess.ShouldBeTrue();
        using var reopened = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var owned = (await reopened.ReadOwnedSeedsAsync()).Value.ShouldHaveSingleItem();
        owned.Item.ShouldBe(seed);
        owned.Draft.ShouldBeNull("Core may settle its draft, but native verified delivery must remain enumerable.");
        owned.Publication.ShouldNotBeNull().CaptureId.ShouldBe(reference.CaptureId);
        (await reopened.SelectAsync(seed)).IsSuccess.ShouldBeFalse();
        (await reopened.ContinueSeedDeliveryAsync(reference.CaptureId, new string('0', 64))).Error.Code.ShouldBe(ErrorCode.DigestMismatch);
        var delivered = await reopened.ContinueSeedDeliveryAsync(reference.CaptureId, reference.Digest);
        delivered.IsSuccess.ShouldBeTrue(delivered.IsSuccess ? "" : delivered.Error.Message);
        delivered.Value.Status.ShouldBe("Complete", delivered.Value.Error?.Message);
        delivered.Value.Error.ShouldBeNull();
        (await reopened.ContinueSeedDeliveryAsync(reference.CaptureId, reference.Digest)).Value.Status.ShouldBe("Complete");
        var after = (await ReadHumanHostAsync(host.Root, host.Stamp, false, default)).Value.Snapshot;
        after.Proposals.ShouldHaveSingleItem().Digest.ShouldBe(reference.Digest);
        after.Journal.Single().Attempt.ShouldBe(before.Journal.Single().Attempt);
        after.Journal.Single().Receipts.Length.ShouldBe(before.Journal.Single().Receipts.Length);
        after.Mappings.Single().ShouldBe(before.Mappings.Single());
        var bench = (await user.ReadAsync()).Value.Benches.Single();
        bench.Selectors.ShouldContain(extra);
        bench.Selectors.Count(selector => selector.Item == after.Mappings.Single().Remote).ShouldBe(1);
        bench.Selectors.ShouldNotContain(selector => selector.Item == seed);
        var settled = (await user.ReadEditorCapturesAsync(default)).Value.Single();
        settled.Settled.ShouldBeTrue();
        settled.SeedProposal.ShouldBe(capture.SeedProposal);
        _transport.PostCount.ShouldBe(1);
        HumanPatchAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task NativeNormalSeedLostResponseProtectsOriginalUuidAndNeverGuessesOrRecreates()
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        NativeSeedPublication reference;
        ItemKey seed;
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var form = (await editor.CreateSeedAsync("Fixture.Task")).Value;
            seed = form.Item;
            _transport.AfterCommit = () => throw new IOException("Lost native creation response after server commit");
            var saved = await editor.SaveAsync(form.Ticket, NormalSeedFields(form, "Unknown original creation"));
            saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
            saved.Value.Verified.ShouldBeFalse();
            saved.Value.DraftRetained.ShouldBeTrue();
            reference = saved.Value.SeedPublication.ShouldNotBeNull();
            reference.Status.ShouldBe("Protected");
            reference.Error.ShouldNotBeNull();
            (await editor.SaveAsync(form.Ticket, FieldValues.Empty)).IsSuccess.ShouldBeFalse();
        }
        var before = (await ReadHumanHostAsync(host.Root, host.Stamp, false, default)).Value.Snapshot;
        before.Drafts.ShouldHaveSingleItem().Key.ShouldBe(seed);
        before.Mappings.ShouldBeEmpty();
        before.Proposals.ShouldHaveSingleItem().Digest.ShouldBe(reference.Digest);
        before.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Indeterminate);
        var capture = (await user.ReadEditorCapturesAsync(default)).Value.Single();
        capture.Settled.ShouldBeFalse();
        _transport.PostCount.ShouldBe(1);
        using var reopened = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var owned = (await reopened.ReadOwnedSeedsAsync()).Value.ShouldHaveSingleItem();
        owned.Item.ShouldBe(seed);
        owned.Publication.ShouldNotBeNull().CaptureId.ShouldBe(reference.CaptureId);
        owned.Draft.ShouldNotBeNull().Key.ShouldBe(seed);
        (await reopened.SelectAsync(seed)).Error.Code.ShouldBe(ErrorCode.Indeterminate);
        (await reopened.ObserveSeedAsync(reference.CaptureId)).Value.Status.ShouldBe("Protected");
        for (var attempt = 0; attempt < 2; attempt++)
            (await reopened.ContinueSeedDeliveryAsync(reference.CaptureId, reference.Digest)).Value.Status.ShouldBe("Protected");
        var after = (await ReadHumanHostAsync(host.Root, host.Stamp, false, default)).Value.Snapshot;
        after.Drafts.Single().Key.ShouldBe(seed);
        after.Proposals.Single().Digest.ShouldBe(reference.Digest);
        after.Journal.Single().Attempt.ShouldBe(before.Journal.Single().Attempt);
        after.Journal.Single().Receipts.Length.ShouldBe(before.Journal.Single().Receipts.Length);
        after.Mappings.ShouldBeEmpty();
        (await user.ReadEditorCapturesAsync(default)).Value.Single().SeedProposal.ShouldBe(capture.SeedProposal);
        (await user.ReadAsync()).Value.Benches.Single().Selectors.ShouldNotContain(selector => selector.Item!.IsLocal);
        _transport.PostCount.ShouldBe(1);
    }

    [Fact]
    public async Task NativeNormalOwnedSeedEnumerationDoesNotAdoptCliPlanOrAnotherBenchDebt()
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        using var services = EditorServices(host.Root);
        using var origin = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var cli = (await origin.Workspace.StageSeedAsync("Fixture.Task", FieldValues.Empty, default)).Value;
        var other = (await user.SaveAsync(new(Guid.NewGuid(), "Another seed Bench", []), 0)).Value;
        ItemKey otherSeed;
        using (var otherEditor = (await OpenHumanHostAsync(host.Root, other.Id, host.Stamp, default)).Value)
            otherSeed = (await otherEditor.CreateSeedAsync("Fixture.Task")).Value.Item;
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        ItemKey planSeed;
        using (var plan = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            plan.StoreInstance.ShouldNotBe(host.Stamp.StoreInstance);
            planSeed = (await plan.CreateSeedAsync("Fixture.Task")).Value.Item;
            (await plan.ReadPlanDraftsAsync()).Value.ShouldHaveSingleItem().Key.ShouldBe(planSeed);
        }
        status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, false)).IsSuccess.ShouldBeTrue();
        using var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var owned = (await editor.CreateSeedAsync("Fixture.Task")).Value;
        var seeds = (await editor.ReadOwnedSeedsAsync()).Value;
        seeds.ShouldHaveSingleItem().Item.ShouldBe(owned.Item);
        seeds.Single().Draft.ShouldNotBeNull().Baseline.ShouldBeNull();
        seeds.Single().Publication.ShouldBeNull();
        (await editor.SelectAsync(cli.Key)).Error.Code.ShouldBe(ErrorCode.Conflict);
        (await editor.SelectAsync(otherSeed)).Error.Code.ShouldBe(ErrorCode.Conflict);
        (await editor.SelectAsync(planSeed)).Error.Code.ShouldBe(ErrorCode.Unsupported);
        (await editor.ReadPlanDraftsAsync()).IsSuccess.ShouldBeFalse();
        _transport.PostCount.ShouldBe(0);
        (await user.ReadAsync()).Value.Benches.SelectMany(bench => bench.Selectors)
            .Any(selector => selector.Item is { IsLocal: true }).ShouldBeFalse();
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("mode")]
    [InlineData("schema")]
    [InlineData("bench")]
    [InlineData("ticket")]
    public async Task NativeNormalSeedFreshAuthoringGuardsRefuseBeforeCreate(string change)
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        using var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var form = (await editor.CreateSeedAsync("Fixture.Task")).Value;
        if (change == "actor") HumanActorDenied = true;
        else if (change == "schema") _transport.CloseGateEnabled = false;
        else if (change == "mode")
        {
            var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
            (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        }
        else if (change == "bench")
        {
            var bench = (await user.ReadAsync()).Value.Benches.Single();
            (await user.SaveAsync(new(bench.Id, "Changed seed Bench", bench.Selectors), bench.Revision)).IsSuccess.ShouldBeTrue();
        }
        var saved = await editor.SaveAsync(change == "ticket" ? Guid.NewGuid() : form.Ticket,
            NormalSeedFields(form, "Guarded creation"));
        (saved.IsSuccess && saved.Value.Verified).ShouldBeFalse();
        _transport.PostCount.ShouldBe(0);
        (await user.ReadEditorCapturesAsync(default)).Value.ShouldAllBe(capture => !capture.Settled);
    }

    private static FieldValues NormalSeedFields(NativeEditForm form, string title)
    {
        form.Evaluation.States.ShouldContain("To Do", "The fixture's discovery must expose the creation state, not copy a current item.");
        return FieldValues.Empty.Set("System.Title", FieldValue.String(title))
            .Set(form.StateField, FieldValue.String("To Do"));
    }

    [Fact]
    public async Task NativeNormalSeedCompletionRequirednessUsesCreationNotRemoteTransition()
    {
        var host = await CreateHumanHostAsync();
        _transport.CloseGateEnabled.ShouldBeTrue();
        using var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var created = (await editor.CreateSeedAsync("Fixture.Task")).Value;
        created.Values.Count.ShouldBe(0);
        var fields = FieldValues.Empty.Set("System.Title", FieldValue.String("Incomplete completion seed"))
            .Set(created.StateField, FieldValue.String("Done"));
        var evaluated = (await editor.EvaluateAsync(created.Ticket, fields)).Value;
        evaluated.Evaluation.Fields.Single(value => value.Definition.Reference == "Fixture.CloseEvidence").Required.ShouldBeTrue();
        evaluated.Evaluation.Errors.ShouldNotBeEmpty();
        var staged = await editor.StageAsync(created.Ticket, fields);
        staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
        var owned = (await editor.ReadOwnedSeedsAsync()).Value.ShouldHaveSingleItem();
        owned.Draft.ShouldNotBeNull().Baseline.ShouldBeNull("Creation has no observed remote pre-state or remote transition.");
        (await editor.SaveAsync(created.Ticket, FieldValues.Empty)).IsSuccess.ShouldBeFalse();
        _transport.PostCount.ShouldBe(0);
    }

    private static async Task SeedPinFailureAsync(string registry, bool fail)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(Path.GetDirectoryName(registry)!, "benches.db"), Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = fail
            ? "CREATE TRIGGER normal_seed_pin_failure BEFORE UPDATE ON benches BEGIN SELECT RAISE(FAIL,'Fixture pin delivery unavailable'); END;"
            : "DROP TRIGGER normal_seed_pin_failure;";
        await command.ExecuteNonQueryAsync();
    }
}
