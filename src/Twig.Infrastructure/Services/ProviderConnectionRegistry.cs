using Microsoft.Data.Sqlite;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
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
internal sealed record RegisteredProviderConnection(ProviderConnectionRequirement Requirement, ProviderConnectionCredential? Credential);

/// <summary>Device-local provider registrations shared across workspaces by the current user's daemon.</summary>
internal sealed class ProviderConnectionRegistry
{
    private readonly string path;

    internal ProviderConnectionRegistry(string nativeRegistryPath)
    {
        if (!Path.IsPathFullyQualified(nativeRegistryPath))
            throw new ArgumentException("An absolute native registry path is required.", nameof(nativeRegistryPath));
        path = Path.Combine(Path.GetDirectoryName(nativeRegistryPath)!, "provider-connections.db");
    }

    internal async Task<RegisteredProviderConnection> AddAsync(ProviderConnectionRequirement requirement, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO connections(provider,scope,target) VALUES($provider,$scope,$target)
            ON CONFLICT(provider,scope,target) DO NOTHING;
            """;
        Dimensions(command, requirement);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return await FindAsync(db, null, requirement, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The registered provider connection could not be read back.");
    }

    internal async Task BindAsync(ProviderConnectionRequirement requirement, ProviderConnectionCredential credential,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credential.Selector) || credential.Selector != credential.Selector.Trim()
            || credential.Selector.Length > 256 || credential.Selector.Any(char.IsControl)
            || requirement.Provider == "ado" && (credential.Kind != "ado" || credential.ClientId is not null)
            || requirement.Provider == "github" && (credential.Kind is not ("gh" or "github-app")
                || credential.Kind == "gh" && (credential.ClientId is not null || credential.Selector.StartsWith('-')
                    || credential.Selector.Any(char.IsWhiteSpace))
                || credential.Kind == "github-app" && (requirement.Scope != "github.com"
                    || string.IsNullOrWhiteSpace(credential.ClientId) || credential.ClientId.Any(char.IsWhiteSpace)
                    || credential.ClientId.Any(char.IsControl)))
            || requirement.Provider == "twig")
            throw new InvalidOperationException("Choose a registered ADO --identity, a GitHub --account, or a GitHub App --identity and --client-id. Native connections do not need credentials.");
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        using var transaction = db.BeginTransaction(deferred: false);
        var current = await FindAsync(db, transaction, requirement, ct).ConfigureAwait(false);
        if (current is null) throw Missing(requirement);
        if (current.Credential is { } existing && existing != credential)
            throw new InvalidOperationException("This connection already has a different user credential binding. No credential or existing workspace selection was switched.");
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO credential_bindings(provider,scope,target,kind,selector,client_id)
            VALUES($provider,$scope,$target,$kind,$selector,$client)
            ON CONFLICT(provider,scope,target) DO NOTHING;
            """;
        Dimensions(command, requirement);
        command.Parameters.AddWithValue("$kind", credential.Kind);
        command.Parameters.AddWithValue("$selector", credential.Selector);
        command.Parameters.AddWithValue("$client", (object?)credential.ClientId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    internal async Task<IReadOnlyList<RegisteredProviderConnection>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        using var command = db.CreateCommand();
        command.CommandText = Select + " ORDER BY c.provider,c.scope,c.target;";
        var result = new List<RegisteredProviderConnection>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(Read(reader));
        return result;
    }

    internal async Task<RegisteredProviderConnection> RequireAsync(ProviderConnectionRequirement requirement,
        CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        var found = await FindAsync(db, null, requirement, ct).ConfigureAwait(false) ?? throw Missing(requirement);
        if (requirement.Provider != "twig" && found.Credential is null)
            throw new InvalidOperationException(
                $"Connection has no credential binding for this user. Run 'twig connection bind {requirement.SetupArguments}' with your own --identity or --account.");
        return found;
    }

    private static InvalidOperationException Missing(ProviderConnectionRequirement requirement)
        => new($"Required connection is not registered on this device. Run 'twig connection add {requirement.SetupArguments}', then bind your own credentials. No workspace or account fallback was used.");

    private const string Select = """
        SELECT c.provider,c.scope,c.target,b.kind,b.selector,b.client_id
        FROM connections c LEFT JOIN credential_bindings b
        ON b.provider=c.provider AND b.scope=c.scope AND b.target=c.target
        """;

    private static async Task<RegisteredProviderConnection?> FindAsync(SqliteConnection db, SqliteTransaction? transaction,
        ProviderConnectionRequirement requirement, CancellationToken ct)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Select + " WHERE c.provider=$provider AND c.scope=$scope AND c.target=$target;";
        Dimensions(command, requirement);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static RegisteredProviderConnection Read(SqliteDataReader reader)
        => new(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
            reader.IsDBNull(3) ? null : new(reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));

    private static void Dimensions(SqliteCommand command, ProviderConnectionRequirement requirement)
    {
        command.Parameters.AddWithValue("$provider", requirement.Provider);
        command.Parameters.AddWithValue("$scope", requirement.Scope);
        command.Parameters.AddWithValue("$target", requirement.Target);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, ForeignKeys = true, DefaultTimeout = 5,
        }.ToString());
        try
        {
            await db.OpenAsync(ct).ConfigureAwait(false);
            using var command = db.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS connections(
                    provider TEXT NOT NULL,scope TEXT NOT NULL,target TEXT NOT NULL,
                    PRIMARY KEY(provider,scope,target));
                CREATE TABLE IF NOT EXISTS credential_bindings(
                    provider TEXT NOT NULL,scope TEXT NOT NULL,target TEXT NOT NULL,
                    kind TEXT NOT NULL,selector TEXT NOT NULL,client_id TEXT,
                    PRIMARY KEY(provider,scope,target),
                    FOREIGN KEY(provider,scope,target) REFERENCES connections(provider,scope,target));
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return db;
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
