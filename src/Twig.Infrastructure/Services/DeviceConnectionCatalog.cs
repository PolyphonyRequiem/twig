using Microsoft.Data.Sqlite;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Providers.Native;

namespace Twig.Infrastructure.Services;

/// <summary>Portable provider coordinates. Neither credentials nor device-local IDs are part of this key.</summary>
internal sealed record ProviderConnectionRequirement(string Provider, string Scope, string Target)
{
    internal static ProviderConnectionRequirement FromConfiguration(TwigConfiguration configuration)
        => Create(configuration.ResolveWorkItemProviderKind(), configuration.Organization, configuration.Project,
            configuration.GitHub?.Host, configuration.GitHub?.Repository, configuration.NativeDatabase);

    internal static ProviderConnectionRequirement Create(string? provider, string? organization = null,
        string? project = null, string? host = null, string? repository = null, string? database = null)
    {
        switch (provider)
        {
            case "ado" when host is null && repository is null && database is null:
                if (NativeSetupEnrollment.AdoProblem(organization, project) is { } problem)
                    throw new InvalidOperationException(problem);
                var endpoint = new Uri(AdoEndpoint.NormalizeOrgUrl(organization!));
                var scope = endpoint.AbsoluteUri.TrimEnd('/');
                if (endpoint.Host == "dev.azure.com" && endpoint.AbsolutePath.Trim('/').Split('/') is { Length: 1 } segments)
                    scope = AdoEndpoint.NormalizeOrgUrl(segments[0]);
                else if (endpoint.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
                    && endpoint.AbsolutePath is "" or "/")
                    scope = AdoEndpoint.NormalizeOrgUrl(endpoint.Host[..^".visualstudio.com".Length]);
                return new("ado", scope.ToLowerInvariant(), project!.ToLowerInvariant());
            case "github" when string.IsNullOrEmpty(organization) && string.IsNullOrEmpty(project) && database is null:
                if (!GitHubWorkspaceScope.TryCreate(new GitHubConfig { Host = host, Repository = repository },
                    out var github, out var githubProblem))
                    throw new InvalidOperationException(githubProblem);
                return new("github", github!.Host, repository!.ToLowerInvariant());
            case "twig" when string.IsNullOrEmpty(organization) && string.IsNullOrEmpty(project)
                && host is null && repository is null && NativeDatabaseName.IsValid(database):
                return new("twig", "local", database!);
            default:
                throw new InvalidOperationException(
                    "Choose --provider ado with --org/--project, github with --host/--repository, or twig with --database. Do not mix provider dimensions.");
        }
    }

    internal string SetupArguments => Provider switch
    {
        "ado" => $"--provider ado --org \"{Scope}\" --project \"{Target}\"",
        "github" => $"--provider github --host \"{Scope}\" --repository \"{Target}\"",
        _ => $"--provider twig --database \"{Target}\"",
    };
}

/// <summary>A user-local credential selector, never credential material or portable repository configuration.</summary>
internal sealed record ProviderConnectionCredential(string Kind, string Selector, string? ClientId = null);
internal sealed record RegisteredProviderConnection(ProviderConnectionRequirement Requirement, ProviderConnectionCredential? Credential,
    string? Organization = null, string? Project = null, string? BindingId = null);

/// <summary>Device-local provider registrations shared across workspaces by the current user's server.</summary>
internal sealed class DeviceConnectionCatalog
{
    private readonly string path;

    internal DeviceConnectionCatalog(string systemDatabasePath)
    {
        if (!Path.IsPathFullyQualified(systemDatabasePath))
            throw new ArgumentException("An absolute system database path is required.", nameof(systemDatabasePath));
        path = systemDatabasePath;
    }

    internal Task<RegisteredProviderConnection> AddAsync(ProviderConnectionRequirement requirement, CancellationToken ct = default)
        => WriteAsync(requirement, null, add: true, ct);

    internal Task BindAsync(ProviderConnectionRequirement requirement, ProviderConnectionCredential credential,
        CancellationToken ct = default)
        => WriteAsync(requirement, credential, add: false, ct);

