using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubAuthenticatedActorReaderTests
{
    private static readonly Uri Endpoint = new("https://api.github.com/graphql");
    private const string ActorBody = """{"data":{"viewer":{"id":"U:opaque/actor","login":"observed_user"}}}""";

    [Theory]
    [InlineData("https://api.github.com/graphql", "https://api.github.com")]
    [InlineData("https://ghe.example/api/graphql", "https://ghe.example")]
    public async Task ExplicitObservation_ReportsStableActorAndApiOriginWithoutBinding(string endpoint, string origin)
    {
        var tokens = new GitHubReadOnlyAuthHandlerTests.FakeTokenSource();
        using var network = new Transport();
        using var reader = Reader(tokens, network, new(endpoint));
        tokens.Calls.ShouldBe(0);
        network.Calls.ShouldBe(0);

        var observation = await reader.ObserveAsync();

        observation.ActorId.ShouldBe("U:opaque/actor");
        observation.Login.ShouldBe("observed_user");
        observation.ResourceScope.ShouldBe(origin);
        observation.Source.ShouldBe("github.graphql.viewer");
        tokens.Calls.ShouldBe(1);
        network.Calls.ShouldBe(1);
        network.Authorization.ShouldBe("Bearer gho_SECRETtoken123");
        network.Endpoint.ShouldBe(new Uri(endpoint));
    }

    [Fact]
    public async Task LoginChange_DoesNotChangeStableIdentity_AndActorChangeIsNotHidden()
    {
        using var network = new Transport();
        using var reader = Reader(new GitHubReadOnlyAuthHandlerTests.FakeTokenSource(), network);
        var first = await reader.ObserveAsync();
        network.Body = """{"data":{"viewer":{"id":"U:opaque/actor","login":"renamed"}}}""";
        var renamed = await reader.ObserveAsync();
        renamed.ActorId.ShouldBe(first.ActorId);
        renamed.Login.ShouldNotBe(first.Login);
        network.Body = """{"data":{"viewer":{"id":"U_other","login":"renamed"}}}""";
        (await reader.ObserveAsync()).ActorId.ShouldNotBe(first.ActorId);
        network.Calls.ShouldBe(3);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"data":{"viewer":null}}""")]
    [InlineData("""{"data":{"viewer":[]}}""")]
    [InlineData("""{"data":{"viewer":{"login":"user"}}}""")]
    [InlineData("""{"data":{"viewer":{"id":null,"login":"user"}}}""")]
    [InlineData("""{"data":{"viewer":{"id":7,"login":"user"}}}""")]
    [InlineData("""{"data":{"viewer":{"id":"","login":"user"}}}""")]
    [InlineData("""{"data":{"viewer":{"id":" U_actor","login":"user"}}}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor\n","login":"user"}}}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor"}}}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":false}}}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":" "}}}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":" user"}}}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","id":"U_other","login":"user"}}}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":"user"}},"errors":[{"message":"private server detail"}]}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":"user"}},"errors":null}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":"user"}},"errors":{}}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":"user"}},"data":{"viewer":{"id":"U_other","login":"other"}}}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_other","login":"other"}},"data":{"viewer":{"id":"U_actor","login":"user"}}}""")]
    [InlineData("""{"data":null,"data":{"viewer":{"id":"U_actor","login":"user"}}}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":"user"}},"data":null}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":"user"}},"errors":[{"message":"private server detail"}],"errors":[]}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":"user"}},"errors":[],"errors":[{"message":"private server detail"}]}""")]
    [InlineData("""{"data":{"viewer":{"id":"U_actor","login":"user"}},"errors":[],"errors":[]}""")]
    public async Task IncompleteOrMalformedEvidence_IsAnExplicitFailure(string body)
    {
        using var network = new Transport { Body = body };
        using var reader = Reader(new GitHubReadOnlyAuthHandlerTests.FakeTokenSource(), network);
        var error = await Should.ThrowAsync<JsonException>(() => reader.ObserveAsync());
        error.ToString().ShouldNotContain("private server detail");
        error.ToString().ShouldNotContain("gho_SECRETtoken123");
        network.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task EmptyErrors_IsAccepted()
    {
        using var network = new Transport
        {
            Body = """{"data":{"viewer":{"id":"U_actor","login":"user_name"}},"errors":[]}""",
        };
        using var reader = Reader(new GitHubReadOnlyAuthHandlerTests.FakeTokenSource(), network);
        (await reader.ObserveAsync()).ActorId.ShouldBe("U_actor");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task HttpFailure_DoesNotReturnAnObservation(int status)
    {
        using var network = new Transport { Status = (HttpStatusCode)status };
        using var reader = Reader(new GitHubReadOnlyAuthHandlerTests.FakeTokenSource(), network);
        var error = await Should.ThrowAsync<HttpRequestException>(() => reader.ObserveAsync());
        error.StatusCode.ShouldBe((HttpStatusCode)status);
    }

    [Fact]
    public async Task CancellationBeforeObservation_DoesNotAcquireCredentialOrSend()
    {
        var tokens = new GitHubReadOnlyAuthHandlerTests.FakeTokenSource();
        using var network = new Transport();
        using var reader = Reader(tokens, network);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => reader.ObserveAsync(cancellation.Token));
        tokens.Calls.ShouldBe(0);
        network.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task UnavailableCredential_DoesNotSendOrReturnAnObservation()
    {
        var runner = new GhCliTokenSourceTests.FakeRunner(new(BoundedProcessOutcome.NotStarted, -1, ""));
        using var network = new Transport();
        using var reader = Reader(new GhCliTokenSource(runner, "github.com", "selected-account"), network);
        await Should.ThrowAsync<GitHubCredentialUnavailableException>(() => reader.ObserveAsync());
        network.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Composition_IsInertAndSharesSelectedCredentialWithExistingRepositoryRead()
    {
        var config = new TwigConfiguration
        {
            Provider = "github", GitHub = new() { Host = "github.com", Repository = "owner/repo" },
            GitHubAccount = "selected-account",
        };
        var runner = new GhCliTokenSourceTests.FakeRunner(new(BoundedProcessOutcome.Exited, 0, "fixture-credential"));
        var transports = new List<Transport>();
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton<IBoundedProcessRunner>(runner);
        services.AddSingleton(new GitHubTransportOverride(() =>
        {
            var transport = new Transport();
            transports.Add(transport);
            return transport;
        }));
        services.AddTwigNetworkServices(config);
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<GitHubAuthenticatedActorReader>();
        var repository = provider.GetRequiredService<GitHubRepositoryDiscovery>();
        runner.Calls.ShouldBeEmpty();
        transports.Sum(t => t.Calls).ShouldBe(0);

        var observation = await reader.ObserveAsync();
        var target = await repository.DiscoverAsync();

        observation.ActorId.ShouldBe("U:opaque/actor");
        observation.Login.ShouldNotBe(config.GitHubAccount);
        target.ResourceScope.ShouldBe(observation.ResourceScope);
        runner.Calls.Count.ShouldBe(1);
        runner.Calls[0].Arguments.ShouldBe(["auth", "token", "--hostname", "github.com", "--user", "selected-account"]);
        transports.Count.ShouldBe(2);
        transports.ShouldAllBe(t => t.Calls == 1 && t.Authorization == "Bearer fixture-credential");
        foreach (var transport in transports)
            transport.Dispose();
    }

    [Fact]
    public void Disposal_ReleasesOwnedTransport()
    {
        using var network = new Transport();
        var reader = Reader(new GitHubReadOnlyAuthHandlerTests.FakeTokenSource(), network);
        reader.Dispose();
        network.Disposed.ShouldBeTrue();
    }

    private static GitHubAuthenticatedActorReader Reader(
        IGitHubTokenSource tokens, Transport network, Uri? endpoint = null)
    {
        endpoint ??= Endpoint;
        return new(GitHubReadOnlyAuthHandler.CreateClient(tokens, endpoint, network),
            new(endpoint.Host, "owner/repo", endpoint));
    }

    private sealed class Transport : HttpMessageHandler
    {
        public string Body { get; set; } = ActorBody;
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }
        public Uri? Endpoint { get; private set; }
        public bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            Endpoint = request.RequestUri;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var query = json.RootElement.GetProperty("query").GetString();
            string body;
            if (query == "query { viewer { id login } }")
            {
                json.RootElement.GetProperty("variables").EnumerateObject().ShouldBeEmpty();
                body = Body;
            }
            else
            {
                query.ShouldBe(GitHubIssueQueries.RepositoryDiscovery);
                body = """{"data":{"repository":{"id":"R_repo","nameWithOwner":"owner/repo","owner":{"id":"O_owner"}}}}""";
            }
            return new(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
