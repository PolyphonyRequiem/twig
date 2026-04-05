using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoRestClient.AddLinkAsync"/>.
/// Uses fake HttpMessageHandlers to verify the outbound HTTP requests.
/// RemoveLinkAsync tests are in <see cref="AdoRestClientRemoveLinkTests"/>.
/// </summary>
public sealed class AdoRestClientLinkTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "testproject";

    [Fact]
    public async Task AddLinkAsync_SendsPatchWithRelationsOp()
    {
        var handler = new LinkTrackingHandler();
        var client = CreateClient(handler);

        await client.AddLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        handler.RequestCount.ShouldBe(1);
        handler.LastMethod.ShouldBe("PATCH");
    }

    [Fact]
    public async Task AddLinkAsync_UrlContainsSourceId()
    {
        var handler = new LinkTrackingHandler();
        var client = CreateClient(handler);

        await client.AddLinkAsync(sourceId: 42, targetId: 99, adoLinkType: "System.LinkTypes.Related");

        handler.LastUrl!.ShouldContain("/workitems/42");
    }

    [Fact]
    public async Task AddLinkAsync_BodyContainsAddRelationsOp()
    {
        var handler = new LinkTrackingHandler();
        var client = CreateClient(handler);

        await client.AddLinkAsync(sourceId: 1, targetId: 2, adoLinkType: "System.LinkTypes.Dependency-Forward");

        var body = handler.LastRequestBody;
        body.ShouldNotBeNull();

        using var doc = JsonDocument.Parse(body);
        var ops = doc.RootElement;
        ops.GetArrayLength().ShouldBe(1);

        var op = ops[0];
        op.GetProperty("op").GetString().ShouldBe("add");
        op.GetProperty("path").GetString().ShouldBe("/relations/-");

        var value = op.GetProperty("value");
        value.GetProperty("rel").GetString().ShouldBe("System.LinkTypes.Dependency-Forward");
        value.GetProperty("url").GetString().ShouldBe($"{OrgUrl}/_apis/wit/workitems/2");
    }

    [Fact]
    public async Task AddLinkAsync_TargetUrlUsesOrgUrlNotProjectUrl()
    {
        var handler = new LinkTrackingHandler();
        var client = CreateClient(handler);

        await client.AddLinkAsync(sourceId: 10, targetId: 20, adoLinkType: "System.LinkTypes.Hierarchy-Forward");

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        var targetUrl = doc.RootElement[0].GetProperty("value").GetProperty("url").GetString();
        targetUrl.ShouldBe($"{OrgUrl}/_apis/wit/workitems/20");
    }

    [Fact]
    public async Task AddLinkAsync_UsesJsonPatchContentType()
    {
        var handler = new LinkTrackingHandler();
        var client = CreateClient(handler);

        await client.AddLinkAsync(sourceId: 1, targetId: 2, adoLinkType: "System.LinkTypes.Related");

        handler.LastContentType!.ShouldContain("application/json-patch+json");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoRestClient CreateClient(HttpMessageHandler handler)
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
    /// HttpMessageHandler that captures details of outbound requests and returns 200 OK
    /// with a minimal work item JSON response (PATCH returns the updated work item).
    /// </summary>
    private sealed class LinkTrackingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastUrl { get; private set; }
        public string? LastMethod { get; private set; }
        public string? LastRequestBody { get; private set; }
        public string? LastContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastUrl = request.RequestUri?.ToString();
            LastMethod = request.Method.Method;
            LastContentType = request.Content?.Headers.ContentType?.ToString();

            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            // PATCH returns the updated work item
            var responseJson = """{"id":1,"rev":2,"fields":{"System.WorkItemType":"Task","System.Title":"Test","System.State":"New"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
