using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeCaptureIncompleteBirthCannotBeInventoriedOrActivated(bool retainedPayload)
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Incomplete birth", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var item = (await editor.SelectAsync(key, default)).Value;
        var ready = (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("protected intent")),
            null, null, default)).Value;
        ready.Capture.ShouldNotBeNull().Ready.ShouldBeTrue();
        var confirmed = await NativeCoreExecution.InventoryAsync(_bindings.RegistryPath);
        confirmed.IsSuccess.ShouldBeTrue();
        var metadata = Path.Combine(Path.GetDirectoryName(_bindings.RegistryPath)!, "benches.db");
        await CaptureSql(metadata, """
            DROP TABLE editor_capture_history;
            ALTER TABLE meta DROP COLUMN editor_registry;
            ALTER TABLE meta DROP COLUMN editor_generation;
            ALTER TABLE meta DROP COLUMN editor_head;
            """);
        if (!retainedPayload) await CaptureSql(metadata, "DROP TABLE editor_captures; DROP TABLE editor_capture_meta; ALTER TABLE meta DROP COLUMN editor_initialized;");
        await CaptureSql(_bindings.RegistryPath, """
            DROP TABLE editor_capture_refs; DROP TABLE editor_user_heads; DROP TABLE editor_provenance_meta;
            UPDATE layout_meta SET version=11;
            """);
        await using var registryEvidence = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _bindings.RegistryPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await registryEvidence.OpenAsync();
        await using var metadataEvidence = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = metadata, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await metadataEvidence.OpenAsync();
        var registryBefore = await CaptureDataVersion(registryEvidence);
        var metadataBefore = await CaptureDataVersion(metadataEvidence);
        (await NativeCoreExecution.PublishingAsync(_bindings.RegistryPath, default)).IsSuccess.ShouldBeFalse();
        (await NativeCoreExecution.InventoryAsync(_bindings.RegistryPath)).IsSuccess.ShouldBeFalse();
        (await NativeCoreExecution.ActivateAsync(_bindings.RegistryPath, confirmed.Value, confirmed.Value.Digest, true, true)).IsSuccess.ShouldBeFalse();
        (await user.ReadEditorCapturesAsync(default)).IsSuccess.ShouldBeFalse();
        (await user.ReadEditorCapturesAsync(default, inspect: true)).IsSuccess.ShouldBeFalse();
        (await CaptureDataVersion(registryEvidence)).ShouldBe(registryBefore, "refusal must not commit registry repairs, including in the WAL");
        (await CaptureDataVersion(metadataEvidence)).ShouldBe(metadataBefore, "refusal must not commit capture-store repairs, including in the WAL");
        _transport.PatchAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData("DELETE FROM editor_capture_history WHERE generation=1;", false)]
    [InlineData("UPDATE editor_capture_history SET before_document='{}' WHERE generation=2;", false)]
    [InlineData("DELETE FROM editor_capture_refs WHERE generation=1;", true)]
    public async Task NativeCaptureMissingBidirectionalHistoryCannotAuthorizeNewCaptureOrPublication(string damage, bool registry)
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "History integrity", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var item = (await editor.SelectAsync(ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42"), default)).Value;
        (await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("retained")), null, null, default)).IsSuccess.ShouldBeTrue();
        await CaptureSql(registry ? _bindings.RegistryPath : Path.Combine(Path.GetDirectoryName(_bindings.RegistryPath)!, "benches.db"), damage);
        (await user.ReadEditorCapturesAsync(default)).IsSuccess.ShouldBeFalse();
        (await NativeCoreExecution.PublishingAsync(_bindings.RegistryPath, default)).IsSuccess.ShouldBeFalse();
        _transport.PatchAttempts.ShouldBe(0);
    }

    private static async Task<long> CaptureDataVersion(SqliteConnection database)
    {
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA data_version;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
