using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Implements <see cref="IGitHubReleaseService"/> via the GitHub REST API.
/// </summary>
internal sealed class GitHubReleaseClient : IGitHubReleaseService
{
    private readonly HttpClient _http;
    private readonly string _repoSlug;

    public GitHubReleaseClient(HttpClient httpClient, string repoSlug)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (string.IsNullOrWhiteSpace(repoSlug))
            throw new ArgumentException("GitHub repository slug is required.", nameof(repoSlug));

        _http = httpClient;
        _repoSlug = repoSlug;
    }

    public Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default) =>
        FetchSingleReleaseAsync($"https://api.github.com/repos/{_repoSlug}/releases/latest", ct);

    public async Task<IReadOnlyList<GitHubReleaseInfo>> GetReleasesAsync(int count, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{_repoSlug}/releases?per_page={count}";
        using var request = CreateRequest(url);
        using var response = await _http.SendAsync(request, ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var dtos = JsonSerializer.Deserialize(json, TwigJsonContext.Default.ListGitHubRelease);
        if (dtos is null) return [];

        var results = new List<GitHubReleaseInfo>(dtos.Count);
        foreach (var dto in dtos)
            results.Add(MapToInfo(dto));
        return results;
    }

    public Task<GitHubReleaseInfo?> GetReleaseByTagAsync(string tag, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return FetchSingleReleaseAsync($"https://api.github.com/repos/{_repoSlug}/releases/tags/{tag}", ct);
    }

    private async Task<GitHubReleaseInfo?> FetchSingleReleaseAsync(string url, CancellationToken ct)
    {
        using var request = CreateRequest(url);
        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize(json, TwigJsonContext.Default.GitHubRelease);
        return dto is null ? null : MapToInfo(dto);
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "twig-cli");
        request.Headers.Add("Accept", "application/vnd.github+json");
        return request;
    }

    private static GitHubReleaseInfo MapToInfo(GitHubRelease dto)
    {
        var assets = new List<GitHubReleaseAssetInfo>(dto.Assets.Count);
        foreach (var a in dto.Assets)
            assets.Add(new GitHubReleaseAssetInfo(a.Name, a.BrowserDownloadUrl, a.Size));
        DateTimeOffset? publishedAt = null;
        if (!string.IsNullOrEmpty(dto.PublishedAt) && DateTimeOffset.TryParse(dto.PublishedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            publishedAt = parsed;
        return new GitHubReleaseInfo(dto.TagName, dto.Name, dto.Body, publishedAt, assets);
    }
}
