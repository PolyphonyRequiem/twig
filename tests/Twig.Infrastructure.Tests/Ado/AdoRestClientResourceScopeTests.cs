using System.Net;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public sealed class AdoRestClientResourceScopeTests
{
    private const string InstanceId = "A1B2C3D4-E5F6-4789-ABCD-0123456789EF";

    [Fact]
    public async Task ResolveResourceScopeAsync_NormalizesAndCachesConnectionInstanceId()
    {
        using var handler = new ConnectionDataHandler($$"""{"instanceId":"{{InstanceId}}"}""");
        using var http = new HttpClient(handler);
        var provider = CreateProvider(http);

        (await provider.ResolveResourceScopeAsync()).ShouldBe(InstanceId.ToLowerInvariant());
        (await provider.ResolveResourceScopeAsync()).ShouldBe(InstanceId.ToLowerInvariant());
        handler.RequestCount.ShouldBe(1);
        handler.LastRequestUri!.AbsolutePath.ShouldBe("/test-org/_apis/connectionData");
        handler.LastRequestUri.Query.ShouldContain("api-version=7.1-preview");
    }

    [Theory]
    [InlineData("""{"authenticatedUser":{}}""")]
    [InlineData("""{"instanceId":null}""")]
    [InlineData("""{"instanceId":""}""")]
    [InlineData("""{"instanceId":"not-a-guid"}""")]
    [InlineData("""{"instanceId":"00000000-0000-0000-0000-000000000000"}""")]
    public async Task ResolveResourceScopeAsync_ReturnsNullWhenInstanceIdIsUnavailable(string json)
    {
        using var handler = new ConnectionDataHandler(json);
        using var http = new HttpClient(handler);
        var provider = CreateProvider(http);

        (await provider.ResolveResourceScopeAsync()).ShouldBeNull();
        handler.RequestCount.ShouldBe(1);
    }

    private static IWorkItemProvider CreateProvider(HttpClient http) =>
        new AdoRestClient(http, new FakeAuthenticationProvider(), "https://dev.azure.com/test-org",
            "test-project", new WorkItemMapper());

    private sealed class FakeAuthenticationProvider : IAuthenticationProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
            Task.FromResult("test-token");

        public void InvalidateToken() { }
    }

    private sealed class ConnectionDataHandler(string json) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
