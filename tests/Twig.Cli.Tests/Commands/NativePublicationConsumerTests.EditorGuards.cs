using System.Text;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData("mode")]
    [InlineData("bench")]
    [InlineData("draft")]
    [InlineData("schema")]
    [InlineData("actor")]
    [InlineData("endpoint")]
    public async Task NativeEditorCapturedAuthorityAndVersionsRefuseChangedProofBeforeMutation(string damage)
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Guard", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "43");
        var item = (await editor.SelectAsync(key, default)).Value;
        var staged = (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("protected")), null,
            [new(other, "Native.Association", true)], default)).Value;
        if (damage == "mode")
            (await user.SetPlanModeAsync((await user.ReadAsync()).Value.InstanceId, bench.Id, bench.Revision, 0, true)).IsSuccess.ShouldBeTrue();
        else if (damage == "bench")
            (await user.SaveAsync(new(bench.Id, "Changed", []), bench.Revision)).IsSuccess.ShouldBeTrue();
        else if (damage == "draft")
            (await origin.Workspace.StageEditAsync(key, FieldValues.Empty.Set("System.Title", FieldValue.String("later")), default)).IsSuccess.ShouldBeTrue();
        else if (damage == "schema") _transport.CloseGateEnabled = true;
        else if (damage == "actor") _transport.ObservedPrincipalOverride = Sibling;
        else _transport.SetItem(43, "endpoint changed", _transport.RevisionOf(43) + 1, Actor);
        var refused = await editor.SaveAsync(staged, default);
        refused.IsSuccess.ShouldBeFalse(damage);
        _transport.PatchAttempts.ShouldBe(0);
        (await user.ReadEditorCapturesAsync(default)).Value.Single().Settled.ShouldBeFalse();
        (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeEditorInterruptedCaptureRequiresExactDurableDraftAndNeverPublishesOnRecovery(bool stageCompleted)
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Interrupted", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var item = (await editor.SelectAsync(key, default)).Value;
        var fields = FieldValues.Empty.Set("System.Title", FieldValue.String("durable intended value"));
        var intended = new Draft(key, item.Observation.Type, item.Observation, fields);
        var proposal = new Proposal(native.Connection, editor.Metadata.Schema.Fingerprint, item.StoreVersion,
            [new("intent", OpKind.Batch, key, item.Observation.Revision, fields)]);
        var capture = new NativeEditorCapture(Guid.NewGuid(), 1, (await user.ReadAsync()).Value.InstanceId, bench.Id,
            bench.Revision, editor.Plan.Id, editor.Plan.Revision, false, Guid.Empty, origin.Source, native.Paths.RepoRoot,
            native.Store.InstanceId, item.StoreVersion, Encoding.UTF8.GetString(Twig.Core.Core.ProposalBytes(proposal)),
            origin.Workspace.DraftFingerprint(intended), false);
        (await user.SaveEditorCaptureAsync(capture, 0, default)).IsSuccess.ShouldBeTrue();
        if (stageCompleted)
            (await origin.Workspace.StageEditAsync(key, fields, default, capturedBaseline: item.Observation,
                expectedVersion: item.StoreVersion)).IsSuccess.ShouldBeTrue();
        using var reopened = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var interrupted = (await reopened.SelectAsync(key, default)).Value;
        (await reopened.SaveAsync(interrupted, default)).IsSuccess.ShouldBeFalse();
        var recovered = await reopened.RecoverCaptureAsync(interrupted, default);
        recovered.IsSuccess.ShouldBe(stageCompleted);
        _transport.PatchAttempts.ShouldBe(0);
        var retained = (await user.ReadEditorCapturesAsync(default)).Value.Single();
        retained.Ready.ShouldBe(stageCompleted);
        retained.IntentJson.ShouldBe(capture.IntentJson);
        retained.Settled.ShouldBeFalse();
    }

    [Theory]
    [InlineData("DELETE FROM editor_captures;")]
    [InlineData("UPDATE editor_captures SET document='{}';")]
    [InlineData("DROP TABLE editor_captures;")]
    public async Task NativeEditorMissingOrCorruptCaptureCannotBecomeAbsentIntent(string damage)
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Integrity", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var item = (await editor.SelectAsync(key, default)).Value;
        var staged = (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("retained")), null, null, default)).Value;
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(Path.GetDirectoryName(_bindings.RegistryPath)!, "benches.db"), Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = damage;
            await command.ExecuteNonQueryAsync();
        }
        var refused = await editor.SaveAsync(staged, default);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        (await editor.SelectAsync(key, default)).IsSuccess.ShouldBeFalse();
        _transport.PatchAttempts.ShouldBe(0);
        (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.Single().Changes["System.Title"].Text.ShouldBe("retained");
    }

    [Fact]
    public async Task NativeEditorCachedGuidanceIsReusedAcrossItemsAndLocalStagesWithNonemptyBenchSelectors()
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Guidance", [new(SelectorKind.Pin, key, connection: native.Connection)]), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        int MetadataCalls() => _transport.ObservedPaths.Count(path => path.Contains("workitemtypes", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/fields", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/layout", StringComparison.OrdinalIgnoreCase));
        var calls = MetadataCalls();
        calls.ShouldBeGreaterThan(0);
        (await editor.SelectAsync(ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "43"), default)).IsSuccess.ShouldBeTrue();
        var item = (await editor.SelectAsync(key, default)).Value;
        var staged = await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("local")), null, null, default);
        staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
        MetadataCalls().ShouldBe(calls);
        _transport.PatchAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task NativeEditorPendingSafetyCheckStillAllowsFreshOwnedPlanDraftsWithoutInventingCoverage()
    {
        var native = await RelationshipWorkspaceAsync();
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _bindings.RegistryPath, Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE core_execution_meta SET active=0,digest=NULL,revision=revision+1;";
            await command.ExecuteNonQueryAsync();
        }
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Pending", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        (await user.SetPlanModeAsync((await user.ReadAsync()).Value.InstanceId, bench.Id, bench.Revision, 0, true)).IsSuccess.ShouldBeTrue();
        var opened = await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default);
        opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
        using var editor = opened.Value;
        var item = (await editor.SelectAsync(ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42"), default)).Value;
        (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("local pending")), null, null, default)).IsSuccess.ShouldBeTrue();
        (await NativeCoreExecution.PublishingAsync(_bindings.RegistryPath, default)).IsSuccess.ShouldBeFalse();
        (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.ShouldBeEmpty();
        _transport.PatchAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeEditorExactSourceStoreAndUserInstanceCannotBeReplaced(bool userInstance)
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Replacement", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var item = (await editor.SelectAsync(ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42"), default)).Value;
        var staged = (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("never lose")), null, null, default)).Value;
        if (userInstance)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Path.GetDirectoryName(_bindings.RegistryPath)!, "benches.db"), Pooling = false }.ToString());
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE meta SET instance=$instance;";
            command.Parameters.AddWithValue("$instance", Guid.NewGuid().ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            var replacement = (await Twig.Store.Sqlite.SqliteStore.CreateAsync(Path.Combine(native.Paths.RepoRoot, "replacement.db"))).Value;
            (await replacement.PinWorkspaceAsync(native.Connection)).IsSuccess.ShouldBeTrue();
            await File.WriteAllTextAsync(Path.Combine(native.Paths.TwigDir, CoreWorkspaceFactory.FileName),
                System.Text.Json.JsonSerializer.Serialize(origin.Document with { DatabasePath = replacement.DatabasePath,
                    DatabaseInstance = replacement.InstanceId }, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument));
        }
        var refused = await editor.SaveAsync(staged, default);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.Code.ShouldBe(userInstance ? ErrorCode.StorageUnavailable : ErrorCode.Conflict);
        _transport.PatchAttempts.ShouldBe(0);
        (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.Single().Changes["System.Title"].Text.ShouldBe("never lose");
    }

    [Fact]
    public async Task NativeEditorBaselineFingerprintCannotBeSubstitutedBehindTheSameOpaqueRevision()
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Baseline", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var item = (await editor.SelectAsync(key, default)).Value;
        (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("protected")), null, null, default)).IsSuccess.ShouldBeTrue();
        var stored = (await native.Store.ReadAsync(native.Connection, default)).Value;
        var draft = stored.Drafts.Single();
        var baseline = draft.Baseline!;
        var replacement = new Draft(key, draft.Type, new(key, baseline.Type,
            baseline.Fields.Set("System.Title", FieldValue.String("substituted base")), baseline.Revision,
            baseline.RelationshipsComplete, baseline.Resources, baseline.ResourcesComplete), draft.Changes);
        (await native.Store.CommitAsync(native.Connection, stored.Version, new(stored.Version + 1, stored.Items,
            stored.Relationships, [replacement], stored.Proposals, stored.Journal, stored.Mappings, stored.Benches), default)).IsSuccess.ShouldBeTrue();
        var reopened = (await editor.SelectAsync(key, default)).Value;
        var refused = await editor.SaveAsync(reopened, default);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.Code.ShouldBe(ErrorCode.Conflict);
        _transport.PatchAttempts.ShouldBe(0);
    }
}
