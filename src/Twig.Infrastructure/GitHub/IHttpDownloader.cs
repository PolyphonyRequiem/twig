namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Abstracts HTTP file download so <see cref="SelfUpdater"/> can be unit tested
/// without hitting the network.
/// </summary>
internal interface IHttpDownloader
{
    /// <summary>
    /// Downloads the resource at <paramref name="url"/> and writes it to <paramref name="destinationPath"/>.
    /// </summary>
    Task DownloadFileAsync(string url, string destinationPath, CancellationToken ct);
}

/// <summary>
/// Default implementation that streams an HTTP GET response to a local file.
/// </summary>
internal sealed class HttpClientDownloader : IHttpDownloader
{
    private readonly HttpClient _http;

    public HttpClientDownloader(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
    }

    public async Task DownloadFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "twig-cli");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = File.Create(destinationPath);
        await stream.CopyToAsync(fileStream, ct);
    }
}
