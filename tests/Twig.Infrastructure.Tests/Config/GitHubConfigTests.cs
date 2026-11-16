using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

public sealed class GitHubConfigTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("github.com", null)]
    [InlineData(null, "o/r")]
    [InlineData("GitHub.com", "o/r")]
    [InlineData("not a host", "o/r")]
    [InlineData("github.com", "o")]
    public void MissingOrMalformedScopeFailsClosed(string? host, string? repository)
    {
        GitHubWorkspaceScope.TryCreate(new() { Host = host, Repository = repository }, out var scope, out var problem)
            .ShouldBeFalse();
        scope.ShouldBeNull();
        problem.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("github.com", "https://api.github.com/graphql")]
    [InlineData("ghe.example.com", "https://ghe.example.com/api/graphql")]
    public void EndpointIsDerivedFromExplicitValidHost(string host, string endpoint)
    {
        GitHubWorkspaceScope.TryCreate(new() { Host = host, Repository = "o/r" }, out var scope, out _).ShouldBeTrue();
        scope!.GraphQlEndpoint.ShouldBe(new Uri(endpoint));
        scope.Repository.ShouldBe("o/r");
    }

    [Fact]
    public void MissingConfigFailsClosed()
        => GitHubWorkspaceScope.TryCreate(null, out _, out _).ShouldBeFalse();
}
