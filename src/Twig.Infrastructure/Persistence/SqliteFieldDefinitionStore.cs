using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IFieldDefinitionStore"/>.
/// Stores field definitions in the <c>field_definitions</c> table.
/// </summary>
public sealed class SqliteFieldDefinitionStore : IFieldDefinitionStore
{
    private readonly SqliteCacheStore _store;

    public SqliteFieldDefinitionStore(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task<FieldDefinition?> GetByReferenceNameAsync(string referenceName, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ref_name, display_name, data_type, is_read_only
            FROM field_definitions
            WHERE ref_name = @refName
            """;
        cmd.Parameters.AddWithValue("@refName", referenceName);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<FieldDefinition?>(null);

        return Task.FromResult<FieldDefinition?>(MapRow(reader));
    }

    public Task<IReadOnlyList<FieldDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ref_name, display_name, data_type, is_read_only
            FROM field_definitions
            ORDER BY ref_name
            """;

        using var reader = cmd.ExecuteReader();
        var defs = new List<FieldDefinition>();
        while (reader.Read())
            defs.Add(MapRow(reader));

        return Task.FromResult<IReadOnlyList<FieldDefinition>>(defs);
    }

    public Task SaveBatchAsync(IReadOnlyList<FieldDefinition> definitions, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var def in definitions)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO field_definitions
                        (ref_name, display_name, data_type, is_read_only, last_synced_at)
                    VALUES (@refName, @displayName, @dataType, @isReadOnly, @syncedAt)
                    """;
                cmd.Parameters.AddWithValue("@refName", def.ReferenceName);
                cmd.Parameters.AddWithValue("@displayName", def.DisplayName);
                cmd.Parameters.AddWithValue("@dataType", def.DataType);
                cmd.Parameters.AddWithValue("@isReadOnly", def.IsReadOnly ? 1 : 0);
                cmd.Parameters.AddWithValue("@syncedAt", DateTime.UtcNow.ToString("o"));
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

    private static FieldDefinition MapRow(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new FieldDefinition(
            ReferenceName: reader.GetString(0),
            DisplayName: reader.GetString(1),
            DataType: reader.GetString(2),
            IsReadOnly: reader.GetInt32(3) == 1);
    }
}
