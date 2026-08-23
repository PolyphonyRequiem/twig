using System.Globalization;
using Microsoft.Data.Sqlite;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IPendingChangeStore"/>.
/// Stores pending changes as rows in the pending_changes table with auto-increment IDs.
/// </summary>
public sealed class SqlitePendingChangeStore : IPendingChangeStore, IPendingChangeReader
{
    private readonly SqliteCacheStore _store;

    public SqlitePendingChangeStore(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task AddChangeAsync(int workItemId, string changeType, string? fieldName, string? oldValue, string? newValue, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pending_changes (work_item_id, change_type, field_name, old_value, new_value, created_at)
            VALUES (@workItemId, @changeType, @fieldName, @oldValue, @newValue, @createdAt);
            """;
        cmd.Parameters.AddWithValue("@workItemId", workItemId);
        cmd.Parameters.AddWithValue("@changeType", changeType);
        cmd.Parameters.AddWithValue("@fieldName", (object?)fieldName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@oldValue", (object?)oldValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@newValue", (object?)newValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task AddChangesBatchAsync(int workItemId, IReadOnlyList<(string ChangeType, string? FieldName, string? OldValue, string? NewValue)> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0) return Task.CompletedTask;

        var conn = _store.GetConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            foreach (var (changeType, fieldName, oldValue, newValue) in changes)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO pending_changes (work_item_id, change_type, field_name, old_value, new_value, created_at)
                    VALUES (@workItemId, @changeType, @fieldName, @oldValue, @newValue, @createdAt);
                    """;
                cmd.Parameters.AddWithValue("@workItemId", workItemId);
                cmd.Parameters.AddWithValue("@changeType", changeType);
                cmd.Parameters.AddWithValue("@fieldName", (object?)fieldName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@oldValue", (object?)oldValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@newValue", (object?)newValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@createdAt", now);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PendingChangeRecord>> GetChangesAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM pending_changes WHERE work_item_id = @workItemId ORDER BY id;";
        cmd.Parameters.AddWithValue("@workItemId", workItemId);

        var changes = new List<PendingChangeRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            changes.Add(MapRow(reader));
        }
        return Task.FromResult<IReadOnlyList<PendingChangeRecord>>(changes);
    }

    public Task ClearChangesAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_changes WHERE work_item_id = @workItemId;";
        cmd.Parameters.AddWithValue("@workItemId", workItemId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task ClearChangesByTypeAsync(int workItemId, string changeType, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_changes WHERE work_item_id = @workItemId AND change_type = @changeType;";
        cmd.Parameters.AddWithValue("@workItemId", workItemId);
        cmd.Parameters.AddWithValue("@changeType", changeType);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<int>> GetDirtyItemIdsAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT work_item_id FROM pending_changes ORDER BY work_item_id;";

        var ids = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }
        return Task.FromResult<IReadOnlyList<int>>(ids);
    }

    public Task RemapWorkItemIdAsync(int oldId, int newId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        // Enrolled in the ambient transaction: publish remaps inside SqliteUnitOfWork's
        // transaction, and an unenrolled command would sit outside the rollback (#270).
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = "UPDATE pending_changes SET work_item_id = @newId WHERE work_item_id = @oldId;";
        cmd.Parameters.AddWithValue("@newId", newId);
        cmd.Parameters.AddWithValue("@oldId", oldId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<int> ClearAllChangesAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM pending_changes
            WHERE work_item_id NOT IN (SELECT id FROM work_items WHERE is_seed = 1);
            """;
        var count = cmd.ExecuteNonQuery();
        return Task.FromResult(count);
    }

    public Task<(int Notes, int FieldEdits)> GetChangeSummaryAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN change_type IN ('note', 'add_note') THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN change_type IN ('field', 'state', 'set_field') THEN 1 ELSE 0 END), 0)
            FROM pending_changes
            WHERE work_item_id = @workItemId;
            """;
        cmd.Parameters.AddWithValue("@workItemId", workItemId);

        using var reader = cmd.ExecuteReader();
        reader.Read();
        return Task.FromResult((reader.GetInt32(0), reader.GetInt32(1)));
    }

    public Task<int> GetTotalPendingChangeCountAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pending_changes;";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        return Task.FromResult(count);
    }

    public Task<IReadOnlyList<PendingChangeDetail>> GetAllChangesAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        // A single scan of pending_changes, left-joined against the durable seed tables so a
        // caller sees the identity behind every row in one round-trip. The joins are gated on
        // the sign of work_item_id: negative alias -> staged_identities, positive ADO id ->
        // publish_id_map. The row order is pending_changes.id — global, not per work item —
        // so repeated edits stay in the sequence they were staged in.
        cmd.CommandText = """
            SELECT
                pc.id,
                pc.work_item_id,
                pc.change_type,
                pc.field_name,
                pc.old_value,
                pc.new_value,
                pc.created_at,
                si.staged_identity AS neg_identity,
                si.alias           AS neg_alias,
                pim.staged_identity AS pos_identity,
                pim.old_id          AS pos_alias,
                pim.new_id          AS pos_new_id
            FROM pending_changes pc
            LEFT JOIN staged_identities si
                ON pc.work_item_id < 0 AND si.alias = pc.work_item_id
            LEFT JOIN publish_id_map pim
                ON pc.work_item_id > 0 AND pim.new_id = pc.work_item_id
            ORDER BY pc.id;
            """;

        var details = new List<PendingChangeDetail>();
        long previousId = -1;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            var id = reader.GetInt64(0);
            var workItemId = reader.GetInt32(1);

            // ORDER BY pc.id makes any duplicate pc.id appear consecutively — the only source
            // is a many-to-one join hit against staged_identities.alias or publish_id_map.new_id.
            // The read refuses to guess between them; the caller has to reconcile the durable
            // tables before this projection can succeed.
            if (previousId == id)
            {
                throw new InvalidOperationException(
                    $"Ambiguous seed identity mapping for pending change {id}: work item {workItemId} matches more than one staged_identities or publish_id_map row.");
            }
            previousId = id;

            var kind = reader.GetString(2);
            var field = reader.IsDBNull(3) ? null : reader.GetString(3);
            var oldValue = reader.IsDBNull(4) ? null : reader.GetString(4);
            var newValue = reader.IsDBNull(5) ? null : reader.GetString(5);
            var stagedAt = DateTimeOffset.Parse(
                reader.GetString(6),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

            // Convenience mirror: notes surface newValue as Note. Every other kind — including
            // ones this build doesn't recognise — leaves it null and keeps the raw kind.
            string? note = kind is "note" or "add_note" ? newValue : null;

            SeedRemapIdentity? seedRemap = null;
            if (!reader.IsDBNull(7) && !reader.IsDBNull(8))
            {
                var identityText = reader.GetString(7);
                var aliasValue = reader.GetInt32(8);
                if (StagedIdentity.TryParse(identityText, out var identity)
                    && StagedAlias.TryFrom(aliasValue, out var alias))
                {
                    seedRemap = new SeedRemapIdentity(identity, alias, PublishedWorkItemId: null);
                }
            }
            else if (!reader.IsDBNull(9) && !reader.IsDBNull(10) && !reader.IsDBNull(11))
            {
                var identityText = reader.GetString(9);
                var aliasValue = reader.GetInt32(10);
                var publishedId = reader.GetInt32(11);
                if (StagedIdentity.TryParse(identityText, out var identity)
                    && StagedAlias.TryFrom(aliasValue, out var alias))
                {
                    seedRemap = new SeedRemapIdentity(identity, alias, publishedId);
                }
            }

            details.Add(new PendingChangeDetail(
                PendingChangeId: id,
                WorkItemId: workItemId,
                Kind: kind,
                Field: field,
                Note: note,
                OldValue: oldValue,
                NewValue: newValue,
                StagedAt: stagedAt,
                SeedRemap: seedRemap));
        }

        return Task.FromResult<IReadOnlyList<PendingChangeDetail>>(details);
    }

    private static PendingChangeRecord MapRow(SqliteDataReader reader)
    {
        return new PendingChangeRecord(
            WorkItemId: reader.GetInt32(reader.GetOrdinal("work_item_id")),
            ChangeType: reader.GetString(reader.GetOrdinal("change_type")),
            FieldName: reader.IsDBNull(reader.GetOrdinal("field_name"))
                ? null
                : reader.GetString(reader.GetOrdinal("field_name")),
            OldValue: reader.IsDBNull(reader.GetOrdinal("old_value"))
                ? null
                : reader.GetString(reader.GetOrdinal("old_value")),
            NewValue: reader.IsDBNull(reader.GetOrdinal("new_value"))
                ? null
                : reader.GetString(reader.GetOrdinal("new_value")));
    }
}
