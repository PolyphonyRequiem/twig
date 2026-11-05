using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Persistence;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Services;

internal sealed record InitialAuthoringObservation(string PriorReadOnlyRelease, string PairedCliBuild, string PanelBuild);

internal static class NativeInitialAuthoring
{
    internal const string ReadOnlyRelease = "0638f75a134bcbcecc78fbd015bf0c82bd11651f";
    internal static string NativeBuild => typeof(NativeItemEditor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Statement(string digest) => Hash("Adopt the exact prior supported readonly namespace; preserve all Bench/Core debt; complete actual inventory and paired new CLI/panel; all old processes quiesced:" + digest);

    internal static async Task<string> Evidence(string path, InitialAuthoringObservation observation,
        IEnumerable<NativeCoreStoreReference> sources, CancellationToken ct)
    {
        if (observation.PriorReadOnlyRelease != ReadOnlyRelease || string.IsNullOrWhiteSpace(observation.PairedCliBuild)
            || observation.PairedCliBuild != NativeBuild || string.IsNullOrWhiteSpace(observation.PanelBuild)
            || observation.PanelBuild.Length > 256)
            throw new InvalidDataException("An actual supported readonly release and exact observed paired build identities are required.");
        var sourceList = sources.ToArray();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await db.OpenAsync(ct).ConfigureAwait(false);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'editor_%';";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0)
            throw new InvalidDataException("Ever-initialized or lost capture storage cannot be adopted as readonly.");
        command.CommandText = "PRAGMA table_info(meta);";
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                if (reader.GetString(1).StartsWith("editor_", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Retained capture initialization/generation evidence prevents adoption.");
        var evidence = new List<string> { observation.PriorReadOnlyRelease, observation.PairedCliBuild, observation.PanelBuild,
            Path.GetFullPath(path), await Digest(path, ct).ConfigureAwait(false) };
        foreach (var source in sourceList.OrderBy(value => value.Instance))
        {
            evidence.Add(source.Instance.ToString("D") + ":" + await Digest(source.DatabasePath, ct).ConfigureAwait(false));
            var marker = Path.Combine(source.Root, ".twig", CoreWorkspaceFactory.FileName);
            if (new FileInfo(marker).Length > 65536) throw new InvalidDataException("Source locator exceeds its bound.");
            evidence.Add(Hash(await File.ReadAllTextAsync(marker, ct).ConfigureAwait(false)));
        }
        return "readonly-adoption:" + JsonSerializer.Serialize(evidence.ToArray(), NativeCoreExecutionJson.Default.StringArray);
    }

    private static async Task<string> Digest(string path, CancellationToken ct)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await db.OpenAsync(ct).ConfigureAwait(false);
        using var tx = db.BeginTransaction(deferred: true);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var names = new List<string>();
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) names.Add(reader.GetString(0));
        if (names.Count > 64) throw new InvalidDataException("Initial authoring inventory exceeds its table bound.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        command.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name;";
        var schemaCount = 0;
        using (var schema = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await schema.ReadAsync(ct).ConfigureAwait(false))
            {
                if (++schemaCount > 256) throw new InvalidDataException("Initial authoring schema exceeds its bound.");
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, 4)
                    .Select(index => schema.GetValue(index).ToString()!).ToArray(), NativeCoreExecutionJson.Default.StringArray));
            }
        long bytes = 0;
        foreach (var name in names)
        {
            command.CommandText = "SELECT sql FROM sqlite_master WHERE name=$name;";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$name", name);
            hash.AppendData(Encoding.UTF8.GetBytes((string)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!));
            command.Parameters.Clear();
            command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\";";
            var rows = new List<string>();
            using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var row = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, reader.FieldCount).Select(index =>
                        reader.GetValue(index) is byte[] data ? "blob:" + Convert.ToHexString(data)
                        : reader.GetValue(index).GetType().FullName + ":" + Convert.ToString(reader.GetValue(index),
                            System.Globalization.CultureInfo.InvariantCulture)).ToArray(), NativeCoreExecutionJson.Default.StringArray);
                    bytes += row.Length;
                    if (bytes > 64 * 1024 * 1024 || rows.Count >= 100000) throw new InvalidDataException("Initial authoring inventory exceeds its row/byte bound.");
                    rows.Add(Convert.ToHexStringLower(SHA256.HashData(row)));
                }
            foreach (var row in rows.Order(StringComparer.Ordinal)) hash.AppendData(Encoding.UTF8.GetBytes(row));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
