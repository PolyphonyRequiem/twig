using System.Globalization;
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
        var distinctIds = Distinct(workItemIds);

        if (distinctIds.Count == 0)
            return Task.FromResult<IReadOnlyList<WorkItemLink>>(Array.Empty<WorkItemLink>());

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();

        // Parameterized IN list — the id values are bound, never interpolated. Only the
        // placeholder names are built into the SQL text, and those are generated here.
        var placeholders = BindIdList(cmd, distinctIds);

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

    /// <inheritdoc />
    public Task<DateTimeOffset?> GetLinksVerifiedAtAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT verified_at FROM work_item_link_verifications WHERE source_id = @sourceId;";
        cmd.Parameters.AddWithValue("@sourceId", workItemId);

        var raw = cmd.ExecuteScalar() as string;
        return Task.FromResult(ParseVerifiedAt(raw));
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<int, DateTimeOffset>> GetLinksVerifiedAtForSetAsync(
        IReadOnlyList<int> workItemIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workItemIds);

        var distinctIds = Distinct(workItemIds);
        if (distinctIds.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<int, DateTimeOffset>>(EmptyVerifications);

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();

        var placeholders = BindIdList(cmd, distinctIds);
        cmd.CommandText =
            $"SELECT source_id, verified_at FROM work_item_link_verifications WHERE source_id IN ({string.Join(", ", placeholders)});";

        var verified = new Dictionary<int, DateTimeOffset>(distinctIds.Count);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // An unparseable stamp is treated as no stamp: reporting a garbage instant as a
            // verification would be the same false confidence in a new costume.
            var parsed = ParseVerifiedAt(reader.IsDBNull(1) ? null : reader.GetString(1));
            if (parsed is { } instant)
                verified[reader.GetInt32(0)] = instant;
        }

        return Task.FromResult<IReadOnlyDictionary<int, DateTimeOffset>>(verified);
    }

    /// <inheritdoc />
    public Task SaveLinksAsync(int workItemId, IReadOnlyList<WorkItemLink> links, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            ReplaceSource(conn, tx, workItemId, links, Now());
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveLinksForSourcesAsync(
        IReadOnlyDictionary<int, IReadOnlyList<WorkItemLink>> linksBySource,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(linksBySource);

        if (linksBySource.Count == 0)
            return Task.CompletedTask;

        var conn = _store.GetConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // One timestamp for the whole set: these edges were read in one fetch, so stamping
            // them with drifting per-row instants would imply a precision the data does not have.
            var verifiedAt = Now();
            foreach (var (sourceId, links) in linksBySource)
                ReplaceSource(conn, tx, sourceId, links, verifiedAt);

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return Task.CompletedTask;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Replaces one source's whole edge set and stamps it verified, on the caller's transaction.
    /// </summary>
    /// <remarks>
    /// 🔴 The stamp rides the SAME transaction as the edge rows, deliberately: it is what makes
    /// "no rows" mean "no edges" rather than "never asked" (AB#831), so it must not be able to
    /// land without them, nor they without it.
    /// </remarks>
    private static void ReplaceSource(
        SqliteConnection conn,
        SqliteTransaction tx,
        int sourceId,
        IReadOnlyList<WorkItemLink> links,
        string verifiedAt)
    {
        using (var deleteCmd = conn.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM work_item_links WHERE source_id = @sourceId;";
            deleteCmd.Parameters.AddWithValue("@sourceId", sourceId);
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
            insertCmd.Parameters.AddWithValue("@sourceId", sourceId);
            insertCmd.Parameters.AddWithValue("@targetId", link.TargetId);
            insertCmd.Parameters.AddWithValue("@linkType", link.LinkType);
            insertCmd.ExecuteNonQuery();
        }

        using var stampCmd = conn.CreateCommand();
        stampCmd.Transaction = tx;
        stampCmd.CommandText = """
            INSERT INTO work_item_link_verifications (source_id, verified_at)
            VALUES (@sourceId, @verifiedAt)
            ON CONFLICT(source_id) DO UPDATE SET verified_at = excluded.verified_at;
            """;
        stampCmd.Parameters.AddWithValue("@sourceId", sourceId);
        stampCmd.Parameters.AddWithValue("@verifiedAt", verifiedAt);
        stampCmd.ExecuteNonQuery();
    }

    private static readonly Dictionary<int, DateTimeOffset> EmptyVerifications = [];

    private static DateTimeOffset? ParseVerifiedAt(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Distinct-preserving-order: a caller passing the same id twice must not be charged two
    /// bound parameters for it, nor receive its row twice.
    /// </summary>
    private static List<int> Distinct(IReadOnlyList<int> ids)
    {
        var distinct = new List<int>(ids.Count);
        var seen = new HashSet<int>();
        foreach (var id in ids)
        {
            if (seen.Add(id))
                distinct.Add(id);
        }

        return distinct;
    }

    /// <summary>
    /// Binds one parameter per id and returns the generated placeholder names. Only the
    /// placeholder names are built into the SQL text; the id values are always bound.
    /// </summary>
    private static string[] BindIdList(SqliteCommand cmd, List<int> distinctIds)
    {
        var placeholders = new string[distinctIds.Count];
        for (var i = 0; i < distinctIds.Count; i++)
        {
            placeholders[i] = $"@id{i.ToString(CultureInfo.InvariantCulture)}";
            cmd.Parameters.AddWithValue(placeholders[i], distinctIds[i]);
        }

        return placeholders;
    }
}
