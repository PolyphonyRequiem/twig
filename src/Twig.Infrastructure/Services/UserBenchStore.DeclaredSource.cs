using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Services;

internal static class RepositoryReadConnection
{
    internal static SqliteConnection Create(string path)
    {
        return new(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    }
}

public sealed partial class UserBenchStore
{
    private async Task<UserRepositoryBindings> ProjectDeclaration(UserRepositoryBindings stored,
        SqliteConnection? db, SqliteTransaction? transaction, CancellationToken ct)
    {
        var manifest = Path.Combine(stored.RepositoryRoot, "twig.json");
        try
        {
            await using var file = File.OpenRead(manifest);
            if (file.Length > 1024 * 1024) throw new InvalidDataException("Oversized declaration.");
            var repo = await JsonSerializer.DeserializeAsync(file, TwigJsonContext.Default.TwigRepoConfig, ct).ConfigureAwait(false)
                ?? throw new JsonException("Missing declaration.");
            var requirement = ProviderConnectionRequirement.FromConfiguration(new() { RepoCoords = repo });
            stored = stored with { DeclaredSourceLabel = $"{requirement.Provider}: {requirement.Scope}/{requirement.Target}" };
            var registry = captureRegistry ?? Path.Combine(Path.GetDirectoryName(path)!, "system.db");
            RegisteredProviderConnection binding;
            try
            {
                binding = await new DeviceConnectionCatalog(registry).RequireAsync(requirement, ct,
                    repo.Organization, repo.Project).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return stored with { DeclaredSourceStatus = ex.Message };
            }
            var matches = new List<UserBenchWorkspace>();
            if (db is not null)
            {
                var accepts = await DeclaredSourceMatcher.CreateAsync(binding, registry, ct).ConfigureAwait(false);
                await using var query = db.CreateCommand();
                query.Transaction = transaction;
                query.CommandText = "SELECT root, connection, store_instance FROM workspaces;";
                await using var rows = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await rows.ReadAsync(ct).ConfigureAwait(false))
                {
                    var connection = JsonSerializer.Deserialize(rows.GetString(1), UserBenchJson.Default.Connection);
                    if (connection is not null && accepts(connection))
                        matches.Add(new(rows.GetString(0), connection, Guid.ParseExact(rows.GetString(2), "D")));
                }
            }
            if (matches.Count != 1)
                return stored with { DeclaredSourceStatus = matches.Count == 0
                    ? "No registered workspace is available for the declared source and identity."
                    : "The declared source has ambiguous workspace registrations; no workspace was selected." };
            var exact = matches[0];
            if (stored.ExcludedConnections.Contains(exact.Connection))
                return stored with { DeclaredSourceStatus = "The declared source is explicitly excluded from this repository." };
            await using var marker = File.OpenRead(Path.Combine(exact.Root, ".twig", CoreWorkspaceFactory.FileName));
            var pinned = await JsonSerializer.DeserializeAsync(marker, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument, ct).ConfigureAwait(false);
            if (pinned is not { Version: 1 } || pinned.Connection != exact.Connection
                || pinned.DatabaseInstance != exact.StoreInstance || !Path.IsPathFullyQualified(pinned.DatabasePath) || !File.Exists(pinned.DatabasePath))
                throw new InvalidDataException("Registered workspace evidence is unavailable.");
            await using var core = RepositoryReadConnection.Create(pinned.DatabasePath);
            await core.OpenAsync(ct).ConfigureAwait(false);
            using var verify = core.CreateCommand();
            verify.CommandText = "SELECT workspace_connection FROM core_identity WHERE id=$instance;";
            verify.Parameters.AddWithValue("$instance", exact.StoreInstance.ToString("D"));
            if (await verify.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string pin
                || JsonSerializer.Deserialize(pin, UserBenchJson.Default.Connection) != exact.Connection)
                throw new InvalidDataException("The registered workspace no longer has its exact store and connection.");
            return stored with { DeclaredConnection = exact.Connection };
        }
        catch (FileNotFoundException) when (stored.DeclaredSourceLabel is null && !File.Exists(manifest))
        {
            return stored;
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            return stored with { DeclaredSourceStatus = "The repository declaration or its registered local source is unavailable." };
        }
    }
}
