using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IPublishIdMapRepository"/>.
/// All queries use parameterized SQL — no string interpolation.
/// </summary>
public sealed class SqlitePublishIdMapRepository : IPublishIdMapRepository
{
    private readonly SqliteCacheStore _store;

    public SqlitePublishIdMapRepository(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task RecordMappingAsync(int oldId, int newId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            INSERT OR REPLACE INTO publish_id_map (old_id, new_id, published_at)
            VALUES (@oldId, @newId, @publishedAt);
            """;
        cmd.Parameters.AddWithValue("@oldId", oldId);
        cmd.Parameters.AddWithValue("@newId", newId);
        cmd.Parameters.AddWithValue("@publishedAt", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<int?> GetNewIdAsync(int oldId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT new_id FROM publish_id_map WHERE old_id = @oldId;";
        cmd.Parameters.AddWithValue("@oldId", oldId);

        var result = cmd.ExecuteScalar();
        var newId = result is DBNull || result is null ? (int?)null : Convert.ToInt32(result);
        return Task.FromResult(newId);
    }

    public Task<IReadOnlyList<(int OldId, int NewId)>> GetAllMappingsAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT old_id, new_id FROM publish_id_map ORDER BY old_id;";

        var mappings = new List<(int OldId, int NewId)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            mappings.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return Task.FromResult<IReadOnlyList<(int OldId, int NewId)>>(mappings);
    }
}
