using System.Text.RegularExpressions;

namespace Twig.Infrastructure.Config;

/// <summary>
/// Epic #1167 explicit GitHub workspace scope (<c>github.host</c>, <c>github.repository</c>).
/// Consulted only when the workspace selects <c>provider=github</c>. Nothing here is inferred
/// from the git remote; both values must be set explicitly.
/// </summary>
public sealed class GitHubConfig
{
    /// <summary>Bare lowercase host name, e.g. <c>github.com</c>.</summary>
    public string? Host { get; set; }

    /// <summary>Exactly <c>owner/name</c>.</summary>
    public string? Repository { get; set; }
}

/// <summary>A validated GitHub workspace scope: host, repository and its GraphQL endpoint.</summary>
internal sealed partial record GitHubWorkspaceScope(string Host, string Repository, Uri GraphQlEndpoint)
{
    /// <summary>Normalizes and validates a host; null when it is not a bare DNS host name.</summary>
    public static string? NormalizeHost(string? value)
    {
        if (value is null)
            return null;
        var lowered = value.ToLowerInvariant();
        return HostPattern().IsMatch(lowered) && lowered.Length <= 253 ? lowered : null;
    }

    /// <summary>Validates <c>owner/name</c>; null when malformed.</summary>
    public static string? NormalizeRepository(string? value)
    {
        if (value is null || !RepositoryPattern().IsMatch(value))
            return null;
        var name = value[(value.IndexOf('/') + 1)..];
        return name is "." or ".." ? null : value;
    }

    /// <summary>
    /// Builds the scope from stored config, or explains (without echoing secrets) why it is
    /// missing or malformed.
    /// </summary>
    public static bool TryCreate(GitHubConfig? config, out GitHubWorkspaceScope? scope, out string problem)
    {
        scope = null;
        if (config?.Host is null || config.Repository is null)
        {
            problem = "github.host and github.repository must both be set explicitly";
            return false;
        }
        var host = NormalizeHost(config.Host);
        if (host is null || !string.Equals(host, config.Host, StringComparison.Ordinal))
        {
            problem = "github.host must be a bare lowercase host name such as github.com";
            return false;
        }
        var repository = NormalizeRepository(config.Repository);
        if (repository is null)
        {
            problem = "github.repository must be exactly owner/name";
            return false;
        }
        var endpoint = host == "github.com"
            ? new Uri("https://api.github.com/graphql")
            : new Uri($"https://{host}/api/graphql");
        scope = new GitHubWorkspaceScope(host, repository, endpoint);
        problem = string.Empty;
        return true;
    }

    [GeneratedRegex(@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();
}
