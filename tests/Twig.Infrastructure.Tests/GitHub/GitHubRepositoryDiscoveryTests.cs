using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubRepositoryDiscoveryTests
{
    private static readonly Uri Endpoint = new("https://api.github.com/graphql");

    [Fact]
    public async Task Discovery_ReportsOpaqueIdentityWithoutWritingBindingOrPointer()
    {
        using var fixture = new GitHubTargetFixture();
        var tokens = new GitHubReadOnlyAuthHandlerTests.FakeTokenSource();
        using var network = new DiscoveryTransport();
        using var http = GitHubReadOnlyAuthHandler.CreateClient(tokens, Endpoint, network);
        var discovery = new GitHubRepositoryDiscovery(http, new("github.com", "old/repo", Endpoint));

        var observation = await discovery.DiscoverAsync();

        observation.Target.ShouldBe(GitHubTargetFixture.Target);
        observation.Locator.ShouldBe("renamed/repo");
        observation.ResourceScope.ShouldBe("https://api.github.com");
        tokens.Calls.ShouldBe(1);
        network.Requests.ShouldBe(1);
        var row = await fixture.Registry.FindByIdentityAsync(observation.Target.ToIdentity(Endpoint));
        row.IsSuccess.ShouldBeTrue(row.Error);
        row.Value.ShouldBeNull();
        (await fixture.Attachment.ReadWithRevisionAsync()).Value.Revision.ShouldBe(0);
        File.Exists(Path.Combine(fixture.Paths.TwigDir, "provider-target.json")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"data":{"repository":null}}""")]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"data":{"repository":{"id":"R_repo","owner":{"id":"O_owner"},"nameWithOwner":"o/r"}},"errors":[{"message":"private information"}]}""")]
    [InlineData("""{"data":{"repository":{"id":"R_repo","owner":null,"nameWithOwner":"o/r"}}}""")]
    [InlineData("""{"data":{"repository":{"id":"R_repo","owner":{"id":7},"nameWithOwner":"o/r"}}}""")]
    [InlineData("""{"data":{"repository":{"id":"R_repo","owner":{"id":"O_owner"},"nameWithOwner":"malformed"}}}""")]
    public async Task UnavailableOrIncompleteDiscovery_DoesNotClaimAbsence(string json)
    {
        using var network = new DiscoveryTransport { Body = json };
        using var http = GitHubReadOnlyAuthHandler.CreateClient(
            new GitHubReadOnlyAuthHandlerTests.FakeTokenSource(), Endpoint, network);
        var discovery = new GitHubRepositoryDiscovery(http, new("github.com", "o/r", Endpoint));
        var error = await Should.ThrowAsync<JsonException>(() => discovery.DiscoverAsync());
        error.Message.ShouldNotContain("private information");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task HttpFailure_IsAnExplicitFailureNotAnEmptyObservation(int status)
    {
        using var network = new DiscoveryTransport { Status = (HttpStatusCode)status };
        using var http = GitHubReadOnlyAuthHandler.CreateClient(
            new GitHubReadOnlyAuthHandlerTests.FakeTokenSource(), Endpoint, network);
        await Should.ThrowAsync<HttpRequestException>(() =>
            new GitHubRepositoryDiscovery(http, new("github.com", "o/r", Endpoint)).DiscoverAsync());
    }

    [Fact]
    public void Descriptor_RoundTripsOpaqueComponentsWithoutDelimiterCollisions()
    {
        var first = new GitHubRepositoryTarget("O:a", "b:R");
        var second = new GitHubRepositoryTarget("O", "a:b:R");
        var identity = first.ToIdentity(Endpoint);
        identity.DescriptorValue.ShouldNotBe(second.ToIdentity(Endpoint).DescriptorValue);
        GitHubRepositoryTarget.FromIdentity(identity, Endpoint).ShouldBe(first);
        first.ToIdentity(Endpoint).ShouldNotBe(new GitHubRepositoryTarget("o:a", "b:R").ToIdentity(Endpoint));
    }

    [Theory]
    [InlineData("ado", "https://api.github.com", "github.repository-owner/v1", "[\"O\",\"R\"]")]
    [InlineData("github", "https://other.example", "github.repository-owner/v1", "[\"O\",\"R\"]")]
    [InlineData("github", "https://api.github.com", "unknown/v1", "[\"O\",\"R\"]")]
    [InlineData("github", "https://api.github.com", "github.repository-owner/v1", "[ \"O\", \"R\" ]")]
    [InlineData("github", "https://api.github.com", "github.repository-owner/v1", "[\"O\"]")]
    public void Descriptor_ForeignOrNoncanonicalIdentityIsRefused(
        string kind, string scope, string descriptor, string value)
        => Should.Throw<InvalidDataException>(() => GitHubRepositoryTarget.FromIdentity(
            new(kind, scope, descriptor, value), Endpoint));

    internal sealed class DiscoveryTransport : HttpMessageHandler
    {
        public string Body { get; init; } =
            """{"data":{"repository":{"id":"R_repo","nameWithOwner":"renamed/repo","owner":{"id":"O_owner","login":"renamed"}}}}""";
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            json.RootElement.GetProperty("query").GetString().ShouldBe(GitHubIssueQueries.RepositoryDiscovery);
            json.RootElement.GetProperty("variables").EnumerateObject().Select(p => p.Name)
                .ShouldBe(["owner", "name"]);
            request.Headers.Authorization.ShouldNotBeNull();
            return new(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }
}
