using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoGitClient"/>. Uses a fake HttpMessageHandler
/// to verify URL construction, auth, and response mapping.
/// </summary>
public class AdoGitClientTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string GitProject = "GitProject";
    private const string Repository = "my-repo";

    // ── GetPullRequestsForBranchAsync ─────────────────────────────────

    [Fact]
    public async Task GetPullRequestsForBranch_ReturnsMatchingPRs()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, """
        {
            "value": [
                {
                    "pullRequestId": 42,
                    "title": "Test PR",
                    "status": "active",
                    "sourceRefName": "refs/heads/feature/123-test",
                    "targetRefName": "refs/heads/main",
                    "url": "https://dev.azure.com/testorg/GitProject/_apis/git/repositories/my-repo/pullRequests/42"
                }
            ],
            "count": 1
        }
        """);

        var client = CreateClient(handler);
        var prs = await client.GetPullRequestsForBranchAsync("feature/123-test");

        prs.Count.ShouldBe(1);
        prs[0].PullRequestId.ShouldBe(42);
        prs[0].Title.ShouldBe("Test PR");
        prs[0].Status.ShouldBe("active");

        // Verify URL uses git project, not backlog project
        handler.LastRequestUrl.ShouldContain("/GitProject/");
        handler.LastRequestUrl.ShouldContain("/repositories/my-repo/");
        handler.LastRequestUrl.ShouldContain("sourceRefName=");
    }

    [Fact]
    public async Task GetPullRequestsForBranch_EmptyResult_ReturnsEmptyList()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[],"count":0}""");

        var client = CreateClient(handler);
        var prs = await client.GetPullRequestsForBranchAsync("no-prs-branch");

        prs.Count.ShouldBe(0);
    }

    // ── CreatePullRequestAsync ────────────────────────────────────────

    [Fact]
    public async Task CreatePullRequest_ReturnsCreatedPR()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, """
        {
            "pullRequestId": 99,
            "title": "New PR",
            "status": "active",
            "sourceRefName": "refs/heads/feature/456-work",
            "targetRefName": "refs/heads/main",
            "url": "https://dev.azure.com/testorg/GitProject/_apis/git/repositories/my-repo/pullRequests/99"
        }
        """);

        var client = CreateClient(handler);
        var result = await client.CreatePullRequestAsync(new PullRequestCreate(
            "refs/heads/feature/456-work",
            "refs/heads/main",
            "New PR",
            "Description",
            456));

        result.PullRequestId.ShouldBe(99);
        result.Title.ShouldBe("New PR");

        // Verify URL uses git project
        handler.LastRequestUrl.ShouldContain("/GitProject/");
        handler.LastRequestUrl.ShouldContain("/repositories/my-repo/");
        handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
    }

    // ── GetRepositoryIdAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetRepositoryId_ReturnsId()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, """
        {
            "id": "abc-123-def",
            "name": "my-repo",
            "url": "https://dev.azure.com/testorg/GitProject/_apis/git/repositories/my-repo"
        }
        """);

        var client = CreateClient(handler);
        var repoId = await client.GetRepositoryIdAsync();

        repoId.ShouldBe("abc-123-def");
        handler.LastRequestUrl.ShouldContain("/GitProject/");
    }

    // ── GetProjectIdAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetProjectId_ReturnsId()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"id":"proj-id-123","name":"GitProject"}""");

        var client = CreateClient(handler);
        var projectId = await client.GetProjectIdAsync();

        projectId.ShouldBe("proj-id-123");
    }

    // ── Auth header ───────────────────────────────────────────────────

    [Fact]
    public async Task Requests_IncludeAuthHeader()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[],"count":0}""");

        var client = CreateClient(handler);
        await client.GetPullRequestsForBranchAsync("any-branch");

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Headers.Authorization.ShouldNotBeNull();
        handler.LastRequest.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.ShouldBe("fake-bearer-token");
    }

    // ── Error handling ────────────────────────────────────────────────

    [Fact]
    public async Task NotFound_ThrowsAdoNotFoundException()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"message":"Not found"}""");

        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoNotFoundException>(
            () => client.GetRepositoryIdAsync());
    }

    [Fact]
    public async Task Unauthorized_ThrowsAdoAuthenticationException()
    {
        var handler = new FakeHandler();
        // Two 401s: initial attempt + retry (GET requests retry once after token invalidation)
        handler.Enqueue(HttpStatusCode.Unauthorized, """{"message":"Auth failed"}""");
        handler.Enqueue(HttpStatusCode.Unauthorized, """{"message":"Auth failed"}""");

        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoAuthenticationException>(
            () => client.GetRepositoryIdAsync());
    }

    [Fact]
    public void Constructor_ThrowsIfOrgUrlMissing()
    {
        Should.Throw<InvalidOperationException>(
            () => new AdoGitClient(new HttpClient(), new FakeAuthProvider(), "", GitProject, Repository));
    }

    [Fact]
    public void Constructor_ThrowsIfProjectMissing()
    {
        Should.Throw<InvalidOperationException>(
            () => new AdoGitClient(new HttpClient(), new FakeAuthProvider(), OrgUrl, "", Repository));
    }

    [Fact]
    public void Constructor_ThrowsIfRepositoryMissing()
    {
        Should.Throw<InvalidOperationException>(
            () => new AdoGitClient(new HttpClient(), new FakeAuthProvider(), OrgUrl, GitProject, ""));
    }

    // ── URL construction with different project ───────────────────────

    [Fact]
    public async Task UrlConstruction_UsesGitProjectNotBacklogProject()
    {
        // Verify the client uses the git project in URLs, which may differ from backlog project
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"value":[],"count":0}""");

        var client = new AdoGitClient(
            new HttpClient(handler),
            new FakeAuthProvider(),
            OrgUrl,
            "CrossProjectGit",  // git project
            "some-repo");

        await client.GetPullRequestsForBranchAsync("main");

        handler.LastRequestUrl.ShouldContain("/CrossProjectGit/");
    }

    // ── AddArtifactLinkAsync ──────────────────────────────────────────

    [Fact]
    public async Task AddArtifactLink_UsesBacklogProjectNotGitProject()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");

        var client = new AdoGitClient(
            new HttpClient(handler),
            new FakeAuthProvider(),
            OrgUrl,
            "GitProject",       // git project
            "some-repo",
            "BacklogProject");  // backlog project

        await client.AddArtifactLinkAsync(
            workItemId: 42,
            artifactUri: "vstfs:///Git/PullRequestId/abc",
            linkType: "ArtifactLink",
            revision: 5);

        // URL should target the backlog project, not the git project
        handler.LastRequestUrl.ShouldContain("/BacklogProject/");
        handler.LastRequestUrl.ShouldNotContain("/GitProject/");
        handler.LastRequestUrl.ShouldContain("/_apis/wit/workitems/42");
        handler.LastRequestMethod.ShouldBe(HttpMethod.Patch);

        // Verify If-Match header for optimistic concurrency
        handler.LastRequest!.Headers.TryGetValues("If-Match", out var ifMatchValues).ShouldBeTrue();
        ifMatchValues!.First().ShouldBe("5");

        // Verify content type
        handler.LastRequestContentType.ShouldBe("application/json-patch+json");
    }

    [Fact]
    public async Task AddArtifactLink_DefaultsBacklogProjectToGitProject_WhenNotSpecified()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");

        // No backlogProject parameter — should default to git project
        var client = new AdoGitClient(
            new HttpClient(handler),
            new FakeAuthProvider(),
            OrgUrl,
            "SharedProject",
            "some-repo");

        await client.AddArtifactLinkAsync(42, "vstfs:///Git/PullRequestId/abc", "ArtifactLink", 1);

        handler.LastRequestUrl.ShouldContain("/SharedProject/");
    }

    [Fact]
    public async Task AddArtifactLink_BacklogProjectEmptyString_FallsBackToGitProject()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");

        // Empty-string backlogProject should fall back to git project (not produce malformed URL)
        var client = new AdoGitClient(
            new HttpClient(handler),
            new FakeAuthProvider(),
            OrgUrl,
            "GitProject",
            "some-repo",
            "");

        await client.AddArtifactLinkAsync(42, "vstfs:///Git/PullRequestId/abc", "ArtifactLink", 1);

        handler.LastRequestUrl.ShouldContain("/GitProject/");
        handler.LastRequestUrl.ShouldNotContain("//_apis");
    }

    [Fact]
    public async Task AddArtifactLink_SerializesCorrectJsonBody()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");

        var client = CreateClient(handler);
        await client.AddArtifactLinkAsync(
            workItemId: 99,
            artifactUri: "vstfs:///Git/PullRequestId/project-id%2Frepo-id%2F42",
            linkType: "ArtifactLink",
            revision: 3);

        // Verify the JSON body has the correct patch document structure
        handler.LastRequestBody.ShouldNotBeNull();
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;

        root.GetArrayLength().ShouldBe(1);
        var patch = root[0];
        patch.GetProperty("op").GetString().ShouldBe("add");
        patch.GetProperty("path").GetString().ShouldBe("/relations/-");

        var value = patch.GetProperty("value");
        value.GetProperty("rel").GetString().ShouldBe("ArtifactLink");
        value.GetProperty("url").GetString().ShouldBe("vstfs:///Git/PullRequestId/project-id%2Frepo-id%2F42");
        value.GetProperty("attributes").GetProperty("name").GetString().ShouldBe("Pull Request");
    }

    [Fact]
    public async Task AddArtifactLink_NotFound_ThrowsAdoNotFoundException()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"message":"Not found"}""");

        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoNotFoundException>(
            () => client.AddArtifactLinkAsync(999, "vstfs:///Git/PullRequestId/abc", "ArtifactLink", 1));
    }

    [Fact]
    public async Task AddArtifactLink_Unauthorized_ThrowsAdoAuthenticationException()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, """{"message":"Auth failed"}""");

        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoAuthenticationException>(
            () => client.AddArtifactLinkAsync(42, "vstfs:///Git/PullRequestId/abc", "ArtifactLink", 1));
    }

    // ── AdoOfflineException ───────────────────────────────────────────

    [Fact]
    public async Task HttpRequestException_ThrowsAdoOfflineException()
    {
        var handler = new ThrowingHandler(new HttpRequestException("Network unreachable"));

        var client = new AdoGitClient(
            new HttpClient(handler),
            new FakeAuthProvider(),
            OrgUrl,
            GitProject,
            Repository);

        await Should.ThrowAsync<AdoOfflineException>(
            () => client.GetPullRequestsForBranchAsync("any-branch"));
    }

    [Fact]
    public async Task TaskCanceledException_NonCancellation_ThrowsAdoOfflineException()
    {
        var handler = new ThrowingHandler(new TaskCanceledException("Timeout"));

        var client = new AdoGitClient(
            new HttpClient(handler),
            new FakeAuthProvider(),
            OrgUrl,
            GitProject,
            Repository);

        await Should.ThrowAsync<AdoOfflineException>(
            () => client.GetPullRequestsForBranchAsync("any-branch"));
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoGitClient CreateClient(FakeHandler handler) =>
        new(new HttpClient(handler), new FakeAuthProvider(), OrgUrl, GitProject, Repository);

    private sealed class FakeAuthProvider : IAuthenticationProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult("fake-bearer-token");

        public void InvalidateToken() { }
    }

    /// <summary>
    /// HttpMessageHandler that returns pre-queued responses and records request details.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastRequestUrl => LastRequest?.RequestUri?.ToString() ?? string.Empty;
        public HttpMethod LastRequestMethod => LastRequest?.Method ?? HttpMethod.Get;
        public string? LastRequestContentType { get; private set; }
        public string? LastRequestBody { get; private set; }

        public void Enqueue(HttpStatusCode status, string body) =>
            _responses.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Clone headers before the request is disposed
            LastRequest = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                LastRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            LastRequestContentType = request.Content?.Headers.ContentType?.MediaType;
            LastRequestBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;

            if (!_responses.TryDequeue(out var queued))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            return new HttpResponseMessage(queued.Status)
            {
                Content = new StringContent(queued.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// HttpMessageHandler that throws a specified exception on SendAsync.
    /// </summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }
}
