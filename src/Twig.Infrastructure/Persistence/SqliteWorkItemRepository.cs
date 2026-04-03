using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IWorkItemRepository"/>.
/// All queries use parameterized SQL — no string interpolation.
/// </summary>
public sealed class SqliteWorkItemRepository : IWorkItemRepository
{
    private readonly SqliteCacheStore _store;

    public SqliteWorkItemRepository(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task<WorkItem?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_items WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        var item = reader.Read() ? MapRow(reader) : null;
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<WorkItem>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var idList = ids as IList<int> ?? ids.ToList();
        if (idList.Count == 0)
            return Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>());

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();

        var paramNames = new string[idList.Count];
        for (var i = 0; i < idList.Count; i++)
        {
            paramNames[i] = $"@p{i}";
            cmd.Parameters.AddWithValue(paramNames[i], idList[i]);
        }

        cmd.CommandText = $"SELECT * FROM work_items WHERE id IN ({string.Join(',', paramNames)});";

        var items = ReadAll(cmd);
        return Task.FromResult<IReadOnlyList<WorkItem>>(items);
    }

    public Task<IReadOnlyList<WorkItem>> GetChildrenAsync(int parentId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_items WHERE parent_id = @parentId ORDER BY CASE WHEN id < 0 THEN 1 ELSE 0 END, CASE WHEN id >= 0 THEN type END, CASE WHEN id >= 0 THEN title END, ABS(id);";
        cmd.Parameters.AddWithValue("@parentId", parentId);

        var items = ReadAll(cmd);
        return Task.FromResult<IReadOnlyList<WorkItem>>(items);
    }

    public Task<IReadOnlyList<WorkItem>> GetRootItemsAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_items WHERE parent_id IS NULL AND id > 0 ORDER BY type, title;";

        var items = ReadAll(cmd);
        return Task.FromResult<IReadOnlyList<WorkItem>>(items);
    }

    public Task<IReadOnlyList<WorkItem>> GetByIterationAsync(IterationPath iterationPath, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_items WHERE iteration_path = @path;";
        cmd.Parameters.AddWithValue("@path", iterationPath.Value);

        var items = ReadAll(cmd);
        return Task.FromResult<IReadOnlyList<WorkItem>>(items);
    }

    public Task<IReadOnlyList<WorkItem>> GetByIterationAndAssigneeAsync(IterationPath iterationPath, string assignee, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_items WHERE iteration_path = @path AND assigned_to = @assignee COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("@path", iterationPath.Value);
        cmd.Parameters.AddWithValue("@assignee", assignee);

        var items = ReadAll(cmd);
        return Task.FromResult<IReadOnlyList<WorkItem>>(items);
    }

    public Task<IReadOnlyList<WorkItem>> GetParentChainAsync(int id, CancellationToken ct = default)
    {
        var chain = new List<WorkItem>();
        int? currentId = id;

        while (currentId.HasValue)
        {
            var conn = _store.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM work_items WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", currentId.Value);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                break;

            var item = MapRow(reader);
            chain.Add(item);
            currentId = item.ParentId;
        }

        // Reverse so the chain goes root → ... → parent (not child → ... → root)
        chain.Reverse();
        return Task.FromResult<IReadOnlyList<WorkItem>>(chain);
    }

    public Task<IReadOnlyList<WorkItem>> FindByPatternAsync(string pattern, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_items WHERE title LIKE '%' || @pattern || '%' COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("@pattern", pattern);

        var items = ReadAll(cmd);
        return Task.FromResult<IReadOnlyList<WorkItem>>(items);
    }

    public Task<IReadOnlyList<WorkItem>> GetDirtyItemsAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_items WHERE is_dirty = 1;";

        var items = ReadAll(cmd);
        return Task.FromResult<IReadOnlyList<WorkItem>>(items);
    }

    public Task<IReadOnlyList<WorkItem>> GetSeedsAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM work_items WHERE is_seed = 1;";

        var items = ReadAll(cmd);
        return Task.FromResult<IReadOnlyList<WorkItem>>(items);
    }

    public Task<bool> ExistsByIdAsync(int id, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM work_items WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        return Task.FromResult(count > 0);
    }

    public Task<IReadOnlyList<int>> GetOrphanParentIdsAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT w.parent_id
            FROM work_items w
            WHERE w.parent_id IS NOT NULL
              AND w.parent_id > 0
              AND NOT EXISTS (SELECT 1 FROM work_items p WHERE p.id = w.parent_id);
            """;

        var ids = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }
        return Task.FromResult<IReadOnlyList<int>>(ids);
    }

    public Task SaveAsync(WorkItem workItem, CancellationToken ct = default)
    {
        SaveWorkItem(_store.GetConnection(), workItem, _store.ActiveTransaction);
        return Task.CompletedTask;
    }

    public Task SaveBatchAsync(IEnumerable<WorkItem> workItems, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var item in workItems)
            {
                SaveWorkItem(conn, item, tx);
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

    public Task DeleteByIdAsync(int id, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = "DELETE FROM work_items WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task RemapParentIdAsync(int oldParentId, int newParentId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = "UPDATE work_items SET parent_id = @newParentId WHERE parent_id = @oldParentId;";
        cmd.Parameters.AddWithValue("@newParentId", newParentId);
        cmd.Parameters.AddWithValue("@oldParentId", oldParentId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<int?> GetMinSeedIdAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(id) FROM work_items WHERE is_seed = 1;";
        var result = cmd.ExecuteScalar();
        var minId = result is DBNull || result is null ? (int?)null : Convert.ToInt32(result);
        return Task.FromResult(minId);
    }

    public Task EvictExceptAsync(IReadOnlySet<int> keepIds, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();

        if (keepIds.Count == 0)
        {
            // Empty keep set — delete everything
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM work_items;";
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        if (keepIds.Count > 900)
        {
            // Large keep set — use a temp table to avoid SQLite parameter limit (999)
            using var tx = conn.BeginTransaction();
            try
            {
                using (var createCmd = conn.CreateCommand())
                {
                    createCmd.Transaction = tx;
                    createCmd.CommandText = "CREATE TEMP TABLE IF NOT EXISTS _keep_ids (id INTEGER PRIMARY KEY);";
                    createCmd.ExecuteNonQuery();
                }

                using (var clearCmd = conn.CreateCommand())
                {
                    clearCmd.Transaction = tx;
                    clearCmd.CommandText = "DELETE FROM _keep_ids;";
                    clearCmd.ExecuteNonQuery();
                }

                foreach (var id in keepIds)
                {
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = "INSERT INTO _keep_ids (id) VALUES (@id);";
                    insertCmd.Parameters.AddWithValue("@id", id);
                    insertCmd.ExecuteNonQuery();
                }

                using (var deleteCmd = conn.CreateCommand())
                {
                    deleteCmd.Transaction = tx;
                    deleteCmd.CommandText = "DELETE FROM work_items WHERE id NOT IN (SELECT id FROM _keep_ids);";
                    deleteCmd.ExecuteNonQuery();
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

        // Standard path — parameterized IN clause
        using var delCmd = conn.CreateCommand();
        var paramNames = new string[keepIds.Count];
        var i = 0;
        foreach (var id in keepIds)
        {
            var paramName = $"@id{i}";
            paramNames[i] = paramName;
            delCmd.Parameters.AddWithValue(paramName, id);
            i++;
        }

        delCmd.CommandText = $"DELETE FROM work_items WHERE id NOT IN ({string.Join(", ", paramNames)});";
        delCmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<int> ClearPhantomDirtyFlagsAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            UPDATE work_items SET is_dirty = 0
            WHERE is_dirty = 1
              AND is_seed = 0
              AND id NOT IN (SELECT work_item_id FROM pending_changes);
            """;
        var count = cmd.ExecuteNonQuery();
        return Task.FromResult(count);
    }

    public Task ClearDirtyFlagAsync(int id, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = "UPDATE work_items SET is_dirty = 0 WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private static void SaveWorkItem(SqliteConnection conn, WorkItem item, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO work_items
                (id, type, title, state, parent_id, assigned_to, iteration_path, area_path,
                 revision, is_seed, seed_created_at, fields_json, is_dirty, last_synced_at)
            VALUES
                (@id, @type, @title, @state, @parentId, @assignedTo, @iterationPath, @areaPath,
                 @revision, @isSeed, @seedCreatedAt, @fieldsJson, @isDirty, @lastSyncedAt);
            """;

        cmd.Parameters.AddWithValue("@id", item.Id);
        cmd.Parameters.AddWithValue("@type", item.Type.ToString());
        cmd.Parameters.AddWithValue("@title", item.Title);
        cmd.Parameters.AddWithValue("@state", item.State);
        cmd.Parameters.AddWithValue("@parentId", (object?)item.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@assignedTo", (object?)item.AssignedTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@iterationPath", (object?)item.IterationPath.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@areaPath", (object?)item.AreaPath.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@revision", item.Revision);
        cmd.Parameters.AddWithValue("@isSeed", item.IsSeed ? 1 : 0);
        cmd.Parameters.AddWithValue("@seedCreatedAt",
            item.SeedCreatedAt.HasValue ? item.SeedCreatedAt.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("@fieldsJson", SerializeFields(item.Fields));
        cmd.Parameters.AddWithValue("@isDirty", item.IsDirty ? 1 : 0);
        cmd.Parameters.AddWithValue("@lastSyncedAt", DateTimeOffset.UtcNow.ToString("o"));

        cmd.ExecuteNonQuery();
    }

    private static string SerializeFields(IReadOnlyDictionary<string, string?> fields)
    {
        var dict = new Dictionary<string, string?>(fields, StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(dict, TwigJsonContext.Default.DictionaryStringString);
    }

    private static Dictionary<string, string?>? DeserializeFields(string json)
    {
        return JsonSerializer.Deserialize(json, TwigJsonContext.Default.DictionaryStringString);
    }

    private static List<WorkItem> ReadAll(SqliteCommand cmd)
    {
        var items = new List<WorkItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(MapRow(reader));
        }
        return items;
    }

    private static WorkItem MapRow(SqliteDataReader reader)
    {
        var typeResult = WorkItemType.Parse(reader.GetString(reader.GetOrdinal("type")));
        var iterResult = IterationPath.Parse(reader.IsDBNull(reader.GetOrdinal("iteration_path"))
            ? "Unknown"
            : reader.GetString(reader.GetOrdinal("iteration_path")));
        var areaResult = AreaPath.Parse(reader.IsDBNull(reader.GetOrdinal("area_path"))
            ? "Unknown"
            : reader.GetString(reader.GetOrdinal("area_path")));

        var seedCreatedAtStr = reader.IsDBNull(reader.GetOrdinal("seed_created_at"))
            ? null
            : reader.GetString(reader.GetOrdinal("seed_created_at"));

        var fieldsJson = reader.GetString(reader.GetOrdinal("fields_json"));
        var fields = DeserializeFields(fieldsJson);

        var lastSyncedAtStr = reader.IsDBNull(reader.GetOrdinal("last_synced_at"))
            ? null
            : reader.GetString(reader.GetOrdinal("last_synced_at"));

        var item = new WorkItem
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            Type = typeResult.IsSuccess ? typeResult.Value : WorkItemType.Task,
            Title = reader.GetString(reader.GetOrdinal("title")),
            State = reader.GetString(reader.GetOrdinal("state")),
            ParentId = reader.IsDBNull(reader.GetOrdinal("parent_id"))
                ? null
                : reader.GetInt32(reader.GetOrdinal("parent_id")),
            AssignedTo = reader.IsDBNull(reader.GetOrdinal("assigned_to"))
                ? null
                : reader.GetString(reader.GetOrdinal("assigned_to")),
            IterationPath = iterResult.IsSuccess ? iterResult.Value : default,
            AreaPath = areaResult.IsSuccess ? areaResult.Value : default,
            IsSeed = reader.GetInt32(reader.GetOrdinal("is_seed")) == 1,
            SeedCreatedAt = seedCreatedAtStr is not null
                ? DateTimeOffset.Parse(seedCreatedAtStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
                : null,
            LastSyncedAt = lastSyncedAtStr is not null
                ? DateTimeOffset.Parse(lastSyncedAtStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
                : null,
        };

        // Restore revision and dirty flag via domain methods
        var revision = reader.GetInt32(reader.GetOrdinal("revision"));
        var isDirty = reader.GetInt32(reader.GetOrdinal("is_dirty")) == 1;

        // Use MarkSynced to set revision (it also clears dirty, so we set dirty after if needed)
        if (revision > 0)
        {
            item.MarkSynced(revision);
        }

        // Restore fields from JSON
        if (fields is not null)
        {
            foreach (var kvp in fields)
            {
                item.SetField(kvp.Key, kvp.Value);
            }
        }

        // Restore dirty flag directly — no side effects on fields or command queue
        if (isDirty)
        {
            item.SetDirty();
        }

        return item;
    }
}
