using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task NativeHumanPlanEditorOpensOwnedScopeWithoutPublishingOrTouchingNormalDraft()
    {
        var host = await CreateHumanHostAsync();
        using var services = EditorServices(host.Root);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var library = (await user.ReadAsync()).Value;
        var bench = library.Benches.Single();
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        using var origin = (await factory.OpenAsync(default)).Value;
        (await origin.Workspace.StageEditAsync(key,
            FieldValues.Empty.Set("System.Title", FieldValue.String("Independent normal intent")), default)).IsSuccess.ShouldBeTrue();
        (await user.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, 0, true)).IsSuccess.ShouldBeTrue();
        (await user.ReadPlanAsync(bench.Id)).Value.PlanMode.ShouldBeTrue();
        var opened = await OpenHumanHostAsync(host.Root, bench.Id, host.Stamp, default);
        opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
        using var editor = opened.Value;
        var selected = await editor.SelectAsync(key);
        selected.IsSuccess.ShouldBeTrue(selected.IsSuccess ? "" : selected.Error.Message);
        selected.Value.Values["System.Title"].Text.ShouldNotBe("Independent normal intent");
        (await editor.SaveAsync(selected.Value.Ticket,
            FieldValues.Empty.Set("System.Title", FieldValue.String("Must not publish")))).IsSuccess.ShouldBeFalse();
        HumanPatchAttempts.ShouldBe(0);
        (await origin.Workspace.SnapshotAsync(default)).Value.Drafts.Single()
            .Changes["System.Title"].Text.ShouldBe("Independent normal intent");
    }

    [Fact]
    public async Task NativeHumanPlanStagesAcrossItemsAndSeedsAndReopensWithoutCopyingNormalIntent()
    {
        var host = await CreateHumanHostAsync();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        Guid seedId;
        using (var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            editor.PlanMode.ShouldBeTrue();
            editor.StoreInstance.ShouldNotBe(host.Stamp.StoreInstance);
            var first = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42"))).Value;
            (await editor.StageAsync(first.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Planned 42")))).IsSuccess.ShouldBeTrue();
            var second = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43"))).Value;
            (await editor.StageAsync(second.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Planned 43")))).IsSuccess.ShouldBeTrue();
            var created = await editor.CreateSeedAsync(first.Type);
            created.IsSuccess.ShouldBeTrue(created.IsSuccess ? "" : created.Error.Message);
            created.Value.Item.IsLocal.ShouldBeTrue();
            seedId = created.Value.Item.LocalId;
            (await editor.StageAsync(created.Value.Ticket,
                FieldValues.Empty.Set("System.Title", FieldValue.String("Planned local seed")))).IsSuccess.ShouldBeTrue();
            (await editor.ReadPlanDraftsAsync()).Value.Length.ShouldBe(3);
            HumanPatchAttempts.ShouldBe(0);
            status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
            (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, false)).IsSuccess.ShouldBeTrue();
            (await editor.StageAsync(created.Value.Ticket,
                FieldValues.Empty.Set("System.Title", FieldValue.String("Stale handle")))).Error.Code.ShouldBe(ErrorCode.Conflict);
        }
        using (var normal = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value)
        {
            normal.PlanMode.ShouldBeFalse();
            (await normal.CreateSeedAsync("Task")).Error.Code.ShouldBe(ErrorCode.Unsupported);
            (await normal.SelectAsync(ItemKey.Local(seedId))).Error.Code.ShouldBe(ErrorCode.Unsupported);
        }
        status = (await user.ReadPlanStatusAsync(host.Bench)).Value;
        (await user.ChangePlanModeAsync(status.UserInstance, host.Bench, status.BenchRevision, status.Revision, true)).IsSuccess.ShouldBeTrue();
        using var reopened = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var drafts = (await reopened.ReadPlanDraftsAsync()).Value;
        drafts.Length.ShouldBe(3);
        drafts.Single(value => value.Key.IsLocal && value.Key.LocalId == seedId).Changes["System.Title"].Text.ShouldBe("Planned local seed");
        HumanPatchAttempts.ShouldBe(0);
    }

    public async Task<(string Root, string Registry, Guid Bench, NativeSourceStamp Stamp)> CreateHumanHostAsync()
    {
        var native = await RelationshipWorkspaceAsync();
        _transport.RichTextEnabled = _transport.CloseGateEnabled = true;
        using var services = EditorServices(native.Paths.StartDir);
        using var origin = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        foreach (var id in new[] { "42", "43" })
            (await origin.Workspace.ReadAsync(ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, id), default)).IsSuccess.ShouldBeTrue();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "ADO editor fixture",
            [new(SelectorKind.Pin, ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42"), connection: native.Connection),
             new(SelectorKind.Pin, ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "43"), connection: native.Connection)]), 0)).Value;
        (await NativeSourceReader.SelectBenchAsync(native.Paths.StartDir, origin.Source, null, bench.Id, ConfigureHumanEditor, default)).IsSuccess.ShouldBeTrue();
        return (native.Paths.StartDir, _bindings.RegistryPath, bench.Id, origin.Source);
    }
    public Task<Result<NativeSourceObservation>> ReadHumanHostAsync(string root, NativeSourceStamp? expected, bool schema, CancellationToken ct)
        => NativeSourceReader.ReadAsync(root, expected, schema, ConfigureHumanEditor, ct);
    public Task<Result<NativeSourceObservation>> ReadHumanOnlineAsync(string root, NativeSourceStamp expected, CancellationToken ct)
        => NativeSourceReader.ReadOnlineAsync(root, expected, ConfigureHumanEditor, ct);
    public Task<Result<NativeItemEditor>> OpenHumanHostAsync(string root, Guid bench, NativeSourceStamp expected, CancellationToken ct)
        => NativeItemEditor.OpenAsync(root, bench, expected, ConfigureHumanEditor, ct);
    public Task<Result<NativePlanReview>> ReviewHumanPlanAsync(string root, Guid bench, ImmutableArray<NativePlanSource> sources, CancellationToken ct)
        => NativePlanReview.ReviewAsync(root, bench, sources, ConfigureHumanEditor, ct);
    public Task<Result<NativePlanReview>> OpenHumanPlanReviewAsync(string root, string digest, CancellationToken ct)
        => NativePlanReview.OpenAsync(root, digest, ConfigureHumanEditor, ct);
    public Func<Task>? HumanBeforeProjectMetadata { set => _transport.BeforeProjectMetadata = value; }
    public Task<Result<NativeSourceObservation>> SelectHumanBenchAsync(string root, NativeSourceStamp expected, Guid? previous, Guid? next, CancellationToken ct)
        => NativeSourceReader.SelectBenchAsync(root, expected, previous, next, ConfigureHumanEditor, ct);
    public int HumanPatchAttempts => _transport.PatchAttempts;
    public bool HumanActorDenied { set => _transport.ObservedPrincipalOverride = value ? Sibling : null; }

    private void ConfigureHumanEditor(IServiceCollection services)
    {
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
    }

    [Fact]
    public async Task NativeHumanEditorPublishesTitleHtmlAndDiscoveredStateWithRequiredGate()
    {
        var native = await RelationshipWorkspaceAsync();
        _transport.RichTextEnabled = _transport.CloseGateEnabled = true;
        using var services = EditorServices(native.Paths.StartDir);
        using var origin = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Human Save", []), 0)).Value;
        var context = await origin.Workspace.Context.ReadAsync(default);
        using var editor = (await NativeItemEditor.OpenAsync(native.Paths.StartDir, bench.Id, origin.Source, ConfigureHumanEditor, default)).Value;
        var form = (await editor.SelectAsync(ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42"))).Value;
        form.Evaluation.States.ShouldContain("Doing");
        form.Evaluation.States.ShouldNotContain("Done");
        var fields = FieldValues.Empty.Set("System.Title", FieldValue.String("Human title"))
            .Set("System.Description", FieldValue.RichText("<p>Human <strong>description</strong></p>", FieldKind.Html))
            .Set(form.StateField, FieldValue.String("Doing"));
        var firstSave = await editor.SaveAsync(form.Ticket, fields);
        firstSave.IsSuccess.ShouldBeTrue(firstSave.IsSuccess ? "" : firstSave.Error.Message);
        firstSave.Value.Verified.ShouldBeTrue(firstSave.Value.Error?.Message);
        form = (await editor.SelectAsync(ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42"))).Value;
        form.Evaluation.States.ShouldContain("Done");
        fields = FieldValues.Empty.Set(form.StateField, FieldValue.String("Done"));
        var evaluation = (await editor.EvaluateAsync(form.Ticket, fields)).Value;
        evaluation.Evaluation.Fields.Single(value => value.Definition.Reference == "Fixture.CloseEvidence").Required.ShouldBeTrue();
        (await editor.SaveAsync(form.Ticket, fields)).IsSuccess.ShouldBeFalse();
        _transport.PatchAttempts.ShouldBe(1);
        var result = await editor.SaveAsync(form.Ticket, fields.Set("Fixture.CloseEvidence", FieldValue.String("Observed complete")));
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.Verified.ShouldBeTrue(result.Value.Error?.Message);
        result.Value.DraftRetained.ShouldBeFalse();
        _transport.PatchAttempts.ShouldBe(2);
        var snapshot = (await native.Store.ReadAsync(native.Connection, default)).Value;
        var saved = snapshot.Items.Single(value => value.Key.NativeId == "42");
        saved.Fields["System.Title"].Text.ShouldBe("Human title");
        saved.Fields["System.Description"].Kind.ShouldBe(FieldKind.Html);
        saved.Fields["System.State"].Text.ShouldBe("Done");
        (await origin.Workspace.Context.ReadAsync(default)).Value.ShouldBe(context.Value);
        (await user.ReadEditorCapturesAsync(default)).Value.ShouldAllBe(value => value.Settled);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("conflict")]
    [InlineData("mode")]
    [InlineData("draft")]
    [InlineData("schema")]
    public async Task NativeHumanEditorFreshGuardsStopBeforeMutation(string change)
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        using var origin = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Guarded human", []), 0)).Value;
        using var editor = (await NativeItemEditor.OpenAsync(native.Paths.StartDir, bench.Id, origin.Source, ConfigureHumanEditor, default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var form = (await editor.SelectAsync(key)).Value;
        if (change == "actor") _transport.ObservedPrincipalOverride = Sibling;
        else if (change == "conflict") _transport.SetItem(42, "Concurrent title", _transport.RevisionOf(42) + 1, Actor);
        else if (change == "mode")
            (await user.SetPlanModeAsync((await user.ReadAsync()).Value.InstanceId, bench.Id, bench.Revision, 0, true)).IsSuccess.ShouldBeTrue();
        else if (change == "schema") _transport.CloseGateEnabled = true;
        else (await origin.Workspace.StageEditAsync(key, FieldValues.Empty.Set("System.Title", FieldValue.String("CLI debt")), default)).IsSuccess.ShouldBeTrue();
        var result = await editor.SaveAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Human intent")));
        (result.IsSuccess && result.Value.Verified).ShouldBeFalse();
        _transport.PatchAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task NativeHumanEditorReusesAuthorityBoundMetadataAcrossItemsAndProvisionalEvaluation()
    {
        var host = await CreateHumanHostAsync();
        using var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var metadata = _transport.ObservedPaths.Count(path => path.EndsWith("/fields") || path.EndsWith("/layout") || path.EndsWith("/rules"));
        var first = (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42"))).Value;
        (await editor.EvaluateAsync(first.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Provisional")))).IsSuccess.ShouldBeTrue();
        (await editor.SelectAsync(ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43"))).IsSuccess.ShouldBeTrue();
        _transport.ObservedPaths.Count(path => path.EndsWith("/fields") || path.EndsWith("/layout") || path.EndsWith("/rules")).ShouldBe(metadata);
        _transport.PatchAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task NativeHumanEditorNormalSaveNeverFlushesIndependentPlanFields()
    {
        var host = await CreateHumanHostAsync();
        using var services = EditorServices(host.Root);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(host.Registry);
        var library = (await user.ReadAsync()).Value;
        var bench = library.Benches.Single();
        (await user.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, 0, true)).IsSuccess.ShouldBeTrue();
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        using var plan = (await NativeEditor.OpenAsync(factory, user, bench.Id, host.Stamp, default)).Value;
        var item = (await plan.SelectAsync(key, default)).Value;
        (await plan.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("Plan-only title")), null, null, default)).IsSuccess.ShouldBeTrue();
        var mode = (await user.ReadPlanAsync(bench.Id)).Value;
        (await user.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, mode.Revision, false)).IsSuccess.ShouldBeTrue();
        using var normal = (await NativeItemEditor.OpenAsync(host.Root, bench.Id, host.Stamp, ConfigureHumanEditor, default)).Value;
        var form = (await normal.SelectAsync(key)).Value;
        var saved = await normal.SaveAsync(form.Ticket, FieldValues.Empty.Set("System.Title", FieldValue.String("Normal-only title")));
        saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
        saved.Value.Verified.ShouldBeTrue();
        (await plan.Workspace.SnapshotAsync(default)).Value.Drafts.Single().Changes["System.Title"].Text.ShouldBe("Plan-only title");
    }
}
