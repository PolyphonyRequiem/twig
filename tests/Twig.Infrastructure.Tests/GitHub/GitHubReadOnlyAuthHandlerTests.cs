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
    public async Task BootstrapDoesNotAuthorizeRetiredSchemaIntrospection()
    {
        const string retired = """
            query {
              issue: __type(name: "Issue") {
                kind name
                fields(includeDeprecated: true) {
                  name
                  type { kind name ofType { kind name ofType { kind name ofType { kind name } } } }
                }
              }
              issueState: __type(name: "IssueState") {
                kind name
                enumValues(includeDeprecated: true) { name }
              }
            }
            """;
        var h = new Harness();
        await Should.ThrowAsync<GitHubReadOnlyRequestRefusedException>(() =>
            h.SendAsync(HttpMethod.Post, Endpoint.ToString(), Body(retired, new Dictionary<string, string?>())));
        h.Tokens.Calls.ShouldBe(0);
        h.Network.Requests.ShouldBeEmpty();
    }

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

    private static readonly string DiscoveryBody = Body(DiscoveryQuery, new { owner = "owner", name = "repo" });

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
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmittedRead_CarriesBearerToken(bool viewer)
    {
        var body = viewer ? Body(GitHubIssueQueries.AuthenticatedViewer, new Dictionary<string, string?>()) : DiscoveryBody;
        var h = new Harness();

        using var response = await h.SendAsync(HttpMethod.Post, Endpoint.ToString(), body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        h.Network.Requests.Count.ShouldBe(1);
        h.Network.Requests[0].Authorization.ShouldBe("Bearer " + Token);
    }

    public static TheoryData<string, string, string?> RefusedRequests()
    {
        var variables = new { owner = "owner", name = "repo" };
        return new()
        {
            { "GET", Endpoint.ToString(), null },
            { "PUT", Endpoint.ToString(), DiscoveryBody },
            { "DELETE", Endpoint.ToString(), null },
            { "POST", "https://evil.example.com/graphql", DiscoveryBody },
            { "POST", "http://api.github.com/graphql", DiscoveryBody },
            { "POST", "https://api.github.com:8443/graphql", DiscoveryBody },
            { "POST", "https://api.github.com/graphql/extra", DiscoveryBody },
            { "POST", "https://api.github.com/graphql?x=1", DiscoveryBody },
            { "POST", "https://api.github.com/graphql#fragment", DiscoveryBody },
            { "POST", "https://user:pw@api.github.com/graphql", DiscoveryBody },
            { "POST", "https://api.github.com/repos/o/r/issues", DiscoveryBody },
            { "POST", Endpoint.ToString(), null },
            { "POST", Endpoint.ToString(), "not json" },
            { "POST", Endpoint.ToString(), "[]" },
            { "POST", Endpoint.ToString(), Body(DiscoveryQuery + " ", variables) },
            { "POST", Endpoint.ToString(), Body(DiscoveryQuery.Replace("nameWithOwner", "nameWithOwner viewerCanAdminister"), variables) },
            { "POST", Endpoint.ToString(), Body("mutation { x }", variables) },
            { "POST", Endpoint.ToString(), Body(DiscoveryQuery + "\nmutation { x }", variables) },
            { "POST", Endpoint.ToString(), Body(DiscoveryQuery, new Dictionary<string, string>()) },
            { "POST", Endpoint.ToString(), Body(DiscoveryQuery, new { owner = "", name = "repo" }) },
            { "POST", Endpoint.ToString(), Body(DiscoveryQuery, new { owner = 5, name = "repo" }) },
            { "POST", Endpoint.ToString(), Body(DiscoveryQuery, new { owner = "owner", name = "repo", after = "cursor" }) },
            { "POST", Endpoint.ToString(), Body(DiscoveryQuery, new { owner = "owner", name = "repo", input = "x" }) },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(DiscoveryQuery) + ",\"variables\":{\"owner\":\"owner\",\"name\":\"repo\"},\"operationName\":\"x\"}" },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(DiscoveryQuery) + ",\"query\":\"mutation { x }\",\"variables\":{\"owner\":\"owner\",\"name\":\"repo\"}}" },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(DiscoveryQuery) + ",\"variables\":{\"owner\":\"owner\",\"owner\":\"other\",\"name\":\"repo\"}}" },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(DiscoveryQuery) + ",\"variables\":{\"owner\":\"owner\",\"name\":\"repo\"},\"variables\":{\"owner\":\"owner\",\"name\":\"repo\"}}" },
            { "POST", Endpoint.ToString(), "{\"query\":" + JsonSerializer.Serialize(DiscoveryQuery) + "}" },
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
            viewer ? Body(GitHubIssueQueries.AuthenticatedViewer, new Dictionary<string, string?>()) : DiscoveryBody,
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
            () => h.SendAsync(HttpMethod.Post, Endpoint.ToString(), DiscoveryBody));

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
        await h.SendAsync(HttpMethod.Post, Endpoint.ToString(), DiscoveryBody);

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
