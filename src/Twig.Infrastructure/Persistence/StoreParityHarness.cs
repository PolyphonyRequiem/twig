using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

internal sealed class StoreParityHarness(
    SqliteCacheStore store, string? adoResourceScope = null, bool preserveOpaqueContext = false)
{
    private const int ReportFormatVersion = 1;

    internal StoreParityReport Inspect()
    {
        ArgumentNullException.ThrowIfNull(store);
        using var operation = store.AcquireOperation();
        var connection = store.GetConnection();
        using var ownedTransaction = store.ActiveTransaction is null ? connection.BeginTransaction(deferred: true) : null;
        var transaction = store.ActiveTransaction ?? ownedTransaction;
        if (transaction is null)
            throw new InvalidOperationException("The store parity inspection could not acquire a read transaction.");

        var schemaMatches = CheckSchemaInventory(connection, transaction);
        var mirrorVersion = schemaMatches ? ReadMirrorSchemaVersion(connection, transaction) : null;
        var durableVersion = ReadDurableSchemaVersion(connection, transaction);
        var classes = ReadClasses(connection, transaction, schemaMatches);

        var identityCount = schemaMatches && adoResourceScope is not null
            ? CountExternalIdentityMismatches(connection, transaction, adoResourceScope)
            : 0;
        var dirtyCount = schemaMatches ? CountDirtyPendingMismatches(connection, transaction) : 0;
        var seedCount = schemaMatches
            ? Count(connection, transaction, """
                SELECT
                    (SELECT COUNT(*) FROM main.work_items AS w
                     WHERE (w.is_seed = 1 OR w.id < 0)
                       AND (w.staged_identity IS NULL OR NOT EXISTS (
                           SELECT 1 FROM pending.staged_identities AS s
                           WHERE s.staged_identity = w.staged_identity AND s.alias = w.id
                       )))
                  + (SELECT COUNT(*) FROM pending.seed_links AS s
                     WHERE (s.source_id < 0 AND NOT EXISTS (
                               SELECT 1 FROM pending.staged_identities AS i WHERE i.alias = s.source_id
                           ))
                        OR (s.target_id < 0 AND NOT EXISTS (
                               SELECT 1 FROM pending.staged_identities AS i WHERE i.alias = s.target_id
                           )))
                  + (SELECT COUNT(*) FROM pending.publish_id_map AS p
                     WHERE p.old_id < 0 AND (p.staged_identity IS NULL OR NOT EXISTS (
                         SELECT 1 FROM pending.staged_identities AS s
                         WHERE s.staged_identity = p.staged_identity AND s.alias = p.old_id
                     )))
                  + (SELECT COUNT(*) FROM pending.seed_local_identities AS m
                     LEFT JOIN pending.local_work_items AS l ON l.local_id=m.local_id
                     LEFT JOIN pending.staged_identities AS s ON s.staged_identity=m.staged_identity
                     WHERE l.local_id IS NULL OR s.staged_identity IS NULL OR m.local_id=m.staged_identity)
                  + (SELECT COUNT(*) FROM pending.seed_work_items AS w
                     LEFT JOIN pending.seed_local_identities AS m ON m.staged_identity=w.staged_identity
                     LEFT JOIN pending.local_work_items AS l ON l.local_id=w.local_id
                     LEFT JOIN pending.staged_identities AS s ON s.staged_identity=w.staged_identity
                     WHERE m.local_id IS NULL OR m.local_id<>w.local_id
                        OR l.local_id IS NULL OR l.deleted_at IS NOT NULL
                        OR s.alias<>w.id OR s.staged_identity IS NULL);
                """)
            : 0;
        var relationshipInventoryCovered =
            PreservationInventory.AllTables.Any(t => t.Schema == PreservationInventory.MirrorSchema && t.Table == "work_item_links")
            && PreservationInventory.AllTables.Any(t => t.Schema == PreservationInventory.DurableSchema && t.Table == "local_relationships")
            && PreservationInventory.AllRowSets.Any(rowSet => rowSet.ClassName == "main.work_item_links")
            && PreservationInventory.AllRowSets.Any(rowSet => rowSet.ClassName == "pending.local_relationships");
        var relationshipInventoryCount = schemaMatches && !relationshipInventoryCovered ? 1 : 0;
        var unclassifiedWorkItemCount = schemaMatches
            ? classes.Single(entry => entry.ClassName == "mirror.work-items.unclassified").RowCount ?? 0
            : 0;
        var tombstoneCount = schemaMatches
            ? Count(connection, transaction, """
                SELECT
                    (SELECT COUNT(*) FROM pending.local_work_items WHERE deleted_at IS NOT NULL AND revision < 1)
                  + (SELECT COUNT(*) FROM pending.local_relationships WHERE deleted_at IS NOT NULL AND revision < 1);
                """)
            : 0;
        var linkVerificationCount = schemaMatches
            ? Count(connection, transaction, """
                SELECT COUNT(*)
                FROM main.work_item_links AS l
                WHERE NOT EXISTS (
                    SELECT 1 FROM main.work_item_link_verifications AS v WHERE v.source_id = l.source_id
                );
                """)
            : 0;
        var unknownKeys = schemaMatches
            ? Count(connection, transaction, """
                SELECT
                    (SELECT COUNT(*) FROM main.metadata
                     WHERE key NOT IN ('binding_generation', 'process_configuration_data', 'schema_version')
                       AND key NOT LIKE 'draft-served-evidence:%');
                """)
            : 0;
        if (schemaMatches && !preserveOpaqueContext)
            unknownKeys += Count(connection, transaction, """
                SELECT COUNT(*) FROM main.context WHERE key NOT IN (
                    'active_work_item_id', 'area_tree_fetched_at', 'area_tree_json',
                    'last_refreshed_at', 'nav_history_cursor', 'workspace_mode');
                """);

        if (adoResourceScope is not null)
            _ = new ExternalWorkItemIdentity(AdoMirrorIdentityProjection.ProviderKind, adoResourceScope, "0");

        var orphanPendingAnomalies = schemaMatches
            ? ReadOrphanPendingAnomalies(connection, transaction)
            : Array.Empty<StoreParityAnomaly>();
        var checks = new[]
        {
            Check("table-column-inventory", schemaMatches, schemaMatches ? 0 : 1, "table-column-inventory-mismatch"),
            Check("external-identity-projection",
                schemaMatches ? adoResourceScope is null ? null : identityCount == 0 : null,
                identityCount, "external-identity-inconsistent",
                schemaMatches ? "ado-resource-scope-unavailable" : "table-column-inventory-unavailable"),
            Check("dirty-pending-consistency", schemaMatches ? dirtyCount == 0 : null,
                dirtyCount, "dirty-pending-inconsistent"),
            Check("seed-identity-consistency", schemaMatches ? seedCount == 0 : null,
                seedCount, "seed-identity-inconsistent"),
            Check("relationship-inventory-coverage", schemaMatches ? relationshipInventoryCount == 0 : null,
                relationshipInventoryCount, "relationship-inventory-incomplete"),
            Check("work-item-classification", schemaMatches ? unclassifiedWorkItemCount == 0 : null,
                unclassifiedWorkItemCount, "work-item-classification-unknown"),
            Check("tombstone-integrity", schemaMatches ? tombstoneCount == 0 : null,
                tombstoneCount, "tombstone-revision-invalid"),
            Check("link-verification-semantics", schemaMatches ? linkVerificationCount == 0 : null,
                linkVerificationCount, "link-verification-missing"),
            Check("unknown-local-keys", schemaMatches ? unknownKeys == 0 : null,
                unknownKeys, "unknown-local-key"),
            Check("mirror-version", schemaMatches ? mirrorVersion == SqliteCacheStore.SchemaVersion : null,
                !schemaMatches || mirrorVersion == SqliteCacheStore.SchemaVersion ? 0 : 1, "mirror-version-mismatch"),
            Check("durable-version", durableVersion == SqliteCacheStore.DurableSchemaVersion,
                durableVersion == SqliteCacheStore.DurableSchemaVersion ? 0 : 1, "durable-version-mismatch"),
        };

        var anomalies = checks
            .Where(check => check.Status == "failed")
            .Select(check => new StoreParityAnomaly(check.AnomalyCode!, check.AffectedRows))
            .Concat(orphanPendingAnomalies)
            .ToArray();
        var classAnomalies = anomalies.Select(anomaly => anomaly.Code).ToArray();
        var classesWithAnomalies = classes
            .Select(entry => entry with
            {
                Anomalies = schemaMatches
                    ? ClassAnomalies(entry.ClassName, classAnomalies)
                    : ["table-column-inventory-unavailable"],
            })
            .ToArray();

        return new StoreParityReport(
            ReportFormatVersion,
            mirrorVersion,
            durableVersion,
            checks.All(check => check.Status == "passed"),
            classesWithAnomalies,
            checks,
            anomalies);
    }

    private static StoreParityCheckResult Check(
        string name,
        bool? passed,
        long affectedRows,
        string anomalyCode,
        string? unverifiedCode = "table-column-inventory-unavailable")
        => new(
            name,
            passed switch { true => "passed", false => "failed", null => "unverified" },
            affectedRows,
            passed == false ? anomalyCode : null,
            passed is null ? unverifiedCode : null);

    private static string[] ClassAnomalies(string className, IReadOnlyCollection<string> codes)
    {
        if (codes.Count == 0)
            return [];

        IEnumerable<string> relevant = className switch
        {
            "main.work_item_links" => codes.Where(code => code == "link-verification-missing"),
            "main.context" => codes.Where(code => code == "unknown-local-key"),
            "main.metadata" => codes.Where(code => code is "unknown-local-key" or "mirror-version-mismatch"),
            "mirror.work-items.seeds" => codes.Where(code => code == "seed-identity-inconsistent"),
            "mirror.work-items.pending-edits" => codes.Where(code =>
                code is "dirty-pending-inconsistent" or "external-identity-inconsistent"),
            "mirror.work-items.remote-snapshot" => codes.Where(code => code == "external-identity-inconsistent"),
            "mirror.work-items.unclassified" => codes.Where(code => code == "work-item-classification-unknown"),
            "pending.pending_changes" => codes.Where(code =>
                code == "dirty-pending-inconsistent" || code.StartsWith("orphan-pending-", StringComparison.Ordinal)),
            "pending.staged_identities" or "pending.seed_links" or "pending.publish_id_map"
                or "pending.seed_local_identities" or "pending.seed_work_items"
                => codes.Where(code => code == "seed-identity-inconsistent"),
            "pending.local_work_items" or "pending.local_relationships"
                => codes.Where(code => code == "tombstone-revision-invalid"),
            _ => Array.Empty<string>(),
        };
        return relevant.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool CheckSchemaInventory(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var schema in new[] { PreservationInventory.MirrorSchema, PreservationInventory.DurableSchema })
        {
            var expectedTables = PreservationInventory.AllTables
                .Where(table => table.Schema == schema)
                .Select(table => table.Table)
                .ToHashSet(StringComparer.Ordinal);
            var actualTables = ReadTableNames(connection, transaction, schema);
            if (!expectedTables.SetEquals(actualTables))
                return false;
        }

        foreach (var table in PreservationInventory.AllTables)
        {
            var expected = table.Columns.ToHashSet(StringComparer.Ordinal);
            var actual = ReadColumnNames(connection, transaction, table.Schema, table.Table);
            if (!expected.SetEquals(actual))
                return false;
        }

        return true;
    }

    private static HashSet<string> ReadTableNames(SqliteConnection connection, SqliteTransaction transaction, string schema)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT name FROM {Quote(schema)}.sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static HashSet<string> ReadColumnNames(
        SqliteConnection connection, SqliteTransaction transaction, string schema, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA {Quote(schema)}.table_info({Quote(table)});";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }

    private static int? ReadMirrorSchemaVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM main.metadata WHERE key = 'schema_version';";
        var value = command.ExecuteScalar() as string;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version : null;
    }

    private static int ReadDurableSchemaVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA {Quote(PreservationInventory.DurableSchema)}.user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static StoreParityClassReport[] ReadClasses(
        SqliteConnection connection, SqliteTransaction transaction, bool schemaMatches)
    {
        var results = new List<StoreParityClassReport>();
        foreach (var rowSet in PreservationInventory.AllRowSets)
        {
            if (!schemaMatches)
            {
                results.Add(new StoreParityClassReport(rowSet.ClassName, false, null, null, []));
                continue;
            }

            var columns = string.Join(", ", rowSet.Table.Columns.Select(Quote));
            var where = rowSet.Predicate is null ? string.Empty : $" WHERE {rowSet.Predicate}";
            var order = string.Join(", ", rowSet.Table.Columns.Select(column => $"{Quote(column)} COLLATE BINARY"));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT {columns} FROM {Quote(rowSet.Table.Schema)}.{Quote(rowSet.Table.Table)}{where} ORDER BY {order};";
            using var reader = command.ExecuteReader();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var count = 0L;
            while (reader.Read())
            {
                AppendByte(hash, 0xFF);
                for (var index = 0; index < reader.FieldCount; index++)
                    AppendValue(hash, reader.GetValue(index));
                count++;
            }

            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            results.Add(new StoreParityClassReport(rowSet.ClassName, true, count, digest, []));
        }

        return results.ToArray();
    }

    private static long CountExternalIdentityMismatches(
        SqliteConnection connection, SqliteTransaction transaction, string resourceScope)
    {
        var identity = new ExternalWorkItemIdentity(AdoMirrorIdentityProjection.ProviderKind, resourceScope, "0");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM main.work_items AS w
                 WHERE w.id > 0 AND w.is_seed = 0
                   AND NOT EXISTS (
                       SELECT 1 FROM pending.external_identities AS e
                       JOIN pending.local_work_items AS l ON l.local_id = e.local_id
                       WHERE e.provider_kind = @provider AND e.resource_scope = @scope
                         AND e.native_id = CAST(w.id AS TEXT)
                   ))
              + (SELECT COUNT(*) FROM pending.external_identities AS e
                 LEFT JOIN pending.local_work_items AS l ON l.local_id = e.local_id
                 WHERE e.provider_kind = @provider AND e.resource_scope = @scope
                   AND l.local_id IS NULL);
            """;
        command.Parameters.AddWithValue("@provider", identity.ProviderKind);
        command.Parameters.AddWithValue("@scope", identity.ResourceScope);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long CountDirtyPendingMismatches(SqliteConnection connection, SqliteTransaction transaction)
        => Count(connection, transaction, """
            SELECT
                (SELECT COUNT(*) FROM main.work_items AS w
                 WHERE w.is_seed = 0 AND w.id > 0
                   AND w.is_dirty != EXISTS (
                       SELECT 1 FROM pending.pending_changes AS p WHERE p.work_item_id = w.id
                   ))
              + (SELECT COUNT(*) FROM pending.pending_changes AS p
                 LEFT JOIN main.work_items AS w ON w.id = p.work_item_id
                 WHERE w.id IS NULL);
            """);

    private static StoreParityAnomaly[] ReadOrphanPendingAnomalies(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        var categories = new (string Code, string Predicate)[]
        {
            ("orphan-pending-field-changes", "p.change_type IN ('field', 'set_field')"),
            ("orphan-pending-state-changes", "p.change_type = 'state'"),
            ("orphan-pending-note-changes", "p.change_type IN ('note', 'add_note')"),
            ("orphan-pending-unknown-changes",
                "p.change_type NOT IN ('field', 'set_field', 'state', 'note', 'add_note')"),
        };
        return categories
            .Select(category => new StoreParityAnomaly(category.Code,
                Count(connection, transaction, $"""
                    SELECT COUNT(*)
                    FROM pending.pending_changes AS p
                    LEFT JOIN main.work_items AS w ON w.id = p.work_item_id
                    WHERE w.id IS NULL AND {category.Predicate};
                    """)))
            .Where(anomaly => anomaly.Count > 0)
            .ToArray();
    }

    private static long Count(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AppendValue(IncrementalHash hash, object value)
    {
        switch (value)
        {
            case DBNull:
                AppendByte(hash, 0);
                return;
            case long integer:
                AppendByte(hash, 1);
                AppendInt64(hash, integer);
                return;
            case int integer:
                AppendByte(hash, 1);
                AppendInt64(hash, integer);
                return;
            case double real:
                AppendByte(hash, 2);
                AppendInt64(hash, BitConverter.DoubleToInt64Bits(real));
                return;
            case string text:
                AppendByte(hash, 3);
                AppendBytes(hash, Encoding.UTF8.GetBytes(text));
                return;
            case byte[] bytes:
                AppendByte(hash, 4);
                AppendBytes(hash, bytes);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported SQLite value type in preservation digest: {value.GetType().FullName}.");
        }
    }

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        AppendInt64(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void AppendByte(IncrementalHash hash, byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        hash.AppendData(buffer);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
