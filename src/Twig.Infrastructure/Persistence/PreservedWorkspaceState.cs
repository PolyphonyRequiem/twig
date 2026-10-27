using Microsoft.Data.Sqlite;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;

namespace Twig.Infrastructure.Persistence;

internal sealed class PreservedWorkspaceState(SqliteCacheStore store)
{
    internal const string ItemColumns =
        "id, type, title, state, parent_id, assigned_to, assigned_to_unique_name, iteration_path, area_path, " +
        "revision, is_seed, seed_created_at, staged_identity, fields_json, is_dirty, last_synced_at";

    internal bool IsActive
    {
        get
        {
            if (store.IsMigrationInspection) return false;
            using var command = Command();
            command.CommandText = "SELECT source_generation FROM pending.preservation_migrations WHERE slot = 1 AND state = 'active';";
            if (command.ExecuteScalar() is not string generation)
                return false;
            if (!string.Equals(generation, store.AdmittedGeneration, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "preservation-generation-mismatch: retained local state belongs to another admitted generation; preserve both stores and resume the original migration.");
            using var identities = Command();
            identities.CommandText = """
                SELECT 1 FROM pending.seed_work_items s
                LEFT JOIN pending.local_work_items l ON l.local_id=s.local_id
                LEFT JOIN pending.seed_local_identities m ON m.staged_identity=s.staged_identity
                WHERE l.local_id IS NULL OR l.deleted_at IS NOT NULL OR s.local_id=s.staged_identity
                    OR m.local_id IS NULL OR m.local_id<>s.local_id LIMIT 1;
                """;
            if (identities.ExecuteScalar() is not null)
                throw new InvalidOperationException("preservation-seed-mapping-invalid: retained seed identity is missing, tombstoned or conflated with its staged identity.");
            return true;
        }
    }

    internal string Items => IsActive
        ? $"(SELECT {ItemColumns} FROM main.work_items WHERE is_seed = 0 AND id > 0 UNION ALL SELECT {ItemColumns} FROM pending.seed_work_items)"
        : "main.work_items";

    internal string Context => IsActive ? "pending.workspace_context" : "main.context";
    internal string Navigation => IsActive ? "pending.workspace_navigation_history" : "main.navigation_history";

    internal WriteScope BeginWrite() => new(store, this, IsActive);

    internal async Task SaveSeedAsync(int alias, CancellationToken ct)
    {
        using var read = Command();
        read.CommandText = """
            SELECT w.staged_identity FROM main.work_items w
            JOIN pending.staged_identities s ON s.staged_identity=w.staged_identity AND s.alias=w.id
            WHERE w.id=$alias AND w.is_seed=1 AND w.id<0 AND s.retired_at IS NULL;
            """;
        read.Parameters.AddWithValue("$alias", alias);
        if (read.ExecuteScalar() is not string text || !StagedIdentity.TryParse(text, out var staged))
            throw new InvalidOperationException("preservation-seed-identity-invalid: a retained seed needs its original staged identity and negative alias.");
        var publication = ReadPublicationEvidence(staged, store.ActiveTransaction);
        if (publication?.MappedId is not null || publication?.CompletedId is not null)
            throw new InvalidOperationException("preservation-seed-already-published: a stale negative save cannot recreate a retained seed with known positive publication evidence.");

        using var find = Command();
        find.CommandText = """
            SELECT m.local_id,s.alias FROM pending.seed_local_identities m
            JOIN pending.staged_identities s ON s.staged_identity=m.staged_identity
            WHERE m.staged_identity=$staged;
            """;
        find.Parameters.AddWithValue("$staged", staged.ToString());
        LocalWorkItemId local;
        using (var reader = find.ExecuteReader())
        {
            if (reader.Read())
            {
                if (!LocalWorkItemId.TryParse(reader.GetString(0), out local) || reader.GetInt32(1) != alias)
                    throw new InvalidOperationException("preservation-seed-mapping-conflict: the staged identity already owns a different local identity or alias.");
            }
            else
            {
                local = default;
            }
        }
        if (local.IsEmpty)
        {
            local = (await new SqliteLocalWorkItemRepository(store).CreateAsync(FieldValues.Empty, ct).ConfigureAwait(false)).Id;
            using var mapping = Command();
            mapping.CommandText = "INSERT INTO pending.seed_local_identities(staged_identity,local_id) VALUES($staged,$local);";
            mapping.Parameters.AddWithValue("$staged", staged.ToString());
            mapping.Parameters.AddWithValue("$local", local.ToString());
            mapping.ExecuteNonQuery();
        }

        using var save = Command();
        save.CommandText = $"""
            INSERT INTO pending.seed_work_items (local_id, {ItemColumns})
            SELECT $local, {ItemColumns} FROM main.work_items WHERE id = $alias
            ON CONFLICT(staged_identity) DO UPDATE SET
                type=excluded.type, title=excluded.title, state=excluded.state, parent_id=excluded.parent_id,
                assigned_to=excluded.assigned_to, assigned_to_unique_name=excluded.assigned_to_unique_name,
                iteration_path=excluded.iteration_path, area_path=excluded.area_path, revision=excluded.revision,
                seed_created_at=excluded.seed_created_at, fields_json=excluded.fields_json,
                is_dirty=excluded.is_dirty, last_synced_at=excluded.last_synced_at;
            """;
        save.Parameters.AddWithValue("$local", local.ToString());
        save.Parameters.AddWithValue("$alias", alias);
        if (save.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("preservation-seed-write-failed: the seed payload was not retained.");
    }

    internal void DeleteSeed(int alias)
    {
        using var command = Command();
        command.CommandText = "DELETE FROM pending.seed_work_items WHERE id = $alias;";
        command.Parameters.AddWithValue("$alias", alias);
        command.ExecuteNonQuery();
    }

    internal void RemapPublishedReferences(int alias)
    {
        using var mapping = Command();
        mapping.CommandText = """
            SELECT p.new_id FROM pending.publish_id_map p
            JOIN pending.seed_local_identities s ON s.staged_identity=p.staged_identity
            WHERE p.old_id=$old;
            """;
        mapping.Parameters.AddWithValue("$old", alias);
        if (mapping.ExecuteScalar() is not long published) return;
        using var command = Command();
        command.CommandText = """
            UPDATE main.context SET value=$text WHERE key='active_work_item_id' AND value=$oldText;
            UPDATE pending.workspace_context SET value=$text WHERE key='active_work_item_id' AND value=$oldText;
            UPDATE main.navigation_history SET work_item_id=$new WHERE work_item_id=$old;
            UPDATE pending.workspace_navigation_history SET work_item_id=$new WHERE work_item_id=$old;
            """;
        command.Parameters.AddWithValue("$new", published);
        command.Parameters.AddWithValue("$old", alias);
        command.Parameters.AddWithValue("$text", published.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$oldText", alias.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    internal bool HasRetainedIdentity(StagedIdentity identity, SqliteTransaction? transaction = null)
    {
        using var command = store.GetConnection().CreateCommand();
        command.Transaction = transaction ?? store.ActiveTransaction;
        command.CommandText = "SELECT 1 FROM pending.seed_local_identities WHERE staged_identity=$identity;";
        command.Parameters.AddWithValue("$identity", identity.ToString());
        return command.ExecuteScalar() is not null;
    }

    internal (int Alias, int? MappedId, int? CompletedId)? ReadPublicationEvidence(
        StagedIdentity identity, SqliteTransaction? transaction = null)
    {
        using var command = store.GetConnection().CreateCommand();
        command.Transaction = transaction ?? store.ActiveTransaction;
        command.CommandText = """
            SELECT m.local_id,s.alias,l.local_id,l.deleted_at,
                p.old_id,p.staged_identity,p.new_id,i.published_id
            FROM pending.seed_local_identities m
            LEFT JOIN pending.staged_identities s ON s.staged_identity=m.staged_identity
            LEFT JOIN pending.local_work_items l ON l.local_id=m.local_id
            LEFT JOIN pending.publish_id_map p ON p.staged_identity=m.staged_identity OR p.old_id=s.alias
            LEFT JOIN pending.publish_intents i ON i.staged_identity=m.staged_identity
            WHERE m.staged_identity=$identity;
            """;
        command.Parameters.AddWithValue("$identity", identity.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!LocalWorkItemId.TryParse(reader.GetString(0), out var local)
            || local.Value == identity.Value || reader.IsDBNull(1) || reader.GetInt32(1) >= 0
            || reader.IsDBNull(2) || !reader.IsDBNull(3))
            throw new InvalidOperationException("preservation-publication-evidence-invalid: retained identity or alias is absent, tombstoned or conflated.");
        var alias = reader.GetInt32(1);
        int? mapped = reader.IsDBNull(6) ? null : reader.GetInt32(6);
        int? completed = reader.IsDBNull(7) ? null : reader.GetInt32(7);
        if (mapped is not null
            && (mapped is <= 0 || reader.IsDBNull(4) || reader.GetInt32(4) != alias
                || reader.IsDBNull(5) || reader.GetString(5) != identity.ToString()))
            throw new InvalidOperationException("preservation-publication-evidence-invalid: the retained publication map has unknown ownership.");
        if (completed is <= 0 || (mapped is not null && completed is not null && mapped != completed))
            throw new InvalidOperationException("preservation-publication-conflict: prior positive publication evidence disagrees; nothing was rewritten.");
        if (reader.Read())
            throw new InvalidOperationException("preservation-publication-evidence-invalid: multiple publication mappings name the retained identity or alias.");
        return (alias, mapped, completed);
    }

    internal (int Alias, int? MappedId, int? CompletedId)? RequireCompatiblePublication(
        StagedIdentity identity, int publishedId, SqliteTransaction? transaction = null)
    {
        var evidence = ReadPublicationEvidence(identity, transaction);
        if (evidence is null) return null;
        if (publishedId <= 0
            || (evidence.Value.MappedId is { } mapped && mapped != publishedId)
            || (evidence.Value.CompletedId is { } completed && completed != publishedId))
            throw new InvalidOperationException("preservation-publication-conflict: contradictory completion cannot replace the retained native ID or original evidence.");
        return evidence;
    }

    internal void SetContext(string key, string? value)
    {
        using var command = Command();
        command.CommandText = value is null
            ? "DELETE FROM pending.workspace_context WHERE key = $key;"
            : "INSERT INTO pending.workspace_context(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        if (value is not null) command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    internal void MirrorNavigation()
    {
        using var command = Command();
        command.CommandText = """
            DELETE FROM main.navigation_history;
            INSERT INTO main.navigation_history(id,work_item_id,visited_at)
                SELECT id,work_item_id,visited_at FROM pending.workspace_navigation_history ORDER BY id;
            DELETE FROM main.sqlite_sequence WHERE name='navigation_history';
            INSERT INTO main.sqlite_sequence(name,seq)
                SELECT 'navigation_history',seq FROM pending.sqlite_sequence WHERE name='workspace_navigation_history';
            DELETE FROM main.context WHERE key='nav_history_cursor';
            INSERT INTO main.context(key,value)
                SELECT key,value FROM pending.workspace_context WHERE key='nav_history_cursor';
            """;
        command.ExecuteNonQuery();
    }

    internal void VerifyParity()
    {
        foreach (var (source, target, columns) in new[]
        {
            ("main.context", "pending.workspace_context", "key,value"),
            ("main.navigation_history", "pending.workspace_navigation_history", "id,work_item_id,visited_at"),
            ("(SELECT * FROM main.work_items WHERE is_seed=1)", "pending.seed_work_items", ItemColumns),
            ("(SELECT seq FROM main.sqlite_sequence WHERE name='navigation_history')",
             "(SELECT seq FROM pending.sqlite_sequence WHERE name='workspace_navigation_history')", "seq"),
        })
        {
            using var command = Command();
            command.CommandText = $"""
                SELECT EXISTS(SELECT {columns} FROM {source} EXCEPT SELECT {columns} FROM {target})
                    OR EXISTS(SELECT {columns} FROM {target} EXCEPT SELECT {columns} FROM {source});
                """;
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("preservation-parity-failed: retained local payload differs from its source; no activation or rollback is admitted.");
        }
    }

    private void MirrorSeedMutations()
    {
        var columns = ItemColumns.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        var mutable = string.Join(",", columns.Where(column => column is not ("id" or "staged_identity")));
        using var command = Command();
        command.CommandText = $"""
            DELETE FROM pending.seed_work_items
                WHERE NOT EXISTS(SELECT 1 FROM main.work_items w WHERE w.id=seed_work_items.id AND w.is_seed=1);
            UPDATE pending.seed_work_items
                SET ({mutable})=(SELECT {mutable} FROM main.work_items w WHERE w.id=seed_work_items.id);
            """;
        command.ExecuteNonQuery();
    }

    internal sealed class WriteScope : IDisposable
    {
        private readonly SqliteCacheStore _store;
        private readonly PreservedWorkspaceState _state;
        private readonly bool _active;
        private readonly SqliteTransaction? _owned;
        private readonly SqliteTransaction? _prior;

        internal WriteScope(SqliteCacheStore store, PreservedWorkspaceState state, bool active)
        {
            _store = store;
            _state = state;
            _active = active;
            _prior = store.ActiveTransaction;
            if (active && _prior is null)
            {
                _owned = store.GetConnection().BeginTransaction();
                store.ActiveTransaction = _owned;
            }
        }

        internal void Complete(bool seeds = false, bool navigation = false)
        {
            if (_active)
            {
                if (seeds) _state.MirrorSeedMutations();
                if (navigation) _state.MirrorNavigation();
            }
            _owned?.Commit();
        }

        public void Dispose()
        {
            try { _owned?.Dispose(); }
            finally { if (_owned is not null) _store.ActiveTransaction = _prior; }
        }
    }

    private SqliteCommand Command()
    {
        var command = store.GetConnection().CreateCommand();
        command.Transaction = store.ActiveTransaction;
        return command;
    }
}
