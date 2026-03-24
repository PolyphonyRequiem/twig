using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="ISeedLinkRepository"/>.
/// All queries use parameterized SQL — no string interpolation.
/// </summary>
public sealed class SqliteSeedLinkRepository : ISeedLinkRepository
{
    private readonly SqliteCacheStore _store;

    public SqliteSeedLinkRepository(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task AddLinkAsync(SeedLink link, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO seed_links (source_id, target_id, link_type, created_at)
            VALUES (@sourceId, @targetId, @linkType, @createdAt);
            """;
        cmd.Parameters.AddWithValue("@sourceId", link.SourceId);
        cmd.Parameters.AddWithValue("@targetId", link.TargetId);
        cmd.Parameters.AddWithValue("@linkType", link.LinkType);
        cmd.Parameters.AddWithValue("@createdAt", link.CreatedAt.ToString("o"));
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task RemoveLinkAsync(int sourceId, int targetId, string linkType, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM seed_links
            WHERE source_id = @sourceId AND target_id = @targetId AND link_type = @linkType;
            """;
        cmd.Parameters.AddWithValue("@sourceId", sourceId);
        cmd.Parameters.AddWithValue("@targetId", targetId);
        cmd.Parameters.AddWithValue("@linkType", linkType);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SeedLink>> GetLinksForItemAsync(int itemId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source_id, target_id, link_type, created_at
            FROM seed_links
            WHERE source_id = @id OR target_id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", itemId);
        return Task.FromResult(ReadLinks(cmd));
    }

    public Task<IReadOnlyList<SeedLink>> GetAllSeedLinksAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_id, target_id, link_type, created_at FROM seed_links;";
        return Task.FromResult(ReadLinks(cmd));
    }

    private static IReadOnlyList<SeedLink> ReadLinks(SqliteCommand cmd)
    {
        var links = new List<SeedLink>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            links.Add(new SeedLink(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3))));
        }
        return links;
    }

    public Task DeleteLinksForItemAsync(int itemId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM seed_links WHERE source_id = @id OR target_id = @id;";
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task RemapIdAsync(int oldId, int newId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();

        using var srcCmd = conn.CreateCommand();
        srcCmd.Transaction = _store.ActiveTransaction;
        srcCmd.CommandText = "UPDATE seed_links SET source_id = @newId WHERE source_id = @oldId;";
        srcCmd.Parameters.AddWithValue("@newId", newId);
        srcCmd.Parameters.AddWithValue("@oldId", oldId);
        srcCmd.ExecuteNonQuery();

        using var tgtCmd = conn.CreateCommand();
        tgtCmd.Transaction = _store.ActiveTransaction;
        tgtCmd.CommandText = "UPDATE seed_links SET target_id = @newId WHERE target_id = @oldId;";
        tgtCmd.Parameters.AddWithValue("@newId", newId);
        tgtCmd.Parameters.AddWithValue("@oldId", oldId);
        tgtCmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }
}
