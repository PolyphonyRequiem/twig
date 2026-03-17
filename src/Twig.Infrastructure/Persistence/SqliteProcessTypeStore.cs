using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Implements <see cref="IProcessTypeStore"/> against the existing <c>process_types</c> SQLite table.
/// </summary>
public sealed class SqliteProcessTypeStore : IProcessTypeStore
{
    private readonly SqliteCacheStore _store;

    public SqliteProcessTypeStore(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task<ProcessTypeRecord?> GetByNameAsync(string typeName, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT type_name, states_json, default_child_type, valid_child_types_json, color_hex, icon_id
            FROM process_types
            WHERE type_name = @typeName
            """;
        cmd.Parameters.AddWithValue("@typeName", typeName);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<ProcessTypeRecord?>(null);

        return Task.FromResult<ProcessTypeRecord?>(MapRow(reader));
    }

    public Task<IReadOnlyList<ProcessTypeRecord>> GetAllAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT type_name, states_json, default_child_type, valid_child_types_json, color_hex, icon_id
            FROM process_types
            ORDER BY type_name
            """;

        using var reader = cmd.ExecuteReader();
        var records = new List<ProcessTypeRecord>();
        while (reader.Read())
            records.Add(MapRow(reader));

        return Task.FromResult<IReadOnlyList<ProcessTypeRecord>>(records);
    }

    public Task SaveAsync(ProcessTypeRecord record, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();

        var statesJson = JsonSerializer.Serialize(
            record.States.ToList(),
            TwigJsonContext.Default.ListStateEntry);

        var childTypesJson = record.ValidChildTypes.Count > 0
            ? JsonSerializer.Serialize(
                record.ValidChildTypes.ToList(),
                TwigJsonContext.Default.ListString)
            : null;

        cmd.CommandText = """
            INSERT OR REPLACE INTO process_types
                (type_name, states_json, default_child_type, valid_child_types_json, color_hex, icon_id, last_synced_at)
            VALUES (@typeName, @statesJson, @defaultChildType, @validChildTypesJson, @colorHex, @iconId, @syncedAt)
            """;
        cmd.Parameters.AddWithValue("@typeName", record.TypeName);
        cmd.Parameters.AddWithValue("@statesJson", statesJson);
        cmd.Parameters.AddWithValue("@defaultChildType", record.DefaultChildType ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@validChildTypesJson", (object?)childTypesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@colorHex", record.ColorHex ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@iconId", record.IconId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@syncedAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    private static ProcessTypeRecord MapRow(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var typeName = reader.GetString(0);
        var statesJson = reader.GetString(1);
        var defaultChildType = reader.IsDBNull(2) ? null : reader.GetString(2);
        var validChildTypesJson = reader.IsDBNull(3) ? null : reader.GetString(3);
        var colorHex = reader.IsDBNull(4) ? null : reader.GetString(4);
        var iconId = reader.IsDBNull(5) ? null : reader.GetString(5);

        var states = DeserializeStateEntries(statesJson);
        var validChildTypes = validChildTypesJson is not null
            ? DeserializeList(validChildTypesJson)
            : (IReadOnlyList<string>)Array.Empty<string>();

        return new ProcessTypeRecord
        {
            TypeName = typeName,
            States = states,
            DefaultChildType = defaultChildType,
            ValidChildTypes = validChildTypes,
            ColorHex = colorHex,
            IconId = iconId,
        };
    }

    private static IReadOnlyList<string> DeserializeList(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize(json, TwigJsonContext.Default.ListString);
            return result is not null ? result : Array.Empty<string>();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"⚠ Failed to deserialize JSON list from process_types: {ex.Message}. Data may be corrupt — consider running 'twig init --force'.");
            return Array.Empty<string>();
        }
    }

    private const string ProcessConfigurationDataKey = "process_configuration_data";

    public Task SaveProcessConfigurationDataAsync(ProcessConfigurationData config, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(config, TwigJsonContext.Default.ProcessConfigurationData);

        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO metadata (key, value)
            VALUES (@key, @value)
            """;
        cmd.Parameters.AddWithValue("@key", ProcessConfigurationDataKey);
        cmd.Parameters.AddWithValue("@value", json);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<ProcessConfigurationData?> GetProcessConfigurationDataAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", ProcessConfigurationDataKey);

        var result = cmd.ExecuteScalar();
        if (result is not string json)
            return Task.FromResult<ProcessConfigurationData?>(null);

        try
        {
            return Task.FromResult(
                JsonSerializer.Deserialize(json, TwigJsonContext.Default.ProcessConfigurationData));
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"⚠ Failed to deserialize ProcessConfigurationData from cache: {ex.Message}. Data may be corrupt — consider running 'twig init --force'.");
            return Task.FromResult<ProcessConfigurationData?>(null);
        }
    }

    /// <summary>
    /// Deserializes a JSON array of <see cref="StateEntry"/> objects.
    /// Returns an empty array on corrupt or invalid JSON.
    /// </summary>
    private static IReadOnlyList<StateEntry> DeserializeStateEntries(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize(json, TwigJsonContext.Default.ListStateEntry);
            return result is not null ? result : Array.Empty<StateEntry>();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"⚠ Failed to deserialize state entries from process_types: {ex.Message}. Data may be corrupt — consider running 'twig init --force'.");
            return Array.Empty<StateEntry>();
        }
    }
}
