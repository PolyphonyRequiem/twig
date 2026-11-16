using Microsoft.Data.Sqlite;

namespace Twig.Infrastructure.Persistence;

internal static class ConnectionTransitionAdmission
{
    internal static void ValidateNoUnfinishedTransition(string database, string fingerprint)
    {
        if (!File.Exists(database))
            throw new InvalidOperationException("activation-required: the current native registry is missing.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM layout_meta WHERE id=1;";
        if (Convert.ToInt32(command.ExecuteScalar()) != SqliteSystemWorktreeRegistry.SchemaVersion)
            throw new InvalidOperationException("activation-schema-unsupported: only the current native registry is admitted.");
        command.CommandText = "SELECT 1 FROM connection_default_transition_members m JOIN connection_default_transitions f ON f.digest=m.digest WHERE m.worktree_fingerprint=$fp AND f.state<>'completed' LIMIT 1;";
        command.Parameters.AddWithValue("$fp", fingerprint);
        if (command.ExecuteScalar() is not null)
            throw new InvalidOperationException("binding-changed: binding-default-transition-incomplete. This checkout is fenced until every affected worktree completes the original native default intent. Resume connection default with its original binding and confirmed digest, then explicitly reconnect; no partial cache/read/work HTTP is admitted.");
        command.CommandText = "SELECT 1 FROM connection_binding_transitions WHERE worktree_fingerprint=$fp AND state <> 'completed' LIMIT 1;";
        if (command.ExecuteScalar() is not null)
            throw new InvalidOperationException("binding-transition-incomplete: this checkout is fenced by its unfinished native binding intent. Resume the original connection pin/unpin with its confirmed digest, then explicitly reconnect; no read, cache fill or work HTTP is admitted.");
    }
}
