using System.Globalization;
using System.Text.Json;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Serialization;
using Twig.Providers.GitHub;
using Twig.Providers.Native;

namespace Twig.Infrastructure.Services;

// Map portable coordinates to immutable recorded identity, never to the display
// name (or current twig.json) of a candidate workspace.
internal static class DeclaredSourceMatcher
{
    internal static async Task<Func<Connection, bool>> CreateAsync(RegisteredProviderConnection binding,
        string registry, CancellationToken ct)
    {
        var requirement = binding.Requirement;
        if (requirement.Provider == "ado")
            return c => c.Provider.Value == "ado" && binding.BindingId is not null
                && IdentityParts(c) is [var id, var identity, var method, var principal]
                && id == binding.BindingId && identity == binding.IdentityId
                && (binding.PatPrincipal is { } pat ? method == "pat" && principal == pat : method == "aad");
        if (requirement.Provider == "twig")
        {
            var path = Path.Combine(Path.GetDirectoryName(registry)!, "native-databases", $"database-{requirement.Target}.db");
            for (var part = path; part is not null; part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked native authority.");
            await using var db = RepositoryReadConnection.Create(path);
            await db.OpenAsync(ct).ConfigureAwait(false);
            using var query = db.CreateCommand();
            query.CommandText = """
                SELECT id, principal FROM native_source
                WHERE (SELECT application_id FROM pragma_application_id) = 0x54574E41
                  AND (SELECT user_version FROM pragma_user_version) = 1;
                """;
            using var rows = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await rows.ReadAsync(ct).ConfigureAwait(false) || !Guid.TryParseExact(rows.GetString(0), "D", out var source)
                || source == Guid.Empty || rows.GetString(1) != NativeAuthority.CurrentPrincipal)
                throw new InvalidDataException("Native authority unavailable.");
            var key = source.ToString("D");
            var expected = new Connection(new("twig"), "twig://" + key, key, rows.GetString(1));
            if (await rows.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidDataException("Ambiguous native authority.");
            return c => c == expected;
        }
        if (!GitHubWorkspaceScope.TryCreate(new() { Host = requirement.Scope, Repository = requirement.Target },
                out var github, out _) || binding.Credential is not { } credential)
            throw new InvalidDataException("Missing GitHub binding.");
        var scope = github!.GraphQlEndpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        await using var system = RepositoryReadConnection.Create(registry);
        await system.OpenAsync(ct).ConfigureAwait(false);
        using var targets = system.CreateCommand();
        targets.CommandText = """
            SELECT binding_id,revision,descriptor_value FROM provider_target_bindings
            WHERE provider_kind='github' AND resource_scope=$scope AND descriptor_kind=$kind AND locator=$target COLLATE NOCASE;
            """;
        targets.Parameters.AddWithValue("$scope", scope);
        targets.Parameters.AddWithValue("$kind", GitHubRepositoryTarget.DescriptorKind);
        targets.Parameters.AddWithValue("$target", requirement.Target);
        using var result = await targets.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var exactTargets = new HashSet<(string, string, string)>();
        while (await result.ReadAsync(ct).ConfigureAwait(false))
        {
            if (JsonSerializer.Deserialize(result.GetString(2), TwigJsonContext.Default.StringArray) is not [var owner, var repo])
                throw new InvalidDataException("Invalid target identity.");
            exactTargets.Add((result.GetString(0), result.GetInt64(1).ToString(CultureInfo.InvariantCulture),
                GitHubProvider.Destination(owner, repo)));
        }
        if (exactTargets.Count > 1) throw new InvalidDataException("Ambiguous repository target identity.");
        return c => c.Provider.Value == "github" && c.ResourceScope == scope
            && IdentityParts(c) is [var id, _, _, var revision, var kind, var account, var client, var alias, _]
            && exactTargets.Contains((id, revision, c.Destination)) && kind == credential.Kind
            && account == (kind == "gh" ? credential.Selector : "")
            && alias == (kind == "github-app" ? credential.Selector : "") && client == (credential.ClientId ?? "");
    }

    private static string[]? IdentityParts(Connection connection)
    {
        try { return JsonSerializer.Deserialize(connection.IdentityRef, TwigJsonContext.Default.StringArray); }
        catch (JsonException) { return null; }
    }
}
