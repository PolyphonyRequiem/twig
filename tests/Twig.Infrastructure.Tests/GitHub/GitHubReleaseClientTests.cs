using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

/// <summary>
/// Tests for <see cref="GitHubReleaseClient"/> JSON deserialization.
/// Validates that snake_case GitHub API keys deserialize correctly via
/// explicit <c>[JsonPropertyName]</c> attributes.
/// </summary>
public class GitHubReleaseClientTests
{
    // ── DTO deserialization ───────────────────────────────────────────

    [Fact]
    public void GitHubRelease_Deserializes_SnakeCaseKeys()
    {
        const string json = """
        {
            "tag_name": "v1.2.0",
            "name": "Release 1.2.0",
            "body": "## What's new\n- Feature A",
            "assets": [
                {
                    "name": "twig-win-x64.zip",
                    "browser_download_url": "https://github.com/PolyphonyRequiem/twig/releases/download/v1.2.0/twig-win-x64.zip",
                    "size": 5242880
                },
                {
                    "name": "twig-linux-x64.tar.gz",
                    "browser_download_url": "https://github.com/PolyphonyRequiem/twig/releases/download/v1.2.0/twig-linux-x64.tar.gz",
                    "size": 4194304
                }
            ]
        }
        """;

        var release = JsonSerializer.Deserialize(json, TwigJsonContext.Default.GitHubRelease);

        release.ShouldNotBeNull();
        release.TagName.ShouldBe("v1.2.0");
        release.Name.ShouldBe("Release 1.2.0");
        release.Body.ShouldContain("Feature A");
        release.Assets.Count.ShouldBe(2);

        release.Assets[0].Name.ShouldBe("twig-win-x64.zip");
        release.Assets[0].BrowserDownloadUrl.ShouldContain("twig-win-x64.zip");
        release.Assets[0].Size.ShouldBe(5242880);

        release.Assets[1].Name.ShouldBe("twig-linux-x64.tar.gz");
        release.Assets[1].BrowserDownloadUrl.ShouldContain("twig-linux-x64.tar.gz");
        release.Assets[1].Size.ShouldBe(4194304);
    }

    [Fact]
    public void GitHubReleaseList_Deserializes_RawArray()
    {
        const string json = """
        [
            {
                "tag_name": "v1.2.0",
                "name": "Release 1.2.0",
                "body": "Notes for 1.2.0",
                "assets": []
            },
            {
                "tag_name": "v1.1.0",
                "name": "Release 1.1.0",
                "body": "Notes for 1.1.0",
                "assets": [
                    {
                        "name": "twig-osx-arm64.tar.gz",
                        "browser_download_url": "https://example.com/twig-osx-arm64.tar.gz",
                        "size": 3145728
                    }
                ]
            }
        ]
        """;

        var releases = JsonSerializer.Deserialize(json, TwigJsonContext.Default.ListGitHubRelease);

        releases.ShouldNotBeNull();
        releases.Count.ShouldBe(2);
        releases[0].TagName.ShouldBe("v1.2.0");
        releases[1].TagName.ShouldBe("v1.1.0");
        releases[1].Assets.Count.ShouldBe(1);
        releases[1].Assets[0].BrowserDownloadUrl.ShouldBe("https://example.com/twig-osx-arm64.tar.gz");
    }

    [Fact]
    public void GitHubRelease_EmptyAssets_DeserializesEmpty()
    {
        const string json = """
        {
            "tag_name": "v0.1.0",
            "name": "",
            "body": "",
            "assets": []
        }
        """;

        var release = JsonSerializer.Deserialize(json, TwigJsonContext.Default.GitHubRelease);

        release.ShouldNotBeNull();
        release.TagName.ShouldBe("v0.1.0");
        release.Assets.ShouldBeEmpty();
    }

    [Fact]
    public void GitHubRelease_MissingOptionalFields_DefaultsToEmpty()
    {
        // GitHub API always returns these fields, but test defensive defaults
        const string json = """{ "tag_name": "v2.0.0" }""";

        var release = JsonSerializer.Deserialize(json, TwigJsonContext.Default.GitHubRelease);

        release.ShouldNotBeNull();
        release.TagName.ShouldBe("v2.0.0");
        release.Name.ShouldBe("");
        release.Body.ShouldBe("");
        release.Assets.ShouldBeEmpty();
    }

    // ── HTTP integration (with fake handler) ────────────────────────

    [Fact]
    public async Task GetLatestReleaseAsync_Success_MapsToDomainRecord()
    {
        const string json = """
        {
            "tag_name": "v1.3.0",
            "name": "Release 1.3.0",
            "body": "Changelog here",
            "assets": [
                {
                    "name": "twig-win-x64.zip",
                    "browser_download_url": "https://example.com/twig-win-x64.zip",
                    "size": 1024
                }
            ]
        }
        """;

        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, json);

        var client = new GitHubReleaseClient(new HttpClient(handler), "PolyphonyRequiem/twig");
        var release = await client.GetLatestReleaseAsync();

        release.ShouldNotBeNull();
        release.Tag.ShouldBe("v1.3.0");
        release.Name.ShouldBe("Release 1.3.0");
        release.Body.ShouldBe("Changelog here");
        release.Assets.Count.ShouldBe(1);
        release.Assets[0].Name.ShouldBe("twig-win-x64.zip");
        release.Assets[0].BrowserDownloadUrl.ShouldBe("https://example.com/twig-win-x64.zip");
        release.Assets[0].Size.ShouldBe(1024);

        // Verify GitHub API headers
        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequestUrl.ShouldContain("/repos/PolyphonyRequiem/twig/releases/latest");
        handler.LastRequest.Headers.UserAgent.ToString().ShouldContain("twig-cli");
    }

    [Fact]
    public async Task GetLatestReleaseAsync_NotFound_ReturnsNull()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "");

        var client = new GitHubReleaseClient(new HttpClient(handler), "PolyphonyRequiem/twig");
        var release = await client.GetLatestReleaseAsync();

        release.ShouldBeNull();
    }

    [Fact]
    public async Task GetReleasesAsync_Success_ReturnsMultiple()
    {
        const string json = """
        [
            {
                "tag_name": "v1.3.0",
                "name": "Release 1.3.0",
                "body": "Notes 1.3",
                "assets": []
            },
            {
                "tag_name": "v1.2.0",
                "name": "Release 1.2.0",
                "body": "Notes 1.2",
                "assets": []
            }
        ]
        """;

        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, json);

        var client = new GitHubReleaseClient(new HttpClient(handler), "PolyphonyRequiem/twig");
        var releases = await client.GetReleasesAsync(5);

        releases.Count.ShouldBe(2);
        releases[0].Tag.ShouldBe("v1.3.0");
        releases[1].Tag.ShouldBe("v1.2.0");

        handler.LastRequestUrl.ShouldContain("per_page=5");
    }

    // ── Test helpers ──────────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastRequestUrl => LastRequest?.RequestUri?.ToString() ?? string.Empty;

        public void Enqueue(HttpStatusCode status, string body) =>
            _responses.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                LastRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (!_responses.TryDequeue(out var queued))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            return new HttpResponseMessage(queued.Status)
            {
                Content = new StringContent(queued.Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
