using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativeCoreRegistryPersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-current-registry-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(root, "system.db");

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(14)]
    public async Task UnsupportedSchemaDoesNotMutateRegistry(int version)
    {
        Directory.CreateDirectory(root);
        using (var db = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE layout_meta(id INTEGER PRIMARY KEY,version INTEGER NOT NULL);
                INSERT INTO layout_meta VALUES(1,{version});
                CREATE TABLE uncertain_effects(evidence TEXT NOT NULL);
                INSERT INTO uncertain_effects VALUES('original unknown remote effect');
                """;
            await command.ExecuteNonQueryAsync();
        }
        File.Exists(Database + "-wal").ShouldBeFalse();
        var before = await File.ReadAllBytesAsync(Database);
        using var registry = new SqliteSystemWorktreeRegistry(Database, TimeProvider.System);
        var result = await registry.ReadCoreGateAsync(default);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain(AttachmentStorageFailure.SystemStoreSchemaMismatch);
        (await File.ReadAllBytesAsync(Database)).ShouldBe(before);
        File.Exists(Database + "-wal").ShouldBeFalse();
        File.Exists(Database + "-shm").ShouldBeFalse();
    }

    [Fact]
    public async Task FreshInitializationAndCurrentReopenRetainIdentity()
    {
        Guid identity;
        using (var registry = new SqliteSystemWorktreeRegistry(Database, TimeProvider.System))
        {
            var fresh = await registry.ReadCoreGateAsync(default);
            fresh.IsSuccess.ShouldBeTrue();
            fresh.Value.Active.ShouldBeFalse();
            fresh.Value.Sources.ShouldBeEmpty();
            identity = fresh.Value.Instance;
            identity.ShouldNotBe(Guid.Empty);
        }
        using var reopened = new SqliteSystemWorktreeRegistry(Database, TimeProvider.System);
        var current = await reopened.ReadCoreGateAsync(default);
        current.IsSuccess.ShouldBeTrue();
        current.Value.Instance.ShouldBe(identity);
        using var db = new SqliteConnection($"Data Source={Database};Pooling=False");
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master WHERE type='table'
            AND name IN('core_scope_births','editor_capture_refs','connection_activations');
            """;
        Convert.ToInt32(await command.ExecuteScalarAsync()).ShouldBe(3);
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN('profile_cache','tuple_epochs','connection_migrations');";
        Convert.ToInt32(await command.ExecuteScalarAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task RegistryDisposalReleasesDatabaseForExclusiveAccess()
    {
        using (var registry = new SqliteSystemWorktreeRegistry(Database, TimeProvider.System))
        {
            var opened = await registry.ReadCoreGateAsync(default);
            opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
        }
        using var exclusive = new FileStream(Database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        exclusive.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ConcurrentFreshInitializationSharesIdentity()
    {
        using var first = new SqliteSystemWorktreeRegistry(Database, TimeProvider.System);
        using var second = new SqliteSystemWorktreeRegistry(Database, TimeProvider.System);
        var gates = await Task.WhenAll(Task.Run(() => first.ReadCoreGateAsync(default)),
            Task.Run(() => second.ReadCoreGateAsync(default)));
        gates.ShouldAllBe(gate => gate.IsSuccess);
        gates[0].Value.Instance.ShouldBe(gates[1].Value.Instance);
        (await first.ReadCoreGateAsync(default)).Value.Instance.ShouldBe(gates[0].Value.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