    internal Task<RegisteredProviderConnection> RegisterGitHubAsync(ProviderConnectionRequirement requirement,
        ProviderConnectionCredential credential, CancellationToken ct = default)
        => WriteAsync(requirement, credential, add: true, ct);

    private static void ValidateGitHubSelector(ProviderConnectionRequirement requirement, ProviderConnectionCredential credential)
    {
        if (requirement.Provider != "github" || string.IsNullOrWhiteSpace(credential.Selector) || credential.Selector != credential.Selector.Trim()
            || credential.Selector.Length > 256 || credential.Selector.Any(char.IsControl)
            || credential.Kind is not ("gh" or "github-app")
            || credential.Kind == "gh" && (credential.ClientId is not null || credential.Selector.StartsWith('-')
                || credential.Selector.Any(char.IsWhiteSpace))
            || credential.Kind == "github-app" && (requirement.Scope != "github.com"
                || string.IsNullOrWhiteSpace(credential.ClientId) || credential.ClientId.Any(char.IsWhiteSpace)
                || credential.ClientId.Any(char.IsControl)))
            throw new InvalidOperationException("Choose a GitHub --account or a GitHub App --identity and --client-id.");
    }

    private async Task<RegisteredProviderConnection> WriteAsync(ProviderConnectionRequirement requirement,
        ProviderConnectionCredential? credential, bool add, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (credential is not null) ValidateGitHubSelector(requirement, credential);
        if (!add && !File.Exists(path)) throw Missing(requirement);
        using var registry = new SqliteSystemWorktreeRegistry(path, TimeProvider.System);
        var result = await registry.ExecuteWriteAsync(async (db, tx) =>
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            Dimensions(command, requirement);
            if (add)
            {
                command.CommandText = """
                    INSERT INTO device_connections(provider,scope,target) VALUES($provider,$scope,$target)
                    ON CONFLICT(provider,scope,target) DO NOTHING;
                    """;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                command.CommandText = "SELECT 1 FROM device_connections WHERE provider=$provider AND scope=$scope AND target=$target;";
                if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is null) throw Missing(requirement);
            }
            if (credential is not null)
            {
                command.CommandText = """
                    INSERT INTO device_connection_selectors(provider,scope,target,kind,selector,client_id)
                    VALUES($provider,$scope,$target,$kind,$selector,$client)
                    ON CONFLICT(provider,scope,target,kind,selector,client_id) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$kind", credential.Kind);
                command.Parameters.AddWithValue("$selector", credential.Selector);
                command.Parameters.AddWithValue("$client", credential.ClientId ?? "");
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            return NativeResults.Ok(new RegisteredProviderConnection(requirement, credential));
        }, ct).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error.Message);
    }

    internal async Task<IReadOnlyList<RegisteredProviderConnection>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return [];
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        await db.OpenAsync(ct).ConfigureAwait(false);
        using var tx = db.BeginTransaction(deferred: true);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT version FROM layout_meta WHERE id=1;";
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long version
            || version != SqliteSystemWorktreeRegistry.SchemaVersion)
            throw new InvalidOperationException(AttachmentStorageFailure.SystemStoreSchemaMismatch);
        var rows = new List<RegisteredProviderConnection>();
        var ado = new HashSet<ProviderConnectionRequirement>();
        command.CommandText = """
            SELECT c.organization,c.project,b.binding_id,i.name
            FROM connection_bindings b
            JOIN connections c ON c.connection_ref=b.connection_ref
            JOIN identities i ON i.identity_id=b.identity_id;
            """;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var organization = reader.GetString(0);
                var project = reader.GetString(1);
                var requirement = ProviderConnectionRequirement.Create("ado", organization, project);
                rows.Add(new(requirement, new("ado", reader.GetString(3)), organization, project, reader.GetString(2)));
                ado.Add(requirement);
            }
        }
        command.CommandText = """
            SELECT c.provider,c.scope,c.target,s.kind,s.selector,NULLIF(s.client_id,'')
            FROM device_connections c LEFT JOIN device_connection_selectors s
            ON (c.provider,c.scope,c.target)=(s.provider,s.scope,s.target);
            """;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var requirement = new ProviderConnectionRequirement(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                if (ado.Contains(requirement)) continue;
                rows.Add(new(requirement, reader.IsDBNull(3) ? null : new(reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5))));
            }
        }
        return rows.OrderBy(r => r.Requirement.Provider, StringComparer.Ordinal)
            .ThenBy(r => r.Requirement.Scope, StringComparer.Ordinal).ThenBy(r => r.Requirement.Target, StringComparer.Ordinal)
            .ThenBy(r => r.Organization, StringComparer.Ordinal).ThenBy(r => r.Project, StringComparer.Ordinal)
            .ThenBy(r => r.Credential?.Kind, StringComparer.Ordinal).ThenBy(r => r.Credential?.Selector, StringComparer.Ordinal)
            .ThenBy(r => r.Credential?.ClientId, StringComparer.Ordinal).ThenBy(r => r.BindingId, StringComparer.Ordinal).ToArray();
    }

    internal async Task<RegisteredProviderConnection> RequireAsync(ProviderConnectionRequirement requirement,
        CancellationToken ct = default, string? organization = null, string? project = null, string? selector = null,
        string? clientId = null, string? kind = null)
    {
        var registered = (await ListAsync(ct).ConfigureAwait(false)).Where(r => r.Requirement == requirement).ToArray();
        var isAdo = requirement.Provider == "ado";
        var setup = isAdo
            ? $"--provider ado --org \"{organization ?? "<exact-org>"}\" --project \"{project ?? "<exact-project>"}\""
            : requirement.SetupArguments;
        var bind = $"twig connection bind {setup} --identity <registered-alias>";
        if (registered.Length == 0)
        {
            if (!isAdo) throw Missing(requirement);
            throw new InvalidOperationException($"Required connection is not registered on this device. Run 'twig connection add {setup}', then '{bind}'.");
        }
        if (requirement.Provider != "twig" && registered.All(r => r.Credential is null))
            throw new InvalidOperationException(isAdo
                ? $"Connection has no credential binding for this user. Run '{bind}'."
                : $"Connection has no credential binding for this user. Run 'twig connection bind {setup}' with your own --identity or --account.");
        var matches = registered.Where(r => (!isAdo
                || (organization is null || r.Organization == organization) && (project is null || r.Project == project))
            && (kind is null || r.Credential?.Kind == kind)
            && (selector is null || r.Credential?.Selector == selector) && (clientId is null || r.Credential?.ClientId == clientId)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 when isAdo => throw new InvalidOperationException($"No credential binding matches these exact ADO coordinates. Use the organization and project exactly as spelled in twig.json (or the init arguments). Run '{bind}'. No fallback was used."),
            0 => throw new InvalidOperationException("No registered connection binding matches the exact requested coordinates and credential selector. No fallback was used."),
            _ when isAdo => throw new InvalidOperationException(
                $"The ADO connection selection is ambiguous. Choose an existing binding from 'twig connection list {setup}', then run 'twig init --binding <binding-id>'."),
            _ => throw new InvalidOperationException("The connection selection is ambiguous. Specify the exact coordinates and an explicit credential selector; no default was used."),
        };
    }

    internal const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS device_connections(
            provider TEXT NOT NULL,scope TEXT NOT NULL,target TEXT NOT NULL,
            PRIMARY KEY(provider,scope,target));
        CREATE TABLE IF NOT EXISTS device_connection_selectors(
            provider TEXT NOT NULL CHECK(provider='github'),scope TEXT NOT NULL,target TEXT NOT NULL,
            kind TEXT NOT NULL,selector TEXT NOT NULL,client_id TEXT NOT NULL,
            PRIMARY KEY(provider,scope,target,kind,selector,client_id),
            FOREIGN KEY(provider,scope,target) REFERENCES device_connections(provider,scope,target));
        """;

    private static InvalidOperationException Missing(ProviderConnectionRequirement requirement)
        => new($"Required connection is not registered on this device. Run 'twig connection add {requirement.SetupArguments}', then bind your own credentials. No workspace or account fallback was used.");

    private static void Dimensions(SqliteCommand command, ProviderConnectionRequirement requirement)
    {
        command.Parameters.AddWithValue("$provider", requirement.Provider);
        command.Parameters.AddWithValue("$scope", requirement.Scope);
        command.Parameters.AddWithValue("$target", requirement.Target);
    }
}
