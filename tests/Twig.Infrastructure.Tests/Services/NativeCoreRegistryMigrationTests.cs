using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativeCoreRegistryMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-core-registry-upgrade-" + Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(root, "system.db");
    public void Dispose()
    {
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = PathName, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 30 }.ToString());
        SqliteConnection.ClearPool(pool);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task RealSupportedUpgradePreservesExistingBytesAndNeverAutoactivates(int version)
    {
        await Ten();
        await Execute($"UPDATE layout_meta SET version={version} WHERE id=1;");
        var before = await Scalar("SELECT record_json FROM claims WHERE claim_id='preserved';");
        var transport = await Scalar("SELECT intent_json FROM connection_remote_write_intents WHERE intent_id='preserved-intent';");
        var acknowledged = await Scalar("SELECT receipt_json FROM connection_remote_write_receipts WHERE intent_id='preserved-intent';");
        using var upgraded = new SqliteSystemWorktreeRegistry(PathName, TimeProvider.System);
        var gate = await upgraded.ReadCoreGateAsync(default);
        gate.IsSuccess.ShouldBeTrue(gate.IsSuccess ? "" : gate.Error.Message);
        gate.Value.Active.ShouldBeFalse();
        (await Scalar("SELECT version FROM layout_meta WHERE id=1;")).ShouldBe("11");
        (await Scalar("SELECT record_json FROM claims WHERE claim_id='preserved';")).ShouldBe(before);
        (await Scalar("SELECT intent_json FROM connection_remote_write_intents WHERE intent_id='preserved-intent';")).ShouldBe(transport);
        (await Scalar("SELECT receipt_json FROM connection_remote_write_receipts WHERE intent_id='preserved-intent';")).ShouldBe(acknowledged);
    }

    [Fact]
    public async Task UpgradeFailureRollsBackVersionAndAllNewClaimTables()
    {
        await Ten();
        await Execute("CREATE TRIGGER reject_upgrade BEFORE UPDATE OF version ON layout_meta WHEN new.version=11 BEGIN SELECT RAISE(ABORT,'fixture rejected upgrade'); END;");
        using var failed = new SqliteSystemWorktreeRegistry(PathName, TimeProvider.System);
        (await failed.ReadCoreGateAsync(default)).IsSuccess.ShouldBeFalse();
        (await Scalar("SELECT version FROM layout_meta WHERE id=1;")).ShouldBe("10");
        (await Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name IN('core_execution_meta','core_store_refs','core_attempt_refs');")).ShouldBe("0");
        (await Scalar("SELECT record_json FROM claims WHERE claim_id='preserved';")).ShouldBe("{\"original\":\"byte-preserved\"}");
    }

    [Fact]
    public async Task ConcurrentRepeatedOpensDoNotDowngradeOrRecreateActivationIdentity()
    {
        await Ten();
        using var first = new SqliteSystemWorktreeRegistry(PathName, TimeProvider.System);
        using var second = new SqliteSystemWorktreeRegistry(PathName, TimeProvider.System);
        var gates = await Task.WhenAll(first.ReadCoreGateAsync(default), second.ReadCoreGateAsync(default));
        gates.ShouldAllBe(value => value.IsSuccess);
        gates[0].Value.Instance.ShouldBe(gates[1].Value.Instance);
        (await Scalar("SELECT version FROM layout_meta WHERE id=1;")).ShouldBe("11");
        (await first.ReadCoreGateAsync(default)).Value.Instance.ShouldBe(gates[0].Value.Instance);
    }

    private async Task Ten()
    {
        Directory.CreateDirectory(root);
        using (var native = new SqliteSystemWorktreeRegistry(PathName, TimeProvider.System))
            (await native.UpsertConnectionAsync("fixture", "org", "project", null)).IsSuccess.ShouldBeTrue();
        await Execute("""
            INSERT INTO worktrees VALUES('fixture-root','fixture','fixture-root','2026-01-01','2026-01-01',NULL);
            INSERT INTO claims VALUES('preserved','fixture','fixture-root','item',42,'ended','token','2026-01-01','2026-01-01','{"original":"byte-preserved"}');
            INSERT INTO connection_remote_write_intents(intent_id,request_digest,worktree_fingerprint,intent_json)
                VALUES('preserved-intent','original-digest','fixture-root','{"original":"unresolved transport"}');
            INSERT INTO connection_remote_write_receipts VALUES('preserved-intent','preserved-receipt','{"status":"acknowledged-not-verified"}');
            DROP TABLE core_attempt_refs; DROP TABLE core_store_refs; DROP TABLE core_execution_meta;
            UPDATE layout_meta SET version=10 WHERE id=1;
            """);
    }

    private async Task Execute(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathName, Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> Scalar(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathName, Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }
}
