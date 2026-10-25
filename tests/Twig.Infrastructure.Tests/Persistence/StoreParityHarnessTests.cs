using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class StoreParityHarnessTests
{
    private const string ResourceScope = "ado:test-scope";

    [Fact]
    public void Inspect_ReportsAllChecksForPopulatedConsistentClasses()
    {
        using var store = CreateConsistentStore();

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.FormatVersion.ShouldBe(1);
        report.IsConsistent.ShouldBeTrue();
        report.Checks.Length.ShouldBe(11);
        report.Checks.ShouldAllBe(check => check.Status == "passed");
        report.Classes.ShouldContain(entry => entry.ClassName == "main.work_item_links");
        report.Classes.ShouldContain(entry => entry.ClassName == "pending.local_relationships");
        report.Classes.ShouldContain(entry => entry.ClassName == "mirror.work-items.seeds");
        report.Classes.ShouldAllBe(entry => entry.Available && entry.CanonicalDigest != null && entry.CanonicalDigest.Length == 64);
        report.Checks.ShouldContain(check => check.Name == "external-identity-projection" && check.AffectedRows == 0);
        report.Checks.ShouldContain(check => check.Name == "dirty-pending-consistency" && check.AffectedRows == 0);
        report.Checks.ShouldContain(check => check.Name == "seed-identity-consistency" && check.AffectedRows == 0);
        report.Checks.ShouldContain(check => check.Name == "relationship-inventory-coverage" && check.AffectedRows == 0);
        report.Checks.ShouldContain(check => check.Name == "work-item-classification" && check.AffectedRows == 0);
        report.Checks.ShouldContain(check => check.Name == "tombstone-integrity" && check.AffectedRows == 0);
        report.Checks.ShouldContain(check => check.Name == "link-verification-semantics" && check.AffectedRows == 0);
        report.Checks.ShouldContain(check => check.Name == "unknown-local-keys" && check.AffectedRows == 0);
        report.Checks.ShouldContain(check => check.Name == "mirror-version" && check.AffectedRows == 0);
        report.Checks.ShouldContain(check => check.Name == "durable-version" && check.AffectedRows == 0);
        report.Anomalies.ShouldBeEmpty();
        report.Classes.Single(entry => entry.ClassName == "main.work_item_links").RowCount.ShouldBe(1);
        report.Classes.Single(entry => entry.ClassName == "pending.local_relationships").RowCount.ShouldBe(2);
    }

    [Fact]
    public void Inspect_MarksIdentityProjectionUnverifiedWithoutResolvedAdoScope()
    {
        using var store = CreateConsistentStore();

        var report = new StoreParityHarness(store).Inspect();

        report.IsConsistent.ShouldBeFalse();
        var check = report.Checks.Single(check => check.Name == "external-identity-projection");
        check.Status.ShouldBe("unverified");
        check.DetailCode.ShouldBe("ado-resource-scope-unavailable");
        report.Anomalies.ShouldNotContain(anomaly => anomaly.Code == "external-identity-inconsistent");
    }

    [Fact]
    public void Inspect_RequiresExactAdoResourceScopeForIdentityProjection()
    {
        using var store = CreateConsistentStore(identityScope: "ado:other-scope");

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "external-identity-projection").Status.ShouldBe("failed");
        report.Checks.Single(check => check.Name == "external-identity-projection").AffectedRows.ShouldBe(1);
    }

    [Fact]
    public void Inspect_ReportsExternalIdentityWithoutLocalRecord()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        Execute(store, """
            INSERT INTO pending.external_identities
                (provider_kind, resource_scope, native_id, local_id, provider_token, observed_at)
            VALUES ('ado', @scope, 'orphan', '10000000-0000-7000-8000-000000000099', NULL, 'now');
            """, ("@scope", ResourceScope));

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "external-identity-projection").Status.ShouldBe("failed");
        report.Checks.Single(check => check.Name == "external-identity-projection").AffectedRows.ShouldBe(1);
    }

    [Theory]
    [InlineData("field", "orphan-pending-field-changes")]
    [InlineData("set_field", "orphan-pending-field-changes")]
    [InlineData("state", "orphan-pending-state-changes")]
    [InlineData("note", "orphan-pending-note-changes")]
    [InlineData("add_note", "orphan-pending-note-changes")]
    [InlineData("unexpected", "orphan-pending-unknown-changes")]
    public void Inspect_ReportsOrphanPendingChangesByChangeType(string changeType, string anomalyCode)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        Execute(store, """
            INSERT INTO pending.pending_changes
                (work_item_id, change_type, field_name, old_value, new_value, created_at)
            VALUES (7123, @changeType, NULL, NULL, NULL, 'now');
            """, ("@changeType", changeType));

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "dirty-pending-consistency").Status.ShouldBe("failed");
        report.Checks.Single(check => check.Name == "dirty-pending-consistency").AffectedRows.ShouldBe(1);
        report.Anomalies.Single(anomaly => anomaly.Code == anomalyCode).Count.ShouldBe(1);
        report.Classes.Single(entry => entry.ClassName == "pending.pending_changes")
            .Anomalies.ShouldContain(anomalyCode);
    }

    [Fact]
    public void Inspect_ReportsDirtyItemWithoutPendingRows()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        InsertMirrorItem(store, id: 81, dirty: true);

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "dirty-pending-consistency").Status.ShouldBe("failed");
        report.Checks.Single(check => check.Name == "dirty-pending-consistency").AffectedRows.ShouldBe(1);
    }

    [Fact]
    public void Inspect_ReportsPendingRowsOnCleanMirrorItem()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        InsertMirrorItem(store, id: 82);
        Execute(store, """
            INSERT INTO pending.pending_changes
                (work_item_id, change_type, field_name, old_value, new_value, created_at)
            VALUES (82, 'state', 'System.State', 'To Do', 'Doing', 'now');
            """);

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "dirty-pending-consistency").Status.ShouldBe("failed");
        report.Checks.Single(check => check.Name == "dirty-pending-consistency").AffectedRows.ShouldBe(1);
    }

    [Fact]
    public void Inspect_ReportsSeedWithoutDurableIdentity()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        InsertMirrorItem(store, id: -81, seed: true, stagedIdentity: null);

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "seed-identity-consistency").Status.ShouldBe("failed");
        report.Checks.Single(check => check.Name == "seed-identity-consistency").AffectedRows.ShouldBe(1);
    }

    [Fact]
    public void Inspect_ReportsTombstoneWithoutPositiveRevision()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        Execute(store, """
            PRAGMA pending.ignore_check_constraints = ON;
            INSERT INTO pending.local_work_items
                (local_id, revision, fields_json, created_at, updated_at, deleted_at)
            VALUES ('invalid-tombstone', 0, '{}', 'now', 'now', 'now');
            """);

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "tombstone-integrity").Status.ShouldBe("failed");
        report.Checks.Single(check => check.Name == "tombstone-integrity").AffectedRows.ShouldBe(1);
    }

    [Fact]
    public void Inspect_ReportsRelationshipTombstoneWithoutPositiveRevision()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        Execute(store, """
            PRAGMA pending.ignore_check_constraints = ON;
            INSERT INTO pending.local_relationships
                (source_local_id, target_local_id, kind, revision, created_at, updated_at, deleted_at)
            VALUES ('source', 'target', 'related', 0, 'now', 'now', 'now');
            """);

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "tombstone-integrity").Status.ShouldBe("failed");
        report.Checks.Single(check => check.Name == "tombstone-integrity").AffectedRows.ShouldBe(1);
    }

    [Fact]
    public void Inspect_ReportsRemoteLinkWithoutVerification()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        Execute(store, "INSERT INTO main.work_item_links (source_id, target_id, link_type) VALUES (81, 82, 'related');");

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "link-verification-semantics").Status.ShouldBe("failed");
        report.Checks.Single(check => check.Name == "link-verification-semantics").AffectedRows.ShouldBe(1);
    }

    [Fact]
    public void Inspect_ReportsUnknownContextKeyWithoutReturningItsValue()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        Execute(store, """
            INSERT INTO main.context (key, value) VALUES ('PRIVATE-CONTEXT-KEY', 'PRIVATE-CONTEXT-VALUE');
            """);

        var report = new StoreParityHarness(store, ResourceScope).Inspect();
        var json = JsonSerializer.Serialize(report, TwigJsonContext.Default.StoreParityReport);

        report.Checks.Single(check => check.Name == "unknown-local-keys").Status.ShouldBe("failed");
        json.ShouldNotContain("PRIVATE-CONTEXT-KEY");
        json.ShouldNotContain("PRIVATE-CONTEXT-VALUE");
    }

    [Fact]
    public void Inspect_ReportsDurableVersionMismatch()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        Execute(store, $"PRAGMA pending.user_version = {SqliteCacheStore.DurableSchemaVersion - 1};");

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "durable-version").Status.ShouldBe("failed");
        report.Checks.Single(check => check.Name == "durable-version").AffectedRows.ShouldBe(1);
    }

    [Fact]
    public void Inspect_ReportsMirrorVersionMismatch()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        Execute(store, "UPDATE main.metadata SET value = '0' WHERE key = 'schema_version';");

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "mirror-version").Status.ShouldBe("failed");
        report.Checks.Single(check => check.Name == "mirror-version").AffectedRows.ShouldBe(1);
    }

    [Fact]
    public void Inspect_ReportsUnclassifiedMirrorRows()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        InsertMirrorItem(store, id: 0);

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "work-item-classification").Status.ShouldBe("failed");
        report.Checks.Single(check => check.Name == "work-item-classification").AffectedRows.ShouldBe(1);
        report.Classes.Single(entry => entry.ClassName == "mirror.work-items.unclassified").RowCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("CREATE TABLE main.unregistered_table (value TEXT);")]
    [InlineData("DROP TABLE main.work_items;")]
    [InlineData("DROP TABLE main.metadata;")]
    [InlineData("DROP TABLE pending.local_relationships;")]
    [InlineData("ALTER TABLE pending.external_identities RENAME COLUMN resource_scope TO changed_scope;")]
    public void Inspect_ReportsSchemaMismatchWithoutQueryingUnavailableTables(string schemaChange)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        Execute(store, schemaChange);

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.IsConsistent.ShouldBeFalse();
        report.Checks.Single(check => check.Name == "table-column-inventory").Status.ShouldBe("failed");
        report.Checks.Where(check => check.Name != "table-column-inventory" && check.Name != "durable-version")
            .ShouldAllBe(check => check.Status == "unverified");
        report.Checks.Where(check => check.Status == "unverified")
            .ShouldAllBe(check => check.DetailCode == "table-column-inventory-unavailable");
        report.Classes.ShouldAllBe(entry => !entry.Available && entry.RowCount == null && entry.CanonicalDigest == null);
    }

    [Fact]
    public void Inspect_ReportsAdditionalSchemaAsInventoryFailure()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        Execute(store, "CREATE TABLE main.unregistered_table (value TEXT);");

        var report = new StoreParityHarness(store, ResourceScope).Inspect();

        report.Checks.Single(check => check.Name == "table-column-inventory").Status.ShouldBe("failed");
        report.Classes.ShouldAllBe(entry => entry.CanonicalDigest == null);
    }

    [Fact]
    public void Inspect_DigestsAreOrderIndependentAndDistinguishSqliteStorageClasses()
    {
        var baseline = GetWorkItemDigest([101, 102]);
        GetWorkItemDigest([102, 101]).ShouldBe(baseline);

        GetWorkItemDigest([101], emptyParent: true).ShouldNotBe(GetWorkItemDigest([101]));
        GetWorkItemDigest([101], blobFields: true).ShouldNotBe(GetWorkItemDigest([101]));
        GetWorkItemDigest([101], fractionalRevision: true).ShouldNotBe(GetWorkItemDigest([101]));
    }

    [Fact]
    public void Inspect_DoesNotModifyMirrorOrDurableDatabaseAndWalBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "twig-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "twig.db");
        var durablePath = Path.Combine(directory, "pending.db");
        try
        {
            using var store = new SqliteCacheStore($"Data Source={databasePath}");
            Execute(store, """
                INSERT INTO main.metadata (key, value) VALUES ('diagnostic-fixture', 'read-only');
                INSERT INTO pending.local_work_items
                    (local_id, revision, fields_json, created_at, updated_at)
                VALUES ('read-only-fixture', 1, '{}', 'now', 'now');
                """);

            var beforeMirror = ReadDatabaseAndWal(databasePath);
            var beforeDurable = ReadDatabaseAndWal(durablePath);

            _ = new StoreParityHarness(store, ResourceScope).Inspect();

            var afterMirror = ReadDatabaseAndWal(databasePath);
            var afterDurable = ReadDatabaseAndWal(durablePath);
            afterMirror.Database.ShouldBe(beforeMirror.Database);
            afterMirror.Wal.ShouldBe(beforeMirror.Wal);
            afterDurable.Database.ShouldBe(beforeDurable.Database);
            afterDurable.Wal.ShouldBe(beforeDurable.Wal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SqliteCacheStore CreateConsistentStore(string identityScope = ResourceScope)
    {
        var store = new SqliteCacheStore("Data Source=:memory:");
        var localId = "10000000-0000-7000-8000-000000000071";
        var seedIdentity = "10000000-0000-7000-8000-000000000072";
        Execute(store, """
            INSERT INTO pending.local_work_items
                (local_id, revision, fields_json, created_at, updated_at)
            VALUES (@localId, 1, '{}', 'now', 'now');
            INSERT INTO pending.external_identities
                (provider_kind, resource_scope, native_id, local_id, provider_token, observed_at)
            VALUES ('ado', @scope, '71', @localId, 'token', 'now');
            INSERT INTO main.work_items
                (id, type, title, state, revision, is_seed, staged_identity, fields_json, is_dirty, last_synced_at)
            VALUES (71, 'Task', 'fixture', 'Doing', 1, 0, NULL, '{}', 1, 'now');
            INSERT INTO pending.pending_changes
                (work_item_id, change_type, field_name, old_value, new_value, created_at)
            VALUES (71, 'field', 'System.Title', 'before', 'after', 'now');
            INSERT INTO pending.pending_changes
                (work_item_id, change_type, field_name, old_value, new_value, created_at)
            VALUES (71, 'set_field', 'System.AssignedTo', NULL, 'assignee', 'now');
            INSERT INTO pending.pending_changes
                (work_item_id, change_type, field_name, old_value, new_value, created_at)
            VALUES (71, 'state', 'System.State', 'To Do', 'Doing', 'now');
            INSERT INTO pending.pending_changes
                (work_item_id, change_type, field_name, old_value, new_value, created_at)
            VALUES (71, 'note', NULL, NULL, 'note', 'now');
            INSERT INTO pending.pending_changes
                (work_item_id, change_type, field_name, old_value, new_value, created_at)
            VALUES (71, 'add_note', NULL, NULL, 'note', 'now');
            INSERT INTO pending.staged_identities (staged_identity, alias, created_at)
            VALUES (@seedIdentity, -72, 'now');
            INSERT INTO main.work_items
                (id, type, title, state, revision, is_seed, staged_identity, fields_json, is_dirty, last_synced_at)
            VALUES (-72, 'Task', 'seed-fixture', 'To Do', 1, 1, @seedIdentity, '{}', 0, 'now');
            INSERT INTO pending.local_work_items
                (local_id, revision, fields_json, created_at, updated_at, deleted_at)
            VALUES ('10000000-0000-7000-8000-000000000073', 1, '{}', 'now', 'now', 'now');
            INSERT INTO pending.local_relationships
                (source_local_id, target_local_id, kind, revision, created_at, updated_at, deleted_at)
            VALUES (@localId, '10000000-0000-7000-8000-000000000073', 'related', 1, 'now', 'now', NULL);
            INSERT INTO pending.local_relationships
                (source_local_id, target_local_id, kind, revision, created_at, updated_at, deleted_at)
            VALUES (@localId, '10000000-0000-7000-8000-000000000074', 'related', 1, 'now', 'now', 'now');
            INSERT INTO main.work_item_links (source_id, target_id, link_type) VALUES (71, 72, 'related');
            INSERT INTO main.work_item_link_verifications (source_id, verified_at) VALUES (71, 'now');
            INSERT INTO main.context (key, value) VALUES ('workspace_mode', 'work-items')
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO main.metadata (key, value) VALUES ('binding_generation', 'fixture-generation');
            """, ("@localId", localId), ("@scope", identityScope), ("@seedIdentity", seedIdentity));
        return store;
    }

    private static string GetWorkItemDigest(
        int[] insertionOrder,
        bool emptyParent = false,
        bool blobFields = false,
        bool fractionalRevision = false)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        foreach (var id in insertionOrder)
        {
            using var command = store.GetConnection().CreateCommand();
            command.CommandText = $"""
                INSERT INTO main.work_items
                    (id, type, title, state, parent_id, revision, is_seed, fields_json, is_dirty, last_synced_at)
                VALUES ({id}, 'Task', 'digest-{id}', 'To Do', @parent, @revision, 0, @fields, 0, 'now');
                """;
            command.Parameters.AddWithValue("@parent", emptyParent ? (object)string.Empty : DBNull.Value);
            command.Parameters.AddWithValue("@revision", fractionalRevision ? 1.5 : 1);
            command.Parameters.AddWithValue("@fields", blobFields ? (object)new byte[] { 0x7B, 0x7D } : "{}");
            command.ExecuteNonQuery();
        }

        return new StoreParityHarness(store).Inspect().Classes
            .Single(entry => entry.ClassName == "mirror.work-items.remote-snapshot")
            .CanonicalDigest!;
    }

    private static void InsertMirrorItem(
        SqliteCacheStore store, int id, bool dirty = false, bool seed = false, string? stagedIdentity = null)
    {
        Execute(store, """
            INSERT INTO main.work_items
                (id, type, title, state, revision, is_seed, staged_identity, fields_json, is_dirty, last_synced_at)
            VALUES (@id, 'Task', 'fixture', 'To Do', 1, @seed, @stagedIdentity, '{}', @dirty, 'now');
            """, ("@id", id), ("@seed", seed ? 1 : 0), ("@stagedIdentity", (object?)stagedIdentity ?? DBNull.Value),
            ("@dirty", dirty ? 1 : 0));
    }

    private static void Execute(
        SqliteCacheStore store,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = store.GetConnection().CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static (byte[] Database, byte[]? Wal) ReadDatabaseAndWal(string databasePath)
        => (ReadSharedBytes(databasePath), File.Exists(databasePath + "-wal") ? ReadSharedBytes(databasePath + "-wal") : null);

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
