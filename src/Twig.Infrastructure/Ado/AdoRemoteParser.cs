using System.Text.RegularExpressions;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Parses ADO git remote URLs to extract organization, project, and repository name.
/// </summary>
internal static partial class AdoRemoteParser
{
    /// <summary>
    /// Parses an ADO git remote URL to extract organization, project, and repository name.
    /// Supports HTTPS format: https://dev.azure.com/{org}/{project}/_git/{repo}
    /// Supports SSH format: git@ssh.dev.azure.com:v3/{org}/{project}/{repo}
    /// Supports legacy HTTPS format: https://{org}.visualstudio.com/{project}/_git/{repo}
    /// Returns null if the URL doesn't match ADO patterns.
    /// </summary>
    public static AdoRemoteInfo? Parse(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return null;

        var trimmed = remoteUrl.Trim();

        // HTTPS: https://dev.azure.com/{org}/{project}/_git/{repo}
        var match = HttpsPattern().Match(trimmed);
        if (match.Success)
        {
            return new AdoRemoteInfo(
                Uri.UnescapeDataString(match.Groups["org"].Value),
                Uri.UnescapeDataString(match.Groups["project"].Value),
                Uri.UnescapeDataString(match.Groups["repo"].Value));
        }

        // SSH: git@ssh.dev.azure.com:v3/{org}/{project}/{repo}
        match = SshPattern().Match(trimmed);
        if (match.Success)
        {
            return new AdoRemoteInfo(
                Uri.UnescapeDataString(match.Groups["org"].Value),
                Uri.UnescapeDataString(match.Groups["project"].Value),
                Uri.UnescapeDataString(match.Groups["repo"].Value));
        }

        // Legacy HTTPS: https://{org}.visualstudio.com/{project}/_git/{repo}
        match = LegacyHttpsPattern().Match(trimmed);
        if (match.Success)
        {
            return new AdoRemoteInfo(
                Uri.UnescapeDataString(match.Groups["org"].Value),
                Uri.UnescapeDataString(match.Groups["project"].Value),
                Uri.UnescapeDataString(match.Groups["repo"].Value));
        }

        return null;
    }

    [GeneratedRegex(@"^https?://dev\.azure\.com/(?<org>[^/]+)/(?<project>[^/]+)/_git/(?<repo>[^/\s]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex HttpsPattern();

    [GeneratedRegex(@"^git@ssh\.dev\.azure\.com:v3/(?<org>[^/]+)/(?<project>[^/]+)/(?<repo>[^/\s]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SshPattern();

    [GeneratedRegex(@"^https?://(?<org>[^.]+)\.visualstudio\.com/(?<project>[^/]+)/_git/(?<repo>[^/\s]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyHttpsPattern();
}

/// <summary>
/// Extracted ADO remote URL components.
/// </summary>
internal sealed record AdoRemoteInfo(string Organization, string Project, string Repository);
