using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services;
using Twig.Core;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativeCaptureRegistryMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-capture-upgrade-" + Guid.NewGuid().ToString("N"));
    private string Registry => Path.Combine(root, "system.db");

    [Fact]
    public async Task ActualElevenUpgradePreservesAllPriorRowsAndLeavesNewProvenancePending()
    {
        await Eleven();
        var before = await Rows();
        using var upgraded = new SqliteSystemWorktreeRegistry(Registry, TimeProvider.System);
        (await upgraded.ReadCoreGateAsync(default)).IsSuccess.ShouldBeTrue();
        (await Sql("SELECT version FROM layout_meta;")).ShouldBe("12");
        (await Rows()).ShouldBe(before);
        (await NativeCoreExecution.PublishingAsync(Registry, default)).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedElevenUpgradeLeavesElevenAndNoPartialNewSchema(bool corruptPrerequisite)
    {
        await Eleven();
        if (corruptPrerequisite) await Sql("DROP TABLE core_attempt_refs;");
        else await Sql("CREATE TRIGGER stop_twelve BEFORE UPDATE OF version ON layout_meta WHEN new.version=12 BEGIN SELECT RAISE(ABORT,'synthetic interruption'); END;");
        var before = await Rows();
        using var failed = new SqliteSystemWorktreeRegistry(Registry, TimeProvider.System);
        (await failed.ReadCoreGateAsync(default)).IsSuccess.ShouldBeFalse();
        (await Sql("SELECT version FROM layout_meta;")).ShouldBe("11");
        (await Sql("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'editor_%';")).ShouldBe("0");
        (await Rows()).ShouldBe(before);
    }

    [Fact]
    public async Task ConcurrentTwelveOpensNeverDowngradeOrReplaceOldIdentity()
    {
        await Eleven();
        using var first = new SqliteSystemWorktreeRegistry(Registry, TimeProvider.System);
        using var second = new SqliteSystemWorktreeRegistry(Registry, TimeProvider.System);
        var opened = await Task.WhenAll(first.ReadCoreGateAsync(default), second.ReadCoreGateAsync(default));
        opened.ShouldAllBe(value => value.IsSuccess);
        opened[0].Value.Instance.ShouldBe(opened[1].Value.Instance);
        (await Sql("SELECT version FROM layout_meta;")).ShouldBe("12");
    }

    private async Task Eleven()
    {
        Directory.CreateDirectory(root);
        using (var registry = new SqliteSystemWorktreeRegistry(Registry, TimeProvider.System))
            (await registry.UpsertConnectionAsync("fixture", "org", "project", null)).IsSuccess.ShouldBeTrue();
        var connection = new Connection(new("ado"), "collection", "project", "actor");
        var store = (await SqliteStore.CreateAsync(Path.Combine(root, "origin.db"))).Value;
        (await store.PinWorkspaceAsync(connection)).IsSuccess.ShouldBeTrue();
        var source = new NativeCoreStoreReference(store.InstanceId, store.DatabasePath, root, connection);
        (await NativeCoreExecution.RegisterAsync(Registry, source, default)).IsSuccess.ShouldBeTrue();
        var inventory = (await NativeCoreExecution.InventoryAsync(Registry, explicitStores: [source])).Value;
        (await NativeCoreExecution.ActivateAsync(Registry, inventory, inventory.Digest, true, true)).IsSuccess.ShouldBeTrue();
        var user = UserBenchStore.ForRegistry(Registry);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Retained birth", []), 0)).Value;
        var id = (await user.ReadAsync()).Value.InstanceId;
        var mode = (await user.SetPlanModeAsync(id, bench.Id, bench.Revision, 0, true)).Value;
        var plan = (await user.ReservePlanScopeAsync(id, bench.Id, mode.Revision, root,
            new(connection, store.InstanceId, "actor", "authority"))).Value;
        (await user.CompleteCoveredPlanScopeAsync(id, bench.Id, plan.Revision, plan.Scopes.Single().Id, Registry, source))
            .IsSuccess.ShouldBeTrue();
        await Sql("""
            INSERT INTO worktrees VALUES('fixture-root','fixture','fixture-root','2026-01-01','2026-01-01',NULL);
            INSERT INTO claims VALUES('kept','fixture','fixture-root','item',42,'ended','token','2026-01-01','2026-01-01','{"opaque":"original bytes"}');
            INSERT INTO connection_remote_write_intents(intent_id,request_digest,worktree_fingerprint,intent_json)
                VALUES('intent','digest','fixture-root','{"unresolved":"transport"}');
            INSERT INTO connection_remote_write_receipts VALUES('intent','receipt','{"ack":"not verified"}');
            UPDATE core_execution_meta SET active=1,digest=printf('%064d',7),revision=3;
            DROP TABLE IF EXISTS editor_capture_refs; DROP TABLE IF EXISTS editor_user_heads; DROP TABLE IF EXISTS editor_provenance_meta;
            UPDATE layout_meta SET version=11;
            """);
    }

    private async Task<string> Rows()
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Registry, Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'editor_%' AND name<>'layout_meta' ORDER BY name;";
        var names = new List<string>();
        using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        var rows = new List<string>();
        foreach (var name in names)
        {
            command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\";";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(name + ":" + JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(index =>
                    reader.GetValue(index) is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(reader.GetValue(index),
                        System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
        }
        return string.Join("\n", rows.Order(StringComparer.Ordinal));
    }

    private async Task<string> Sql(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Registry, Pooling = false }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }

    public void Dispose()
    {
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Registry, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 30 }.ToString());
        SqliteConnection.ClearPool(pool);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
