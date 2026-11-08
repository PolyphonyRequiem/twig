using Microsoft.Extensions.DependencyInjection;
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
    public async Task NativePublicQualifiedRelationshipEvaluationShowsCloseGatesWithoutStaging(bool retained)
    {
        var host = await CreateHumanHostAsync();
        using var root = EditorServices(host.Root);
        using var origin = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var schema = (await origin.Workspace.SchemaAsync(default)).Value;
        var type = schema.Types.Single(value => value.Reference == "Fixture.Task");
        var doing = type.States.Single(state => state.Category == StateCategory.InProgress).Name;
        var done = type.States.Single(state => state.Category == StateCategory.Completed).Name;
        var gate = type.Rules.Single(rule => rule.Action == RuleAction.Required
            && rule.Conditions.Any(condition => condition.Field == type.StateField && condition.Value!.Text == done)).Field;
        type.Transitions.ShouldContain(new Transition(doing, done));
        _transport.SetState(42, doing);
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43");
        var user = UserBenchStore.ForRegistry(host.Registry);
        using var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
        form.Target.Fields[type.StateField].Text.ShouldBe(doing);
        form.Fields.Evaluation.States.ShouldContain(done);
        form.Fields.Evaluation.Fields.Single(field => field.Definition.Reference == gate).Required.ShouldBeFalse();
        var link = new CreationLink(other, "Native.DependencyForward", true);
        form.Choices.ShouldContain(link);
        if (retained)
            (await editor.StageRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, [link])).IsSuccess.ShouldBeTrue();
        var before = (await origin.Workspace.SnapshotAsync(default)).Value;
        var draftFingerprint = origin.Workspace.DraftFingerprint(before.Drafts.SingleOrDefault(draft => draft.Key == key));
        var capture = (await user.ReadEditorCapturesAsync(default)).Value.SingleOrDefault();
        if (retained)
        {
            capture.ShouldNotBeNull().Ready.ShouldBeTrue();
            UserBenchStore.ParseIntent(capture.IntentJson).Operations.Single().Links.ShouldBe([link]);
            before.Drafts.ShouldHaveSingleItem().Changes.Count.ShouldBe(0);
        }
        else { capture.ShouldBeNull(); before.Drafts.ShouldBeEmpty(); }
        var changes = FieldValues.Empty.Set(type.StateField, FieldValue.String(done));
        var expected = Twig.Core.Core.EvaluateEditorFields(schema, form.Target, changes, true).Value;
        expected.Fields.Single(field => field.Definition.Reference == gate).Required.ShouldBeTrue();
        expected.Errors.ShouldNotBeEmpty("The prospective transition genuinely lacks its process-declared close gate.");
        var evaluated = await editor.EvaluateAsync(form.Fields.Ticket, changes);
        evaluated.IsSuccess.ShouldBeTrue(evaluated.IsSuccess ? "" : evaluated.Error.Message);
        evaluated.Value.Values[type.StateField].Text.ShouldBe(done);
        var required = evaluated.Value.Evaluation.Fields.Single(field => field.Definition.Reference == gate);
        required.Required.ShouldBeTrue();
        required.Allowed.ShouldBe(expected.Fields.Single(field => field.Definition.Reference == gate).Allowed);
        evaluated.Value.Evaluation.Errors.Select(error => error.Code).ShouldBe(expected.Errors.Select(error => error.Code));
        var complete = changes.Set(gate, FieldValue.String("Explicit prospective evidence"));
        var ready = await editor.EvaluateAsync(form.Fields.Ticket, complete);
        ready.IsSuccess.ShouldBeTrue(ready.IsSuccess ? "" : ready.Error.Message);
        ready.Value.Evaluation.Errors.ShouldBeEmpty();
        (await editor.StageAsync(form.Fields.Ticket, complete)).Error.Code.ShouldBe(ErrorCode.Unsupported);
        (await editor.SaveAsync(form.Fields.Ticket, complete)).Error.Code.ShouldBe(ErrorCode.Unsupported);
        var after = (await origin.Workspace.SnapshotAsync(default)).Value;
        after.Version.ShouldBe(before.Version, "Field evaluation must not stage, recapture, prepare or update the Core store.");
        origin.Workspace.DraftFingerprint(after.Drafts.SingleOrDefault(draft => draft.Key == key)).ShouldBe(draftFingerprint);
        after.Proposals.ShouldBeEmpty();
        after.Journal.ShouldBeEmpty();
        after.Items.Single(item => item.Key == key).Fields[type.StateField].Text.ShouldBe(doing);
        var unchanged = (await user.ReadEditorCapturesAsync(default)).Value.SingleOrDefault();
        if (capture is null) unchanged.ShouldBeNull();
        else unchanged.ShouldNotBeNull().SameAs(capture).ShouldBeTrue();
        _transport.PatchAttempts.ShouldBe(0);
        _transport.PostCount.ShouldBe(0);
        if (retained)
        {
            var scalar = (await editor.SelectAsync(key)).Value;
            scalar.RelationshipsReadOnly.ShouldBeTrue();
            (await editor.EvaluateAsync(scalar.Ticket, changes)).Error.Code.ShouldBe(ErrorCode.Unsupported);
            (await editor.SaveAsync(scalar.Ticket, complete)).Error.Code.ShouldBe(ErrorCode.Unsupported);
            _transport.PatchAttempts.ShouldBe(0);
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("endpoint")]
    [InlineData("store")]
    public async Task NativePublicQualifiedRelationshipEvaluationRefusesInvalidatedEvidence(string damage)
    {
        var host = await CreateHumanHostAsync();
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43");
        using var root = EditorServices(host.Root);
        using var origin = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        using var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
        var user = UserBenchStore.ForRegistry(host.Registry);
        if (damage == "source") _transport.ObservedPrincipalOverride = Sibling;
        else if (damage == "endpoint") _transport.SetItem(43, "Changed endpoint", _transport.RevisionOf(43) + 1, Actor);
        else
            (await origin.Workspace.StageEditAsync(other,
                FieldValues.Empty.Set("System.Title", FieldValue.String("Independent endpoint debt")), default)).IsSuccess.ShouldBeTrue();
        var before = (await origin.Workspace.SnapshotAsync(default)).Value;
        var evaluated = await editor.EvaluateAsync(form.Fields.Ticket, FieldValues.Empty);
        evaluated.IsSuccess.ShouldBeFalse(damage);
        var after = (await origin.Workspace.SnapshotAsync(default)).Value;
        after.Version.ShouldBe(before.Version);
        after.Drafts.Select(draft => origin.Workspace.DraftFingerprint(draft))
            .ShouldBe(before.Drafts.Select(draft => origin.Workspace.DraftFingerprint(draft)));
        after.Proposals.ShouldBeEmpty();
        after.Journal.ShouldBeEmpty();
        (await user.ReadEditorCapturesAsync(default)).Value.ShouldBeEmpty();
        _transport.PatchAttempts.ShouldBe(0);
        _transport.PostCount.ShouldBe(0);
    }
}
