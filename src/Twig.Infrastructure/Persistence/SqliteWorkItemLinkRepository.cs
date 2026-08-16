using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IWorkItemLinkRepository"/>.
/// All queries use parameterized SQL — no string interpolation.
/// </summary>
public sealed class SqliteWorkItemLinkRepository : IWorkItemLinkRepository
{
    private readonly SqliteCacheStore _store;

    public SqliteWorkItemLinkRepository(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<WorkItemLink>> GetLinksAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_id, target_id, link_type FROM work_item_links WHERE source_id = @sourceId;";
        cmd.Parameters.AddWithValue("@sourceId", workItemId);

        var links = new List<WorkItemLink>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            links.Add(new WorkItemLink(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2)));
        }

        return Task.FromResult<IReadOnlyList<WorkItemLink>>(links);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkItemLink>> GetLinksForSetAsync(IReadOnlyList<int> workItemIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workItemIds);

        // Distinct first: a caller passing the same id twice must not receive its edges twice,
        // and it keeps the parameter count at the number of DISTINCT ids rather than the input length.
        var distinctIds = new List<int>();
        var seen = new HashSet<int>();
        foreach (var id in workItemIds)
        {
            if (seen.Add(id))
                distinctIds.Add(id);
        }

        if (distinctIds.Count == 0)
            return Task.FromResult<IReadOnlyList<WorkItemLink>>(Array.Empty<WorkItemLink>());

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();

        // Parameterized IN list — the id values are bound, never interpolated. Only the
        // placeholder names are built into the SQL text, and those are generated here.
        var placeholders = new string[distinctIds.Count];
        for (var i = 0; i < distinctIds.Count; i++)
        {
            placeholders[i] = $"@id{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            cmd.Parameters.AddWithValue(placeholders[i], distinctIds[i]);
        }

        cmd.CommandText =
            $"SELECT source_id, target_id, link_type FROM work_item_links WHERE source_id IN ({string.Join(", ", placeholders)});";

        var links = new List<WorkItemLink>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            links.Add(new WorkItemLink(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2)));
        }

        return Task.FromResult<IReadOnlyList<WorkItemLink>>(links);
    }

    public Task SaveLinksAsync(int workItemId, IReadOnlyList<WorkItemLink> links, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            using (var deleteCmd = conn.CreateCommand())
            {
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = "DELETE FROM work_item_links WHERE source_id = @sourceId;";
                deleteCmd.Parameters.AddWithValue("@sourceId", workItemId);
                deleteCmd.ExecuteNonQuery();
            }

            foreach (var link in links)
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = """
                    INSERT INTO work_item_links (source_id, target_id, link_type)
                    VALUES (@sourceId, @targetId, @linkType);
                    """;
                insertCmd.Parameters.AddWithValue("@sourceId", workItemId);
                insertCmd.Parameters.AddWithValue("@targetId", link.TargetId);
                insertCmd.Parameters.AddWithValue("@linkType", link.LinkType);
                insertCmd.ExecuteNonQuery();
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
}
