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
    public async Task NativeCaptureWholeDatabaseRewindCannotHideSurvivingRelationshipIntent(bool userDatabase)
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Independent rewind", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        using var editor = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "43");
        var item = (await editor.SelectAsync(key, default)).Value;
        var metadata = userDatabase ? Path.Combine(Path.GetDirectoryName(_bindings.RegistryPath)!, "benches.db") : _bindings.RegistryPath;
        var backup = Path.Combine(native.Paths.RepoRoot, "synthetic-inspection.db");
        await CopyDatabase(metadata, backup);
        var staged = await editor.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("surviving intent")),
            null, [new(other, "Native.Association", true)], default);
        staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
        var userIdentity = (await user.ReadAsync()).Value.InstanceId;
        await CopyDatabase(backup, metadata);
        (await user.ReadAsync()).Value.InstanceId.ShouldBe(userIdentity);
        (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.Single().Changes["System.Title"].Text.ShouldBe("surviving intent");
        var reopenedUser = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        using var reopened = (await NativeEditor.OpenAsync(factory, reopenedUser, bench.Id, origin.Source, default)).Value;
        var selected = await reopened.SelectAsync(key, default);
        selected.IsSuccess.ShouldBeFalse("either independently rewound metadata store must quarantine the namespace");
        selected.Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        var oldOut = Console.Out; var oldError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            RunCoreLink(native.Paths, ["update", "title", "must not flush", "--id", "42", "-o", "json"]).ShouldBe(1);
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        _transport.PatchAttempts.ShouldBe(0);
    }

    private static async Task CopyDatabase(string from, string to)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = from, Pooling = false }.ToString());
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = to, Pooling = false }.ToString());
        await source.OpenAsync(); await target.OpenAsync();
        source.BackupDatabase(target);
    }
}
