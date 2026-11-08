using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task NativeFreshAcquisitionPreservesFullOutgoingTitleDebtAndRemoval()
    {
        var host = await CreateHumanHostAsync();
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43");
        using var runtime = EditorServices(host.Root);
        using var handle = (await runtime.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var user = UserBenchStore.ForRegistry(host.Registry);
        var schema = (await handle.Workspace.SchemaAsync(default)).Value;
        var type = schema.Types.Single(value => value.Reference == "Fixture.Task");
        var doing = type.States.Single(state => state.Category == StateCategory.InProgress).Name;
        var done = type.States.Single(state => state.Category == StateCategory.Completed).Name;
        var closeGate = type.Rules.Single(rule => rule.Action == RuleAction.Required
            && rule.Conditions.Any(condition => condition.Field == type.StateField && condition.Value!.Text == done)).Field;
        type.Transitions.ShouldContain(new Transition(doing, done));
        _transport.SetState(42, doing);
        var targetRevision = _transport.RevisionOf(42);
        var endpointRevision = _transport.RevisionOf(43);
        var originalTitle = _transport.TitleOf(42);
        var add = new CreationLink(other, "Native.DependencyForward", true);
        var remove = new CreationLink(other, add.Relation, true, true);
        NativeEditorCapture captured;
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            editor.PlanMode.ShouldBeFalse();
            var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
            form.Target.RelationshipsComplete.ShouldBeTrue();
            form.Endpoint.RelationshipsComplete.ShouldBeTrue();
            form.Incident.ShouldBeEmpty();
            form.Choices.ShouldContain(add);
            var before = (await handle.Workspace.SnapshotAsync(default)).Value;
            (await user.ReadEditorCapturesAsync(default)).Value.ShouldBeEmpty();
            var evaluated = await editor.EvaluateAsync(form.Fields.Ticket,
                FieldValues.Empty.Set(type.StateField, FieldValue.String(done)));
            evaluated.IsSuccess.ShouldBeTrue();
            evaluated.Value.Evaluation.Fields.Single(field => field.Definition.Reference == closeGate).Required.ShouldBeTrue();
            evaluated.Value.Evaluation.Errors.ShouldNotBeEmpty();
            var after = (await handle.Workspace.SnapshotAsync(default)).Value;
            after.Version.ShouldBe(before.Version);
            after.Drafts.ShouldBeEmpty();
            after.Proposals.ShouldBeEmpty();
            after.Journal.ShouldBeEmpty();
            (await user.ReadEditorCapturesAsync(default)).Value.ShouldBeEmpty();
            (await editor.SaveAsync(form.Fields.Ticket, FieldValues.Empty)).Error.Code.ShouldBe(ErrorCode.Unsupported);
            (await editor.StageRelationshipsAsync(form.Fields.Ticket,
                FieldValues.Empty.Set("System.Title", FieldValue.String("Atomic Dependency True")), [add])).IsSuccess.ShouldBeTrue();
            captured = (await user.ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem();
            captured.Ready.ShouldBeTrue();
            var debt = (await handle.Workspace.SnapshotAsync(default)).Value.Drafts.ShouldHaveSingleItem();
            debt.Changes["System.Title"].Text.ShouldBe("Atomic Dependency True");
            debt.Links.ShouldBeEmpty();
            handle.Workspace.DraftFingerprint(debt).ShouldBe(captured.DraftFingerprint);
            var witness = captured.Endpoints.ShouldHaveSingleItem();
            witness.Revision.ShouldBe(form.Endpoint.Revision!.Token);
            witness.DraftFingerprint.ShouldBe("none");
            UserBenchStore.ParseIntent(captured.IntentJson).Operations.ShouldHaveSingleItem().Links.ShouldBe([add]);
            _transport.TitleOf(42).ShouldBe(originalTitle);
            _transport.PatchAttempts.ShouldBe(0);
        }
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var scalar = (await editor.SelectAsync(key)).Value;
            scalar.RelationshipsReadOnly.ShouldBeTrue();
            scalar.RelationshipIntent.ShouldBe([add]);
            scalar.Evaluation.Fields.ShouldAllBe(field => field.ReadOnly);
            var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
            form.Intent.ShouldBe([add]);
            (await user.ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem().SameAs(captured).ShouldBeTrue();
            var saved = await editor.SaveRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, form.Intent);
            saved.IsSuccess.ShouldBeTrue();
            saved.Value.Verified.ShouldBeTrue();
            saved.Value.DraftRetained.ShouldBeFalse();
        }
        _transport.PatchAttempts.ShouldBe(1);
        _transport.CommittedWrites.ShouldBe(1);
        _transport.TitleOf(42).ShouldBe("Atomic Dependency True");
        var first = (await handle.Workspace.SnapshotAsync(default)).Value;
        first.Drafts.ShouldBeEmpty();
        var operation = first.Proposals.ShouldHaveSingleItem().Operations.ShouldHaveSingleItem();
        operation.Kind.ShouldBe(OpKind.Batch);
        operation.Fields["System.Title"].Text.ShouldBe("Atomic Dependency True");
        operation.Links.ShouldBe([add]);
        first.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Verified);
        first.Relationships.ShouldContain(new Relationship(key, other, add.Relation, RelationshipOrigin.Provider));
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
            form.Choices.ShouldContain(remove);
            form.Choices.ShouldNotContain(add);
            (await editor.StageRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, [remove])).IsSuccess.ShouldBeTrue();
            captured = (await user.ReadEditorCapturesAsync(default)).Value.Single(capture => !capture.Settled);
            captured.Ready.ShouldBeTrue();
            captured.Endpoints.ShouldHaveSingleItem().Revision.ShouldBe(form.Endpoint.Revision!.Token);
            var debt = (await handle.Workspace.SnapshotAsync(default)).Value.Drafts.ShouldHaveSingleItem();
            debt.Changes.Count.ShouldBe(0);
            handle.Workspace.DraftFingerprint(debt).ShouldBe(captured.DraftFingerprint);
        }
        NativeEditorCapture? atDispatch = null;
        _transport.AfterCommit = async () =>
            atDispatch = (await user.ReadEditorCapturesAsync(default)).Value.Single(capture => !capture.Settled);
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
            form.Intent.ShouldBe([remove]);
            (await user.ReadEditorCapturesAsync(default)).Value.Single(capture => !capture.Settled).SameAs(captured).ShouldBeTrue();
            var saved = await editor.SaveRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, form.Intent);
            saved.IsSuccess.ShouldBeTrue();
            saved.Value.Verified.ShouldBeTrue();
            saved.Value.DraftRetained.ShouldBeFalse();
        }
        var dispatched = atDispatch.ShouldNotBeNull();
        dispatched.Id.ShouldBe(captured.Id);
        dispatched.Revision.ShouldBe(captured.Revision + 2, "This fix must not change physical restaging or capture history.");
        var settled = (await user.ReadEditorCapturesAsync(default)).Value.Single(capture => capture.Id == captured.Id);
        settled.Settled.ShouldBeTrue();
        settled.Revision.ShouldBe(dispatched.Revision + 1);
        _transport.PatchAttempts.ShouldBe(2);
        _transport.CommittedWrites.ShouldBe(2);
        _transport.RevisionOf(42).ShouldBe(targetRevision + 2);
        _transport.RevisionOf(43).ShouldBe(endpointRevision + 2);
        _transport.TitleOf(42).ShouldBe("Atomic Dependency True");
        var final = (await handle.Workspace.SnapshotAsync(default)).Value;
        final.Drafts.ShouldBeEmpty();
        final.Proposals.Length.ShouldBe(2);
        final.Proposals.ShouldAllBe(proposal => proposal.Operations.Length == 1 && proposal.Operations[0].Kind == OpKind.Batch);
        final.Journal.Length.ShouldBe(2);
        final.Journal.ShouldAllBe(row => row.Status == OpStatus.Verified && row.Receipts.Length == 2
            && row.Receipts[0].Status == OpStatus.Applied && row.Receipts[1].Status == OpStatus.Verified
            && row.Receipts[1].Revision!.Item == key);
        final.Items.Where(item => item.Key == key || item.Key == other).Count().ShouldBe(2);
        final.Items.Where(item => item.Key == key || item.Key == other).ShouldAllBe(item => item.RelationshipsComplete);
        final.Relationships.ShouldNotContain(edge => edge.Origin == RelationshipOrigin.Provider && edge.Kind == add.Relation);
        using var patch = System.Text.Json.JsonDocument.Parse(_transport.RelationshipPatches[1].Body);
        patch.RootElement[0].GetProperty("op").GetString().ShouldBe("test");
        patch.RootElement[0].GetProperty("path").GetString().ShouldBe("/rev");
        patch.RootElement.EnumerateArray().Count(value => value.GetProperty("op").GetString() == "remove"
            && value.GetProperty("path").GetString()!.StartsWith("/relations/", StringComparison.Ordinal)).ShouldBe(1);
        patch.RootElement.EnumerateArray().ShouldNotContain(value => value.GetProperty("path").GetString() == "/fields/System.Title");
    }
}
