using System.Text.Json.Serialization;

namespace Twig.Infrastructure.GitHub;

/// <summary>
/// GitHub REST API response for a release. Uses explicit <see cref="JsonPropertyNameAttribute"/>
/// on every property because the GitHub API returns snake_case keys, but
/// <see cref="Serialization.TwigJsonContext"/> uses <c>CamelCase</c> naming policy.
/// </summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = [];
}

/// <summary>
/// GitHub REST API response for a release asset (binary download).
/// </summary>
internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
