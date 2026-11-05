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
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task NativeCaptureInterruptedBeforeCoreStageCanCancelOrRestageOnlyExactIntent(bool cancel, bool changed)
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Local recovery", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "43");
        var item = (await editor.SelectAsync(key, default)).Value;
        var ready = (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("prior debt")),
            null, [new(other, "Native.Association", true)], default)).Value;
        await CaptureSql(_bindings.RegistryPath, """
            CREATE TRIGGER interrupt_stage BEFORE UPDATE OF admitted ON editor_capture_refs WHEN new.admitted=1
            BEGIN SELECT RAISE(ABORT,'synthetic pre-Core-stage interruption'); END;
            """);
        var interrupted = await editor.StageAsync(ready, FieldValues.Empty.Set("System.Title", FieldValue.String("new intended")),
            null, [], default);
        interrupted.IsSuccess.ShouldBeFalse();
        await CaptureSql(_bindings.RegistryPath, "DROP TRIGGER interrupt_stage;");
        (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.Single().Changes["System.Title"].Text.ShouldBe("prior debt");
        using var reopened = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var pending = (await reopened.SelectAsync(key, default)).Value;
        pending.Capture!.Ready.ShouldBeFalse();
        if (changed)
            (await origin.Workspace.StageEditAsync(key, FieldValues.Empty.Set("System.Title", FieldValue.String("later debt")), default)).IsSuccess.ShouldBeTrue();
        var result = await reopened.ResolveInterruptedAsync(pending, cancel, default);
        result.IsSuccess.ShouldBe(!changed, result.IsSuccess ? "" : result.Error.Message);
        var stored = (await native.Store.ReadAsync(native.Connection, default)).Value;
        stored.Drafts.Single().Changes["System.Title"].Text.ShouldBe(changed ? "later debt" : cancel ? "prior debt" : "new intended");
        var current = (await user.ReadEditorCapturesAsync(default)).Value.Single();
        if (!changed)
        {
            current.Ready.ShouldBeTrue();
            UserBenchStore.ParseIntent(current.IntentJson).Operations.Single().Links.Length.ShouldBe(cancel ? 1 : 0);
        }
        else current.Ready.ShouldBeFalse();
        _transport.PatchAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task NativeCaptureUserCommitBeforeRegistryConfirmationRequiresPositiveHistoryRecovery()
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Metadata recovery", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var item = (await editor.SelectAsync(key, default)).Value;
        await CaptureSql(_bindings.RegistryPath, """
            CREATE TRIGGER interrupt_confirmation BEFORE UPDATE OF confirmed ON editor_capture_refs WHEN new.confirmed=1
            BEGIN SELECT RAISE(ABORT,'synthetic metadata-confirmation interruption'); END;
            """);
        (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("retained exact payload")),
            null, null, default)).IsSuccess.ShouldBeFalse();
        await CaptureSql(_bindings.RegistryPath, "DROP TRIGGER interrupt_confirmation;");
        (await user.ReadEditorCapturesAsync(default)).IsSuccess.ShouldBeFalse();
        (await user.ConfirmInterruptedHistoryAsync(default)).IsSuccess.ShouldBeTrue();
        var pending = (await editor.SelectAsync(key, default)).Value;
        (await editor.ResolveInterruptedAsync(pending, cancel: false, default)).IsSuccess.ShouldBeTrue();
        (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.Single().Changes["System.Title"].Text.ShouldBe("retained exact payload");
        _transport.PatchAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCaptureReserveAndPostCoreCrashWindowsNeverInventMissingPayloadOrReplay(bool afterCore)
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Crash windows", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var item = (await editor.SelectAsync(key, default)).Value;
        if (!afterCore) item = (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("prior")),
            null, null, default)).Value;
        var database = afterCore ? _bindings.RegistryPath : Path.Combine(Path.GetDirectoryName(_bindings.RegistryPath)!, "benches.db");
        await CaptureSql(database, afterCore
            ? """CREATE TRIGGER stop_capture BEFORE INSERT ON editor_capture_refs WHEN json_extract(new.document,'$.Revision')=2 BEGIN SELECT RAISE(ABORT,'post-Core metadata stop'); END;"""
            : """CREATE TRIGGER stop_capture BEFORE UPDATE ON editor_captures BEGIN SELECT RAISE(ABORT,'pre-user-commit stop'); END;""");
        (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("exact new")), null, null, default)).IsSuccess.ShouldBeFalse();
        await CaptureSql(database, "DROP TRIGGER stop_capture;");
        if (afterCore)
        {
            var pending = (await editor.SelectAsync(key, default)).Value;
            (await editor.RecoverCaptureAsync(pending, default)).IsSuccess.ShouldBeTrue();
            (await user.ReadEditorCapturesAsync(default)).Value.Single().Ready.ShouldBeTrue();
        }
        else
        {
            (await user.ReadEditorCapturesAsync(default)).IsSuccess.ShouldBeFalse();
            (await user.ConfirmInterruptedHistoryAsync(default)).IsSuccess.ShouldBeFalse("an absent pending payload cannot be manufactured from its reference");
            (await user.ReadEditorCapturesAsync(default, inspect: true)).Value.Single().Ready.ShouldBeTrue();
        }
        (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.Single().Changes["System.Title"].Text.ShouldBe(afterCore ? "exact new" : "prior");
        _transport.PatchAttempts.ShouldBe(0);
    }

    private static async Task CaptureSql(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
