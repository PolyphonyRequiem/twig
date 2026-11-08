using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData("Native.Association", true, true)]
    [InlineData("Native.DependencyForward", true, false)]
    [InlineData("Native.DependencyForward", false, true)]
    [InlineData("Native.ParentForward", false, false)]
    public async Task NativePublicRelationshipsStageReopenAndSaveOneAtomicEffect(string relation, bool outgoing, bool fields)
    {
        var host = await CreateHumanHostAsync();
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43");
        var user = UserBenchStore.ForRegistry(host.Registry);
        var delta = new CreationLink(other, relation, outgoing);
        var changes = fields ? FieldValues.Empty.Set("System.Title", FieldValue.String("Atomic native relationship")) : FieldValues.Empty;
        var targetRevision = _transport.RevisionOf(42);
        var endpointRevision = _transport.RevisionOf(43);
        Guid captureId;
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var selected = await editor.SelectRelationshipsAsync(key, other);
            selected.IsSuccess.ShouldBeTrue(selected.IsSuccess ? "" : selected.Error.Message);
            var form = selected.Value;
            form.Target.Key.ShouldBe(key);
            form.Endpoint.Key.ShouldBe(other);
            form.Target.RelationshipsComplete.ShouldBeTrue();
            form.Endpoint.RelationshipsComplete.ShouldBeTrue();
            form.Target.Revision.ShouldNotBeNull().Item.ShouldBe(key);
            form.Endpoint.Revision.ShouldNotBeNull().Item.ShouldBe(other);
            form.Relations.ShouldContain(new RelationKind("Native.DependencyForward", RelationShape.Dependency, true));
            form.Relations.ShouldContain(new RelationKind("Native.ParentForward", RelationShape.Hierarchy, true));
            form.Relations.ShouldContain(new RelationKind("Native.Association", RelationShape.Association, false));
            form.Relations.ShouldNotContain(kind => kind.Shape == RelationShape.Resource || kind.Reference.EndsWith("Reverse", StringComparison.Ordinal));
            form.Choices.ShouldContain(delta);
            form.Choices.ShouldNotContain(choice => choice.Remove);
            form.Incident.ShouldBeEmpty();
            (await editor.SaveAsync(form.Fields.Ticket, changes)).Error.Code.ShouldBe(ErrorCode.Unsupported);
            var staged = await editor.StageRelationshipsAsync(form.Fields.Ticket, changes, [delta]);
            staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
            staged.Value.Intent.ShouldBe([delta]);
            staged.Value.Incident.ShouldBeEmpty("Staging changes intent, never observed relationship presence or direction.");
            HumanPatchAttempts.ShouldBe(0);
            _transport.RevisionOf(42).ShouldBe(targetRevision);
            _transport.RevisionOf(43).ShouldBe(endpointRevision);
            var capture = (await user.ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem();
            captureId = capture.Id;
            capture.Ready.ShouldBeTrue();
            using var services = EditorServices(host.Root);
            using var origin = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
            (await origin.Workspace.SnapshotAsync(default)).Value.Drafts.ShouldHaveSingleItem().Links.ShouldBeEmpty();
        }
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var readOnly = (await editor.SelectAsync(key)).Value;
            readOnly.RelationshipsReadOnly.ShouldBeTrue();
            readOnly.RelationshipIntent.ShouldBe([delta]);
            readOnly.Evaluation.Fields.ShouldAllBe(field => field.ReadOnly);
            (await editor.SaveAsync(readOnly.Ticket, FieldValues.Empty)).Error.Code.ShouldBe(ErrorCode.Unsupported);
            var reopened = await editor.SelectRelationshipsAsync(key, other);
            reopened.IsSuccess.ShouldBeTrue(reopened.IsSuccess ? "" : reopened.Error.Message);
            reopened.Value.Intent.ShouldBe([delta]);
            reopened.Value.Incident.ShouldBeEmpty();
            (await user.ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem().Id.ShouldBe(captureId);
            var saved = await editor.SaveRelationshipsAsync(reopened.Value.Fields.Ticket, FieldValues.Empty, reopened.Value.Intent);
            saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
            saved.Value.Verified.ShouldBeTrue(saved.Value.Error?.Message);
            saved.Value.DraftRetained.ShouldBeFalse();
        }
        HumanPatchAttempts.ShouldBe(1);
        _transport.CommittedWrites.ShouldBe(1);
        _transport.RevisionOf(42).ShouldBe(targetRevision + 1);
        _transport.RevisionOf(43).ShouldBe(endpointRevision + 1);
        if (fields) _transport.TitleOf(42).ShouldBe("Atomic native relationship");
        var patch = _transport.RelationshipPatches.ShouldHaveSingleItem();
        patch.Id.ShouldBe(42, "Fields and incident delta must ride in one target request, even for an incoming edge.");
        using (var document = JsonDocument.Parse(patch.Body))
        {
            var operations = document.RootElement.EnumerateArray().ToArray();
            operations.ShouldContain(operation => operation.GetProperty("path").GetString() == "/rev" && operation.GetProperty("op").GetString() == "test");
            operations.Count(operation => operation.GetProperty("path").GetString() == "/relations/-").ShouldBe(1);
            operations.Any(operation => operation.GetProperty("path").GetString() == "/fields/System.Title").ShouldBe(fields);
        }
        using var root = EditorServices(host.Root);
        using var handle = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var snapshot = (await handle.Workspace.SnapshotAsync(default)).Value;
        snapshot.Drafts.ShouldBeEmpty();
        var proposal = snapshot.Proposals.ShouldHaveSingleItem();
        var op = proposal.Operations.ShouldHaveSingleItem();
        op.Kind.ShouldBe(OpKind.Batch);
        op.Item.ShouldBe(key);
        op.Links.ShouldBe([delta]);
        op.Fields.Count.ShouldBe(fields ? 1 : 0);
        var row = snapshot.Journal.ShouldHaveSingleItem();
        row.Digest.ShouldBe(proposal.Digest);
        row.Status.ShouldBe(OpStatus.Verified);
        row.Receipts.Last().Revision.ShouldNotBeNull().Item.ShouldBe(key);
        snapshot.Items.ShouldContain(item => item.Key == other && item.RelationshipsComplete
            && item.Revision!.Token == "ado-rev:" + (endpointRevision + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        snapshot.Relationships.ShouldContain(new Relationship(outgoing ? key : other, outgoing ? other : key, relation, RelationshipOrigin.Provider));

        var removal = new CreationLink(other, relation, outgoing, true);
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
            form.Choices.ShouldContain(removal);
            form.Choices.ShouldNotContain(delta);
            var staged = await editor.StageRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, [removal]);
            staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
            staged.Value.Incident.ShouldContain(edge => edge.Kind == relation);
            HumanPatchAttempts.ShouldBe(1);
        }
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
            form.Intent.ShouldBe([removal]);
            var saved = await editor.SaveRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, form.Intent);
            saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
            saved.Value.Verified.ShouldBeTrue(saved.Value.Error?.Message);
        }
        snapshot = (await handle.Workspace.SnapshotAsync(default)).Value;
        snapshot.Relationships.ShouldNotContain(edge => edge.Origin == RelationshipOrigin.Provider && edge.Kind == relation);
        snapshot.Journal.Length.ShouldBe(2);
        snapshot.Journal.ShouldAllBe(entry => entry.Status == OpStatus.Verified);
        HumanPatchAttempts.ShouldBe(2);
        _transport.RevisionOf(42).ShouldBe(targetRevision + 2);
        _transport.RevisionOf(43).ShouldBe(endpointRevision + 2);
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("target")]
    [InlineData("schema")]
    [InlineData("actor")]
    [InlineData("mode")]
    [InlineData("bench")]
    [InlineData("draft")]
    [InlineData("incident-debt")]
    [InlineData("ticket")]
    [InlineData("store")]
    [InlineData("source")]
    [InlineData("witness")]
    public async Task NativePublicRelationshipsChangedEvidenceRefusesWithoutReplacingCapture(string damage)
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        using var root = EditorServices(host.Root);
        using var origin = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43");
        using var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
        var intent = ImmutableArray.Create(new CreationLink(other, "Native.DependencyForward", true));
        (await editor.StageRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, intent)).IsSuccess.ShouldBeTrue();
        var original = (await user.ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem();
        if (damage == "endpoint") _transport.SetItem(43, "Changed endpoint", _transport.RevisionOf(43) + 1, Actor);
        else if (damage == "target") _transport.SetItem(42, "Changed target", _transport.RevisionOf(42) + 1, Actor);
        else if (damage == "schema")
        {
            _transport.CloseGateEnabled.ShouldBeTrue("This fixture must genuinely change the captured schema.");
            _transport.CloseGateEnabled = false;
        }
        else if (damage == "actor") _transport.ObservedPrincipalOverride = Sibling;
        else if (damage == "mode")
        {
            var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
            (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        }
        else if (damage == "bench")
        {
            var bench = (await user.ReadAsync()).Value.Benches.Single();
            (await user.SaveAsync(new(bench.Id, "Changed Bench", bench.Selectors), bench.Revision)).IsSuccess.ShouldBeTrue();
        }
        else if (damage is "draft" or "incident-debt")
            (await origin.Workspace.StageEditAsync(damage == "draft" ? key : other,
                FieldValues.Empty.Set("System.Title", FieldValue.String("Unowned CLI debt")), default)).IsSuccess.ShouldBeTrue();
        else if (damage == "store")
        {
            var replacement = (await Twig.Store.Sqlite.SqliteStore.CreateAsync(Path.Combine(host.Root, "replacement.db"))).Value;
            (await replacement.PinWorkspaceAsync(host.Stamp.Connection)).IsSuccess.ShouldBeTrue();
            var paths = root.GetRequiredService<Twig.Infrastructure.Config.TwigPaths>();
            await File.WriteAllTextAsync(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName),
                JsonSerializer.Serialize(origin.Document with { DatabasePath = replacement.DatabasePath, DatabaseInstance = replacement.InstanceId },
                    CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument));
        }
        else if (damage == "source")
        {
            var paths = root.GetRequiredService<Twig.Infrastructure.Config.TwigPaths>();
            await File.WriteAllTextAsync(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName),
                JsonSerializer.Serialize(origin.Document with { PrincipalId = Sibling }, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument));
        }
        else if (damage == "witness")
        {
            var changed = await user.SaveEditorCaptureAsync(original with { Revision = original.Revision + 1,
                Endpoints = [original.Endpoints.Single() with { Revision = "changed-captured-revision" }] }, original.Revision, default);
            changed.IsSuccess.ShouldBeTrue(changed.IsSuccess ? "" : changed.Error.Message);
            original = changed.Value;
        }
        var saved = await editor.SaveRelationshipsAsync(damage == "ticket" ? Guid.NewGuid() : form.Fields.Ticket, FieldValues.Empty, intent);
        saved.IsSuccess.ShouldBeFalse(damage);
        HumanPatchAttempts.ShouldBe(0);
        (await user.ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem().SameAs(original).ShouldBeTrue("Failed checks must preserve the original capture exactly.");
        if (damage is "draft" or "incident-debt")
            (await origin.Workspace.SnapshotAsync(default)).Value.Drafts.ShouldContain(draft => draft.Changes.ContainsKey("System.Title") && draft.Changes["System.Title"].Text == "Unowned CLI debt");
    }

    [Theory]
    [InlineData("missing-remove")]
    [InlineData("inverse-association")]
    [InlineData("reverse-reference")]
    [InlineData("present-add")]
    [InlineData("wrong-remove-direction")]
    [InlineData("empty")]
    [InlineData("foreign")]
    public async Task NativePublicRelationshipsRejectWrongDirectionPresenceAndEndpointBeforeStaging(string defect)
    {
        var host = await CreateHumanHostAsync();
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43");
        if (defect == "present-add") _transport.SetNativeRelationship(42, 43, "Native.Association");
        if (defect == "wrong-remove-direction") _transport.SetNativeRelationship(42, 43, "Native.DependencyForward");
        using var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
        var link = new CreationLink(defect == "foreign" ? ItemKey.Remote(host.Stamp.Connection.Provider, "foreign", "43") : other,
            defect == "reverse-reference" ? "Native.DependencyReverse" : defect == "wrong-remove-direction" ? "Native.DependencyForward" : "Native.Association",
            defect is not ("inverse-association" or "wrong-remove-direction"),
            defect is "missing-remove" or "wrong-remove-direction");
        if (defect != "empty") form.Choices.ShouldNotContain(link);
        var staged = await editor.StageRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, defect == "empty" ? [] : [link]);
        staged.IsSuccess.ShouldBeFalse(defect);
        HumanPatchAttempts.ShouldBe(0);
        (await UserBenchStore.ForRegistry(host.Registry).ReadEditorCapturesAsync(default)).Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task NativePublicRelationshipsRefuseParentReplacementAndDependencyCycleThroughCore()
    {
        var host = await CreateHumanHostAsync();
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43");
        _transport.SetItem(44, "Existing parent", 1, Actor);
        _transport.SetHierarchy(44, 42);
        _transport.SetNativeRelationship(42, 43, "Native.DependencyForward");
        using var root = EditorServices(host.Root);
        using var origin = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var paths = root.GetRequiredService<Twig.Infrastructure.Config.TwigPaths>();
        await File.WriteAllTextAsync(paths.PolicyPath,
            """{"version":1,"rules":[{"id":"acyclic","kind":"dependency-integrity","enforcement":"refuse","maxNodes":20}]}""");
        using var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
        var parent = new CreationLink(other, "Native.ParentForward", false);
        form.Choices.ShouldNotContain(parent, "An existing parent must not be implicitly replaced.");
        (await editor.SaveRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, [parent])).Error.Code.ShouldBe(ErrorCode.Unsupported);
        form = (await editor.SelectRelationshipsAsync(key, other)).Value;
        var cycle = new CreationLink(other, "Native.DependencyForward", false);
        var refused = await editor.SaveRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, [cycle]);
        refused.IsSuccess.ShouldBeFalse("The real Core policy must evaluate the directed compound graph.");
        HumanPatchAttempts.ShouldBe(0);
        (await origin.Workspace.SnapshotAsync(default)).Value.Drafts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("schema")]
    [InlineData("denied")]
    [InlineData("incomplete")]
    public async Task NativePublicRelationshipsUnavailableMetadataAndEndpointNeverBecomeEmptySuccess(string defect)
    {
        var host = await CreateHumanHostAsync();
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43");
        if (defect == "disabled") _transport.DisableIncidentRelations = true;
        else if (defect == "schema") _transport.FailSchema = true;
        else if (defect == "denied") _transport.SetItem(43, "Unreadable endpoint", 7, Sibling);
        else _transport.IncompleteIncidentItem = 43;
        var opened = await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default);
        if (opened.IsSuccess)
        {
            using var editor = opened.Value;
            (await editor.SelectRelationshipsAsync(key, other)).IsSuccess.ShouldBeFalse(defect);
        }
        else opened.Error.Message.ShouldNotBeNullOrWhiteSpace();
        HumanPatchAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePublicRelationshipsOriginalUncertainReceiptIsProtectedWithoutReplay(bool lostAcknowledgement)
    {
        var host = await CreateHumanHostAsync();
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43");
        var user = UserBenchStore.ForRegistry(host.Registry);
        using var root = EditorServices(host.Root);
        using var origin = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
            var intent = ImmutableArray.Create(new CreationLink(other, "Native.Association", true));
            _transport.LoseNextPatchAcknowledgement = lostAcknowledgement;
            _transport.FailReadbackAfterCommit = !lostAcknowledgement;
            var saved = await editor.SaveRelationshipsAsync(form.Fields.Ticket,
                FieldValues.Empty.Set("System.Title", FieldValue.String("Original uncertain effect")), intent);
            (saved.IsSuccess && saved.Value.Verified).ShouldBeFalse();
            HumanPatchAttempts.ShouldBe(1);
            var snapshot = (await origin.Workspace.SnapshotAsync(default)).Value;
            snapshot.Journal.ShouldHaveSingleItem().Status.ShouldBe(OpStatus.Indeterminate);
            var capture = (await user.ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem();
            (await editor.SaveRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty, intent)).IsSuccess.ShouldBeFalse();
            (await user.ReadEditorCapturesAsync(default)).Value.ShouldHaveSingleItem().SameAs(capture).ShouldBeTrue();
        }
        _transport.FailReadbackAfterCommit = false;
        var original = (await origin.Workspace.SnapshotAsync(default)).Value;
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var readOnly = (await editor.SelectAsync(key)).Value;
            readOnly.RelationshipsReadOnly.ShouldBeTrue();
            (await editor.SaveAsync(readOnly.Ticket, FieldValues.Empty)).IsSuccess.ShouldBeFalse();
            (await editor.SelectRelationshipsAsync(key, other)).IsSuccess.ShouldBeFalse("The original endpoint baseline cannot be silently rebound after an uncertain attempt.");
        }
        var retained = (await origin.Workspace.SnapshotAsync(default)).Value;
        retained.Proposals.Select(proposal => Convert.ToHexString(Twig.Core.Core.ProposalBytes(proposal)))
            .ShouldBe(original.Proposals.Select(proposal => Convert.ToHexString(Twig.Core.Core.ProposalBytes(proposal))));
        var originalRow = original.Journal.ShouldHaveSingleItem();
        var retainedRow = retained.Journal.ShouldHaveSingleItem();
        retainedRow.Digest.ShouldBe(originalRow.Digest);
        retainedRow.Operation.ShouldBe(originalRow.Operation);
        retainedRow.Attempt.ShouldBe(originalRow.Attempt);
        retainedRow.Status.ShouldBe(originalRow.Status);
        retainedRow.Receipts.Length.ShouldBe(originalRow.Receipts.Length);
        for (var index = 0; index < originalRow.Receipts.Length; index++)
        {
            var before = originalRow.Receipts[index];
            var after = retainedRow.Receipts[index];
            after.Digest.ShouldBe(before.Digest);
            after.Operation.ShouldBe(before.Operation);
            after.Attempt.ShouldBe(before.Attempt);
            after.Status.ShouldBe(before.Status);
            after.Revision.ShouldBe(before.Revision);
            after.PublishedKey.ShouldBe(before.PublishedKey);
            (after.Error?.Code).ShouldBe(before.Error?.Code);
            (after.Error?.Message).ShouldBe(before.Error?.Message);
            after.Evidence.ShouldBe(before.Evidence);
        }
        HumanPatchAttempts.ShouldBe(1);
        _transport.CommittedWrites.ShouldBe(1);
        retained.Drafts.ShouldHaveSingleItem().Changes["System.Title"].Text.ShouldBe("Original uncertain effect");
    }

    [Fact]
    public async Task NativePublicRelationshipsDoNotAdoptCliDebtPlanSeedsOrAnotherBench()
    {
        var host = await CreateHumanHostAsync();
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43");
        var user = UserBenchStore.ForRegistry(host.Registry);
        using var root = EditorServices(host.Root);
        var factory = root.GetRequiredService<CoreWorkspaceFactory>();
        using var origin = (await factory.OpenAsync(default)).Value;
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        using var plan = (await NativeEditor.OpenAsync(factory, user, host.Bench, host.Stamp, default)).Value;
        var item = (await plan.SelectAsync(key, default)).Value;
        (await plan.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("Independent Plan")), null, null, default)).IsSuccess.ShouldBeTrue();
        using (var publicPlan = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
            (await publicPlan.SelectRelationshipsAsync(key, other)).Error.Code.ShouldBe(ErrorCode.Unsupported);
        status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, false)).IsSuccess.ShouldBeTrue();
        _transport.SetItem(44, "Independent CLI target", 1, Actor);
        var independent = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "44");
        (await origin.Workspace.StageEditAsync(independent, FieldValues.Empty.Set("System.Title", FieldValue.String("Independent CLI debt")), default)).IsSuccess.ShouldBeTrue();
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            (await editor.SelectRelationshipsAsync(independent, other)).Error.Code.ShouldBe(ErrorCode.Conflict);
            var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
            var saved = await editor.SaveRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty,
                [new(other, "Native.Association", true)]);
            saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
            saved.Value.Verified.ShouldBeTrue(saved.Value.Error?.Message);
        }
        (await plan.Workspace.SnapshotAsync(default)).Value.Drafts.ShouldHaveSingleItem().Changes["System.Title"].Text.ShouldBe("Independent Plan");
        (await origin.Workspace.SnapshotAsync(default)).Value.Drafts.ShouldHaveSingleItem().Changes["System.Title"].Text.ShouldBe("Independent CLI debt");
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var seed = (await editor.CreateSeedAsync("Fixture.Task")).Value;
            (await editor.SelectRelationshipsAsync(seed.Item, other)).Error.Code.ShouldBe(ErrorCode.Unsupported);
        }
        var seedCapture = (await user.ReadEditorCapturesAsync(default)).Value.Single(capture => !capture.Settled
            && !capture.PlanMode && UserBenchStore.ParseIntent(capture.IntentJson).Operations.Single().Item.IsLocal);
        UserBenchStore.ParseIntent(seedCapture.IntentJson).Operations.Single().Links.ShouldBeEmpty();
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
            (await editor.StageRelationshipsAsync(form.Fields.Ticket, FieldValues.Empty,
                [new(other, "Native.DependencyForward", true)])).IsSuccess.ShouldBeTrue();
        }
        var another = (await user.SaveAsync(new(Guid.NewGuid(), "Another Bench", []), 0)).Value;
        using (var editor = (await OpenHumanHostAsync(host.Root, another.Id, host.Stamp, default)).Value)
            (await editor.SelectRelationshipsAsync(key, other)).Error.Code.ShouldBe(ErrorCode.Conflict);
        HumanPatchAttempts.ShouldBe(1);
        _transport.PostCount.ShouldBe(0);
    }
}
