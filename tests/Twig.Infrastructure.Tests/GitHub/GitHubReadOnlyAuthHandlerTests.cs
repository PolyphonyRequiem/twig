using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubReadOnlyAuthHandlerTests
{
    private const string Token = "gho_SECRETtoken123";
    private static readonly Uri Endpoint = new("https://api.github.com/graphql");
    private const string DiscoveryQuery = "query($owner: String!, $name: String!) { repository(owner: $owner, name: $name) { id nameWithOwner owner { id login } } }";

    [Fact]
    public async Task FixedAuthenticatedViewer_IsAdmittedWithEmptyVariables()
    {
        var h = new Harness();
        using var response = await h.SendAsync(HttpMethod.Post, Endpoint.ToString(),
            Body("query { viewer { id login } }", new Dictionary<string, string?>()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        h.Tokens.Calls.ShouldBe(1);
        h.Network.Requests.Count.ShouldBe(1);
        h.Network.Requests[0].Authorization.ShouldBe("Bearer " + Token);
    }

    [Fact]
    public async Task FixedSchemaMetadata_IsAdmittedWithEmptyVariables()
    {
        var h = new Harness();
        using var response = await h.SendAsync(HttpMethod.Post, Endpoint.ToString(),
            Body(GitHubIssueQueries.SchemaMetadata, new Dictionary<string, string?>()));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        h.Tokens.Calls.ShouldBe(1);
        h.Network.Requests.Count.ShouldBe(1);
        h.Network.Requests[0].Authorization.ShouldBe("Bearer " + Token);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"id":"U_actor"}""")]
    [InlineData("""{"after":null}""")]
    [InlineData("""{"id":"U_actor","id":"U_other"}""")]
    public async Task AuthenticatedViewer_NonemptyOrInvalidVariablesRefusedBeforeToken(string variables)
    {
        var h = new Harness();
        var body = "{\"query\":" + JsonSerializer.Serialize(GitHubIssueQueries.AuthenticatedViewer)
            + ",\"variables\":" + variables + "}";
        await Should.ThrowAsync<GitHubReadOnlyRequestRefusedException>(() =>
            h.SendAsync(HttpMethod.Post, Endpoint.ToString(), body));
        h.Tokens.Calls.ShouldBe(0);
        h.Network.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("query { viewer { id login } } ")]
    [InlineData("query { viewer { id login name } }")]
    [InlineData("query { viewer { id login } } mutation { x }")]
    [InlineData("query { viewer { login } }")]
    [InlineData("mutation { viewer { id login } }")]
    public async Task ModifiedOrUnknownViewerDocument_RefusedBeforeToken(string query)
    {
        var h = new Harness();
        await Should.ThrowAsync<GitHubReadOnlyRequestRefusedException>(() =>
            h.SendAsync(HttpMethod.Post, Endpoint.ToString(), Body(query, new Dictionary<string, string?>())));
        h.Tokens.Calls.ShouldBe(0);
        h.Network.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task FixedRepositoryDiscovery_IsAdmitted()
    {
        var h = new Harness();
        using var response = await h.SendAsync(HttpMethod.Post, Endpoint.ToString(),
            Body(DiscoveryQuery, new { owner = "owner", name = "repo" }));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        h.Tokens.Calls.ShouldBe(1);
        h.Network.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"owner":"owner"}""")]
    [InlineData("""{"owner":"owner","name":null}""")]
    [InlineData("""{"owner":"owner","name":7}""")]
    [InlineData("""{"owner":"owner","name":"repo","id":"extra"}""")]
    [InlineData("""{"owner":"owner","owner":"other","name":"repo"}""")]
    [InlineData("""{"owner":"owner/other","name":"repo"}""")]
    [InlineData("""{"owner":"owner","name":".."}""")]
    [InlineData("""{"owner":null,"name":"repo"}""")]
    [InlineData("""{"owner":"owner","name":"repo","name":"other"}""")]
    [InlineData("""{"owner":"owner","name":" repo"}""")]
    public async Task InvalidDiscoveryVariables_RefusedBeforeToken(string variables)
    {
        var h = new Harness();
        var body = "{\"query\":" + JsonSerializer.Serialize(DiscoveryQuery) + ",\"variables\":" + variables + "}";
        await Should.ThrowAsync<GitHubReadOnlyRequestRefusedException>(
            () => h.SendAsync(HttpMethod.Post, Endpoint.ToString(), body));
        h.Tokens.Calls.ShouldBe(0);
        h.Network.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(" mutation { viewer { login } }")]
    public async Task ModifiedDiscoveryDocument_IsRefusedBeforeToken(string suffix)
    {
        var h = new Harness();
        await Should.ThrowAsync<GitHubReadOnlyRequestRefusedException>(() => h.SendAsync(
            HttpMethod.Post, Endpoint.ToString(),
            Body(DiscoveryQuery + suffix, new { owner = "owner", name = "repo" })));
        h.Tokens.Calls.ShouldBe(0);
        h.Network.Requests.ShouldBeEmpty();
    }

    private static string Body(string query, object variables) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["query"] = query, ["variables"] = variables });

    private static readonly string SnapshotBody = Body(GitHubIssueQueries.Snapshot, new Dictionary<string, string> { ["id"] = "I_1" });
    private static readonly string ConnectionBody = Body(GitHubIssueQueries.Connection("subIssues"),
        new Dictionary<string, string?> { ["id"] = "I_1", ["after"] = null });

    private sealed class Harness
    {
        public FakeTokenSource Tokens { get; } = new();
        public Inner Network { get; } = new();
        public HttpClient Client { get; }
        public Harness(HttpStatusCode status = HttpStatusCode.OK, Uri? location = null)
        {
            Network.Status = status;
            Network.Location = location;
            Client = GitHubReadOnlyAuthHandler.CreateClient(Tokens, Endpoint, Network);
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, string? body, Action<HttpRequestMessage>? edit = null)
        {
            var request = new HttpRequestMessage(method, uri);
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            edit?.Invoke(request);
            return Client.SendAsync(request);
        }
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("connection")]
    [InlineData("connection-cursor")]
    public async Task AdmittedRead_CarriesBearerToken(string shape)
    {
        var body = shape switch
        {
            "snapshot" => SnapshotBody,
            "connection" => ConnectionBody,
            _ => Body(GitHubIssueQueries.Connection("blockedBy"), new Dictionary<string, string?> { ["id"] = "I_1", ["after"] = "c1" }),
        };
        var h = new Harness();

        using var response = await h.SendAsync(HttpMethod.Post, Endpoint.ToString(), body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        h.Network.Requests.Count.ShouldBe(1);
        h.Network.Requests[0].Authorization.ShouldBe("Bearer " + Token);
    }

    public static TheoryData<string, string, string?> RefusedRequests()
    {
        var snapshot = GitHubIssueQueries.Snapshot;
        var id = new Dictionary<string, string> { ["id"] = "I_1" };
        return new()
        {
            { "GET", Endpoint.ToString(), null },
            { "PUT", Endpoint.ToString(), SnapshotBody },
            { "DELETE", Endpoint.ToString(), null },
            { "POST", "https://evil.example.com/graphql", SnapshotBody },
            { "POST", "http://api.github.com/graphql", SnapshotBody },
            { "POST", "https://api.github.com:8443/graphql", SnapshotBody },
            { "POST", "https://api.github.com/graphql/extra", SnapshotBody },
            { "POST", "https://api.github.com/graphql?x=1", SnapshotBody },
            { "POST", "https://user:pw@api.github.com/graphql", SnapshotBody },
            { "POST", "https://api.github.com/repos/o/r/issues", SnapshotBody },
            { "POST", Endpoint.ToString(), null },
            { "POST", Endpoint.ToString(), "not json" },
            { "POST", Endpoint.ToString(), "[]" },
            { "POST", Endpoint.ToString(), Body(snapshot + " ", id) },
            { "POST", Endpoint.ToString(), Body(snapshot.Replace("title", "title viewerCanUpdate"), id) },
            { "POST", Endpoint.ToString(), Body("mutation($id: ID!) { closeIssue(input: {issueId: $id}) { clientMutationId } }", id) },
            { "POST", Endpoint.ToString(), Body(snapshot + "\nmutation { x }", id) },
            { "POST", Endpoint.ToString(), Body(snapshot, new Dictionary<string, string>()) },
            { "POST", Endpoint.ToString(), Body(snapshot, new Dictionary<string, string> { ["id"] = "" }) },
            { "POST", Endpoint.ToString(), Body(snapshot, new Dictionary<string, object> { ["id"] = 5 }) },
            { "POST", Endpoint.ToString(), Body(snapshot, new Dictionary<string, string?> { ["id"] = "I_1", ["after"] = null }) },
            { "POST", Endpoint.ToString(), Body(snapshot, new Dictionary<string, string> { ["id"] = "I_1", ["input"] = "x" }) },
            { "POST", Endpoint.ToString(), Body(GitHubIssueQueries.Connection("labels"), id) },
            { "POST", Endpoint.ToString(), Body(GitHubIssueQueries.Connection("labels"), new Dictionary<string, object?> { ["id"] = "I_1", ["after"] = 3 }) },
            { "POST", Endpoint.ToString(), Body(GitHubIssueQueries.SchemaMetadata + " ", new Dictionary<string, string?>()) },
            { "POST", Endpoint.ToString(), Body(GitHubIssueQueries.SchemaMetadata, new Dictionary<string, string?> { ["type"] = "Issue" }) },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(snapshot) + ",\"variables\":{\"id\":\"I_1\"},\"operationName\":\"x\"}" },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(snapshot) + ",\"query\":\"mutation { x }\",\"variables\":{\"id\":\"I_1\"}}" },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(snapshot) + ",\"variables\":{\"id\":\"I_1\",\"id\":\"I_2\"}}" },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(snapshot) + "}" },
            { "POST", "https://evil.example.com/graphql", Body(GitHubIssueQueries.AuthenticatedViewer, new Dictionary<string, string?>()) },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(GitHubIssueQueries.AuthenticatedViewer) + "}" },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(GitHubIssueQueries.AuthenticatedViewer) + ",\"variables\":{},\"operationName\":\"x\"}" },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(GitHubIssueQueries.AuthenticatedViewer) + ",\"query\":\"mutation { x }\",\"variables\":{}}" },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(GitHubIssueQueries.AuthenticatedViewer) + ",\"variables\":{},\"variables\":{}}" },
        };
    }

    [Theory]
    [MemberData(nameof(RefusedRequests))]
    public async Task RefusedRequest_NeverAcquiresTokenOrTouchesNetwork(string method, string uri, string? body)
    {
        var h = new Harness();

        await Should.ThrowAsync<GitHubReadOnlyRequestRefusedException>(() => h.SendAsync(new HttpMethod(method), uri, body));

        h.Tokens.Calls.ShouldBe(0);
        h.Network.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreexistingAuthorization_IsRefusedBeforeToken(bool viewer)
    {
        var h = new Harness();

        await Should.ThrowAsync<GitHubReadOnlyRequestRefusedException>(() => h.SendAsync(HttpMethod.Post, Endpoint.ToString(),
            viewer ? Body(GitHubIssueQueries.AuthenticatedViewer, new Dictionary<string, string?>()) : SnapshotBody,
            r => r.Headers.Authorization = new("Bearer", "other")));

        h.Tokens.Calls.ShouldBe(0);
        h.Network.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AuthenticatedViewer_RedirectResponse_IsRefusedAndNotFollowed()
    {
        var h = new Harness(HttpStatusCode.Found, new Uri("https://evil.example.com/collect"));
        await Should.ThrowAsync<GitHubReadOnlyRequestRefusedException>(() =>
            h.SendAsync(HttpMethod.Post, Endpoint.ToString(),
                Body(GitHubIssueQueries.AuthenticatedViewer, new Dictionary<string, string?>())));
        h.Network.Requests.Count.ShouldBe(1);
        h.Network.Requests[0].Uri.ShouldBe(Endpoint);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task RedirectResponse_IsRefusedAndNotFollowed(HttpStatusCode status)
    {
        var h = new Harness(status, new Uri("https://evil.example.com/collect"));

        var ex = await Should.ThrowAsync<GitHubReadOnlyRequestRefusedException>(
            () => h.SendAsync(HttpMethod.Post, Endpoint.ToString(), SnapshotBody));

        h.Network.Requests.Count.ShouldBe(1);
        h.Network.Requests[0].Uri.ShouldBe(Endpoint);
        ex.Message.ShouldNotContain(Token);
    }

    [Fact]
    public void DefaultTransport_DisablesAutomaticRedirects()
    {
        using var client = GitHubReadOnlyAuthHandler.CreateClient(new FakeTokenSource(), Endpoint);
        var field = typeof(HttpMessageInvoker).GetField("_handler",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var auth = field.GetValue(client).ShouldBeOfType<GitHubReadOnlyAuthHandler>();
        auth.InnerHandler.ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeFalse();
    }

    [Fact]
    public async Task RefusalMessages_NeverContainToken()
    {
        var h = new Harness();
        await h.SendAsync(HttpMethod.Post, Endpoint.ToString(), SnapshotBody);

        var ex = await Should.ThrowAsync<GitHubReadOnlyRequestRefusedException>(
            () => h.SendAsync(HttpMethod.Get, Endpoint.ToString(), null));

        ex.ToString().ShouldNotContain(Token);
    }

    internal sealed class FakeTokenSource : IGitHubTokenSource
    {
        public int Calls { get; private set; }
        public ValueTask<string> GetTokenAsync(CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(Token);
        }
    }

    internal sealed class Inner : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Uri? Location { get; set; }
        public List<(Uri? Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri, request.Headers.Authorization?.ToString()));
            var response = new HttpResponseMessage(Status) { Content = new StringContent("{}") };
            if (Location is not null)
                response.Headers.Location = Location;
            return Task.FromResult(response);
        }
    }
}
