using System.Net;
using System.Text;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoRestClient.QueryByWiqlAsync(string, int, CancellationToken)"/>
/// overload that appends the $top query parameter to the WIQL REST API URL.
/// </summary>
public sealed class AdoRestClientWiqlTopTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "testproject";

    [Theory]
    [InlineData(10)]
    [InlineData(1)]
    public async Task QueryByWiqlAsync_WithTop_AppendsTopToUrl(int top)
    {
        var handler = new WiqlTrackingHandler(new List<int> { 1, 2, 3 });
        IAdoWorkItemService client = CreateClient(handler);

        await client.QueryByWiqlAsync("SELECT [System.Id] FROM WorkItems", top: top);

        handler.LastWiqlUrl.ShouldNotBeNull();
        handler.LastWiqlUrl.ShouldContain($"$top={top}");
    }

    [Fact]
    public async Task QueryByWiqlAsync_WithTop_ReturnsCorrectIds()
    {
        var expectedIds = new List<int> { 5, 10, 15 };
        var handler = new WiqlTrackingHandler(expectedIds);
        IAdoWorkItemService client = CreateClient(handler);

        var result = await client.QueryByWiqlAsync("SELECT [System.Id] FROM WorkItems", top: 50);

        result.ShouldBe(expectedIds);
    }

    [Fact]
    public async Task QueryByWiqlAsync_WithTop_EmptyResult_ReturnsEmptyList()
    {
        var handler = new WiqlTrackingHandler(new List<int>());
        IAdoWorkItemService client = CreateClient(handler);

        var result = await client.QueryByWiqlAsync("SELECT [System.Id] FROM WorkItems", top: 5);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryByWiqlAsync_WithoutTop_DoesNotAppendTopToUrl()
    {
        var handler = new WiqlTrackingHandler(new List<int> { 1 });
        IAdoWorkItemService client = CreateClient(handler);

        await client.QueryByWiqlAsync("SELECT [System.Id] FROM WorkItems");

        handler.LastWiqlUrl.ShouldNotBeNull();
        handler.LastWiqlUrl.ShouldNotContain("$top");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoRestClient CreateClient(WiqlTrackingHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var auth = new FakeAuthProvider();
        return new AdoRestClient(http, auth, OrgUrl, Project);
    }

    private sealed class FakeAuthProvider : IAuthenticationProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult("fake-bearer-token");
    }

    /// <summary>
    /// HttpMessageHandler that captures WIQL request URLs and returns canned responses.
    /// </summary>
    private sealed class WiqlTrackingHandler : HttpMessageHandler
    {
        private readonly string _wiqlJson;
        public string? LastWiqlUrl { get; private set; }

        public WiqlTrackingHandler(List<int> ids)
        {
            var items = ids.Select(id => $"{{\"id\":{id},\"url\":\"\"}}");
            _wiqlJson = $"{{\"queryType\":\"flat\",\"workItems\":[{string.Join(',', items)}]}}";
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/_apis/wit/wiql"))
            {
                LastWiqlUrl = url;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_wiqlJson, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
