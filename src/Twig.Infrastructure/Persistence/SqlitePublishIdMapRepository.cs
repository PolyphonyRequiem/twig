using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IPublishIdMapRepository"/>.
/// All queries use parameterized SQL — no string interpolation.
/// <para>
/// Wayfinder 0014: reads and writes are keyed on <c>staged_identity</c>. The legacy
/// <c>old_id</c> column is retained as the display alias — the migration backfilled a
/// synthetic identity for every pre-0014 row, so no row is unreachable, and nothing joins on
/// the alias any more.
/// </para>
/// </summary>
public sealed class SqlitePublishIdMapRepository : IPublishIdMapRepository
{
    private readonly SqliteCacheStore _store;
    private readonly IStagedIdentityRegistry _registry;

    public SqlitePublishIdMapRepository(SqliteCacheStore store, IStagedIdentityRegistry registry)
    {
        _store = store;
        _registry = registry;
    }

    public async Task RecordMappingAsync(StagedIdentity identity, int newId, CancellationToken ct = default)
    {
        // The alias is recorded for display only. It comes from the durable register, so a row
        // written here can always be rendered as the number the user saw while staging.
        var alias = await _registry.FindAliasAsync(identity, ct);

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = """
            INSERT INTO publish_id_map (old_id, new_id, published_at, staged_identity)
            VALUES (@oldId, @newId, @publishedAt, @identity)
            ON CONFLICT(old_id) DO UPDATE SET
                new_id = excluded.new_id,
                published_at = excluded.published_at,
                staged_identity = excluded.staged_identity;
            """;
        cmd.Parameters.AddWithValue("@oldId", alias?.Value ?? 0);
        cmd.Parameters.AddWithValue("@newId", newId);
        cmd.Parameters.AddWithValue("@publishedAt", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@identity", identity.ToString());
        cmd.ExecuteNonQuery();
    }

    public Task<int?> GetNewIdAsync(StagedIdentity identity, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT new_id FROM publish_id_map WHERE staged_identity = @identity;";
        cmd.Parameters.AddWithValue("@identity", identity.ToString());

        var result = cmd.ExecuteScalar();
        var newId = result is DBNull or null ? (int?)null : Convert.ToInt32(result);
        return Task.FromResult(newId);
    }

    public async Task<int?> GetNewIdByAliasAsync(StagedAlias alias, CancellationToken ct = default)
    {
        // Resolve through the register rather than reading old_id directly: the alias is not a
        // key, and an unknown alias must stay visibly unknown rather than be coerced into a
        // plausible known one (0003 §4).
        var identity = await _registry.FindByAliasAsync(alias, ct);
        if (identity is null)
            return null;

        return await GetNewIdAsync(identity.Value, ct);
    }

    public Task<IReadOnlyList<PublishMapping>> GetAllMappingsAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT staged_identity, old_id, new_id
            FROM publish_id_map
            WHERE staged_identity IS NOT NULL
            ORDER BY old_id;
            """;

        var mappings = new List<PublishMapping>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!StagedIdentity.TryParse(reader.GetString(0), out var identity))
                continue;

            StagedAlias? alias = !reader.IsDBNull(1) && StagedAlias.TryFrom(reader.GetInt32(1), out var parsed)
                ? parsed
                : null;

            mappings.Add(new PublishMapping(identity, alias, reader.GetInt32(2)));
        }

        return Task.FromResult<IReadOnlyList<PublishMapping>>(mappings);
    }
}
