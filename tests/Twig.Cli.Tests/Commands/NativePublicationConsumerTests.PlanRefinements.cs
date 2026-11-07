using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeWholePlanSameScopePartialPrefixDeliversOnlyVerifiedCaptureAndNeverReplaysIt(bool uncertain)
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
            foreach (var id in new[] { "42", "43" })
            {
                var form = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, id))).Value;
                (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Same-scope effect " + id)))).IsSuccess.ShouldBeTrue();
            }
        status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        status.Sources.Length.ShouldBe(1);
        var original = (await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default)).Value;
        original.Operations.Length.ShouldBe(2);
        _transport.AfterCommit = () =>
        {
            _transport.RejectNextPatchStatus = uncertain ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.Conflict;
            return Task.CompletedTask;
        };
        (await original.ApplyAsync(original.Digest)).Value.Status.ShouldBe("PartialProtected");
        var partial = (await original.ContinueDeliveryAsync(original.Digest)).Value;
        partial.Status.ShouldBe("PartialProtected", partial.Error?.Message);
        partial.VerifiedOperations.ShouldBe(1);
        HumanPatchAttempts.ShouldBe(2);
        _transport.CommittedWrites.ShouldBe(1);
        var scope = (await user.ReadPlanAsync(host.Bench)).Value.Scopes.Single();
        var store = (await Twig.Store.Sqlite.SqliteStore.OpenAsync(scope.DatabasePath, default, scope.StoreInstance)).Value;
        var snapshot = (await store.ReadAsync(host.Stamp.Connection, default)).Value;
        snapshot.Drafts.Single().Key.ShouldBe(original.Operations[1].Item);
        (await user.ReadEditorCapturesAsync(default)).Value.Count(capture => capture.Settled).ShouldBe(1);
        var fresh = await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default);
        if (uncertain) fresh.IsSuccess.ShouldBeFalse();
        else
        {
            fresh.IsSuccess.ShouldBeTrue(fresh.IsSuccess ? "" : fresh.Error.Message);
            fresh.Value.Operations.Single().Item.ShouldBe(original.Operations[1].Item);
            (await fresh.Value.ApplyAsync(fresh.Value.Digest)).Value.Status.ShouldBe("Complete");
            HumanPatchAttempts.ShouldBe(3);
            _transport.CommittedWrites.ShouldBe(2);
        }
        (await original.ApplyAsync(original.Digest)).Value.Status.ShouldNotBe("Complete");
        HumanPatchAttempts.ShouldBe(uncertain ? 2 : 3);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NativeWholePlanVerifiedEffectsWithFailedLocalCleanupSettleExactlyWithoutDispatch(bool seed, bool newerIntent)
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        ItemKey key;
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var form = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42"))).Value;
            var stateField = form.StateField;
            var initialState = form.Values[stateField];
            if (seed) form = (await editor.CreateSeedAsync(form.Type)).Value;
            key = form.Item;
            var values = FieldValues.Empty.Set("System.Title", FieldValue.String("Verified original cleanup"));
            if (seed) values = values.Set(stateField, initialState);
            (await editor.StageAsync(form.Ticket, values)).IsSuccess.ShouldBeTrue();
        }
        var scope = (await user.ReadPlanAsync(host.Bench)).Value.Scopes.Single();
        var store = (await Twig.Store.Sqlite.SqliteStore.OpenAsync(scope.DatabasePath, default, scope.StoreInstance)).Value;
        (await store.ReadAsync(host.Stamp.Connection, default)).Value.Drafts.Length.ShouldBe(1);
        await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = scope.DatabasePath, Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT json_array_length(document,'$.Drafts') FROM core_snapshots;";
            Convert.ToInt32(await command.ExecuteScalarAsync()).ShouldBe(1, "The actual persisted Core draft vector must match the cleanup fault condition.");
            command.CommandText = """
                CREATE TRIGGER fixture_plan_cleanup_refusal BEFORE UPDATE OF document ON core_snapshots
                WHEN json_array_length(NEW.document,'$.Drafts')=0 AND json_array_length(OLD.document,'$.Drafts')>0
                BEGIN SELECT RAISE(ABORT,'Fixture exact local settlement refusal'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var review = (await NativePlanReview.ReviewAsync(host.Root, host.Bench,
            (await user.ReadPlanStatusAsync(host.Bench)).Value.Sources, ConfigureHumanEditor, default)).Value;
        var applied = (await review.ApplyAsync(review.Digest)).Value;
        applied.Status.ShouldBe("VerifiedDeliveryPending", applied.Error?.Message);
        applied.Error.ShouldNotBeNull().Message.ShouldContain("Fixture exact local settlement refusal");
        _transport.CommittedWrites.ShouldBe(1);
        var snapshot = (await store.ReadAsync(host.Stamp.Connection, default)).Value;
        snapshot.Journal.Single().Status.ShouldBe(OpStatus.Verified);
        snapshot.Drafts.Single().Key.ShouldBe(key);
        if (newerIntent)
        {
            var draft = snapshot.Drafts.Single();
            (await store.CommitAsync(host.Stamp.Connection, snapshot.Version, new(snapshot.Version + 1,
                snapshot.Items, snapshot.Relationships,
                [new(draft.Key, draft.Type, draft.Baseline, draft.Changes.Set("System.Title", FieldValue.String("Newer independent intent")),
                    draft.Parent, draft.ParentRelation, draft.Resource, draft.Links)],
                snapshot.Proposals, snapshot.Journal, snapshot.Mappings, snapshot.Benches), default)).IsSuccess.ShouldBeTrue();
        }
        await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = scope.DatabasePath, Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = "DROP TRIGGER fixture_plan_cleanup_refusal;";
            await command.ExecuteNonQueryAsync();
        }
        var reopened = (await NativePlanReview.OpenAsync(host.Root, review.Digest, ConfigureHumanEditor, default)).Value;
        var delivered = (await reopened.ContinueDeliveryAsync(reopened.Digest)).Value;
        delivered.Status.ShouldBe(newerIntent ? "VerifiedDeliveryPending" : "Complete", delivered.Error?.Message);
        _transport.CommittedWrites.ShouldBe(1);
        snapshot = (await store.ReadAsync(host.Stamp.Connection, default)).Value;
        if (newerIntent) snapshot.Drafts.Single().Changes["System.Title"].Text.ShouldBe("Newer independent intent");
        else snapshot.Drafts.ShouldBeEmpty();
        if (seed) snapshot.Mappings.Single().Local.ShouldBe(key);
        (await reopened.ContinueDeliveryAsync(reopened.Digest)).Value.Status.ShouldBe(newerIntent ? "VerifiedDeliveryPending" : "Complete");
        _transport.CommittedWrites.ShouldBe(1);
    }

    [Fact]
    public async Task NativeWholePlanDefinitivelyRefusedCreationUuidIsNeverRecreated()
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var existing = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42"))).Value;
            var seed = (await editor.CreateSeedAsync(existing.Type)).Value;
            (await editor.StageAsync(seed.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Refused permanent seed"))
                .Set(existing.StateField, existing.Values[existing.StateField]))).IsSuccess.ShouldBeTrue();
        }
        status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        var review = (await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default)).Value;
        _transport.RejectNextCreateStatus = System.Net.HttpStatusCode.Forbidden;
        (await review.ApplyAsync(review.Digest)).Value.Status.ShouldBe("Protected");
        _transport.PostCount.ShouldBe(1);
        _transport.CommittedWrites.ShouldBe(0);
        (await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default)).IsSuccess.ShouldBeFalse();
        (await review.ContinueDeliveryAsync(review.Digest)).Value.Status.ShouldBe("Protected");
        _transport.PostCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(409)]
    [InlineData(403)]
    public async Task NativeWholePlanDefinitiveNoEffectEditRequiresNewReviewAndFreshRestoredAccess(int statusCode)
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var form = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42"))).Value;
            (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("New review after definitive refusal")))).IsSuccess.ShouldBeTrue();
        }
        status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        var original = (await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default)).Value;
        _transport.RejectNextPatchStatus = (System.Net.HttpStatusCode)statusCode;
        (await original.ApplyAsync(original.Digest)).Value.Status.ShouldBe("Protected");
        HumanPatchAttempts.ShouldBe(1);
        _transport.CommittedWrites.ShouldBe(0);
        _transport.FailSchema = true;
        _transport.SchemaFailureStatus = System.Net.HttpStatusCode.Forbidden;
        (await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default)).IsSuccess.ShouldBeFalse();
        HumanPatchAttempts.ShouldBe(1);
        _transport.FailSchema = false;
        _transport.SchemaFailureStatus = null;
        var fresh = await NativePlanReview.ReviewAsync(host.Root, host.Bench, status.Sources, ConfigureHumanEditor, default);
        fresh.IsSuccess.ShouldBeTrue(fresh.IsSuccess ? "" : fresh.Error.Message);
        fresh.Value.Digest.ShouldNotBe(original.Digest);
        (await fresh.Value.ApplyAsync(fresh.Value.Digest)).Value.Status.ShouldBe("Complete");
        HumanPatchAttempts.ShouldBe(2);
        _transport.CommittedWrites.ShouldBe(1);
        (await original.ApplyAsync(original.Digest)).Value.Status.ShouldNotBe("Complete");
        HumanPatchAttempts.ShouldBe(2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NativeWholePlanReviewDiscoversMetadataOncePerExactScope(int itemCount)
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
            foreach (var id in new[] { "42", "43" }.Take(itemCount))
            {
                var form = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, id))).Value;
                (await editor.StageAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Metadata count " + id)))).IsSuccess.ShouldBeTrue();
            }
        _transport.ObservedPaths.ShouldContain(path => path.EndsWith("/_apis/wit/fields", StringComparison.Ordinal));
        _transport.ObservedPaths.Clear();
        _transport.ItemReadIds.Clear();
        var reviewed = await NativePlanReview.ReviewAsync(host.Root, host.Bench,
            (await user.ReadPlanStatusAsync(host.Bench)).Value.Sources, ConfigureHumanEditor, default);
        reviewed.IsSuccess.ShouldBeTrue(reviewed.IsSuccess ? "" : reviewed.Error.Message);
        reviewed.Value.Operations.Length.ShouldBe(itemCount);
        _transport.ItemReadIds.Distinct().Count().ShouldBe(itemCount, "Every exact item baseline still needs a real native read.");
        var metadata = _transport.ObservedPaths.Count(path => path.EndsWith("/_apis/wit/fields", StringComparison.Ordinal));
        metadata.ShouldBe(1, $"Review metadata discovery must be constant per exact scope, not per {itemCount} items.");
        HumanPatchAttempts.ShouldBe(0);
    }
}
