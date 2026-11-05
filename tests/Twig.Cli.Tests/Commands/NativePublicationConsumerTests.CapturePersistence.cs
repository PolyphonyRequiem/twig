using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;
using Model = Twig.Core.Core;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeEditorJointlyMissingCaptureTablesNeverBecomeFirstUseOnReopen(bool sealedLegacyStorage)
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        (await user.ReadEditorCapturesAsync(default)).Value.ShouldBeEmpty("genuine first use remains empty");
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Missing capture storage", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "43");
        var item = (await editor.SelectAsync(key, default)).Value;
        var staged = (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("protected")),
            null, [new(other, "Native.Association", true)], default)).Value;
        UserBenchStore.ParseIntent(staged.Capture!.IntentJson).Operations.Single().Links.Length.ShouldBe(1);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(Path.GetDirectoryName(_bindings.RegistryPath)!, "benches.db"), Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            using var command = db.CreateCommand();
            if (sealedLegacyStorage)
            {
                command.CommandText = "ALTER TABLE meta DROP COLUMN editor_initialized;";
                await command.ExecuteNonQueryAsync();
                var legacy = await UserBenchStore.ForRegistry(_bindings.RegistryPath).ReadEditorCapturesAsync(default);
                legacy.IsSuccess.ShouldBeTrue(legacy.IsSuccess ? "" : legacy.Error.Message);
                legacy.Value.Single().SameAs(staged.Capture).ShouldBeTrue();
            }
            command.CommandText = "DROP TABLE editor_captures; DROP TABLE editor_capture_meta;";
            await command.ExecuteNonQueryAsync();
        }
        var reopenedUser = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        using var reopened = (await NativeEditor.OpenAsync(factory, reopenedUser, bench.Id, origin.Source, default)).Value;
        var selected = await reopened.SelectAsync(key, default);
        if (selected.IsSuccess)
            (await reopened.StageAsync(selected.Value, FieldValues.Empty, null, null, default)).IsSuccess.ShouldBeFalse(
                "lost relationship metadata must not permit a new field-only capture");
        selected.IsSuccess.ShouldBeFalse();
        selected.Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        (await reopenedUser.SaveEditorCaptureAsync(staged.Capture with { Revision = 1 }, 0, default)).IsSuccess.ShouldBeFalse();
        (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.Single().Changes["System.Title"].Text.ShouldBe("protected");
        _transport.PatchAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeEditorStoredDocumentBoundUsesTheActualEscapedRichTextShape(bool over)
    {
        const int limit = 2 * 1024 * 1024;
        var native = await RelationshipWorkspaceAsync();
        _transport.RichTextEnabled = true;
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Document bound", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "43");
        var item = (await editor.SelectAsync(key, default)).Value;
        var staged = (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("protected")),
            null, [new(other, "Native.Association", true)], default)).Value;
        var before = (await native.Store.ReadAsync(native.Connection, default)).Value;
        var previous = staged.Capture!;
        var intent = UserBenchStore.ParseIntent(previous.IntentJson);
        NativeEditorCapture Candidate(int slashes, int padding)
        {
            var op = intent.Operations.Single();
            var fields = op.Fields.Set("System.Description", FieldValue.RichText(
                "<pre>" + new string('\\', slashes) + new string('a', padding) + "</pre>", FieldKind.Html));
            var proposal = new Proposal(intent.Connection, intent.SchemaFingerprint, intent.ExpectedStoreVersion,
                [new(op.Id, op.Kind, op.Item, op.ExpectedRevision, fields, op.Type, concurrency: op.Concurrency, links: op.Links)]);
            return previous with { Revision = previous.Revision + 1, Ready = false,
                IntentJson = Encoding.UTF8.GetString(Model.ProposalBytes(proposal)),
                DraftFingerprint = origin.Workspace.DraftFingerprint(new(key, before.Drafts.Single().Type,
                    before.Drafts.Single().Baseline, fields)) };
        }
        int Size(NativeEditorCapture capture) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(capture, NativeEditorJson.Default.NativeEditorCapture));
        var low = 0;
        var high = 1024 * 1024 / 2;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Size(Candidate(middle, 0)) <= limit) low = middle; else high = middle - 1;
        }
        var exact = Candidate(low, limit - Size(Candidate(low, 0)));
        var candidate = over ? Candidate(low, limit + 1 - Size(Candidate(low, 0))) : exact;
        Size(candidate).ShouldBe(limit + (over ? 1 : 0));
        Encoding.UTF8.GetByteCount(candidate.IntentJson).ShouldBeLessThanOrEqualTo(1024 * 1024,
            "the existing canonical-intent bound must not mask the real stored-document bound");
        var result = await user.SaveEditorCaptureAsync(candidate, previous.Revision, default);
        result.IsSuccess.ShouldBe(!over);
        if (over) result.Error.Code.ShouldBe(ErrorCode.InvalidInput);
        var readable = await user.ReadEditorCapturesAsync(default);
        readable.IsSuccess.ShouldBeTrue(readable.IsSuccess ? "" : readable.Error.Message);
        readable.Value.Single().SameAs(over ? previous : candidate).ShouldBeTrue();
        var after = (await native.Store.ReadAsync(native.Connection, default)).Value;
        after.Version.ShouldBe(before.Version);
        after.Drafts.Single().Changes.ShouldBe(before.Drafts.Single().Changes);
        if (over)
        {
            var rejectedStage = await editor.StageAsync(staged, UserBenchStore.ParseIntent(candidate.IntentJson).Operations.Single().Fields,
                null, null, default);
            rejectedStage.IsSuccess.ShouldBeFalse();
            rejectedStage.Error.Code.ShouldBe(ErrorCode.InvalidInput);
            (await user.ReadEditorCapturesAsync(default)).Value.Single().SameAs(previous).ShouldBeTrue();
            (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.Single().Changes.ShouldBe(before.Drafts.Single().Changes);
        }
        else
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Path.GetDirectoryName(_bindings.RegistryPath)!, "benches.db"), Pooling = false }.ToString());
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT length(CAST(document AS BLOB)) FROM editor_captures;";
            Convert.ToInt64(await command.ExecuteScalarAsync()).ShouldBe(limit);
        }
        _transport.PatchAttempts.ShouldBe(0);
    }
}
