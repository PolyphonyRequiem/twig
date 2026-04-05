using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoRestClient.AddLinkAsync"/> and <see cref="AdoRestClient.RemoveLinkAsync"/>.
/// Uses fake HttpMessageHandlers to verify the outbound HTTP requests.
/// </summary>
public class AdoRestClientLinkTests
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

    // ── RemoveLinkAsync ─────────────────────────────────────────────

    [Fact]
    public async Task RemoveLinkAsync_FetchesWorkItemThenPatchesRemove()
    {
        var handler = new RemoveLinkTrackingHandler(
            [("System.LinkTypes.Related", 200)], rev: 5);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        handler.GetRequestCount.ShouldBe(1);
        handler.PatchRequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task RemoveLinkAsync_GetRequestExpandsRelations()
    {
        var handler = new RemoveLinkTrackingHandler(
            [("System.LinkTypes.Related", 200)], rev: 3);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        handler.LastGetUrl.ShouldNotBeNull();
        handler.LastGetUrl.ShouldContain("$expand=relations");
        handler.LastGetUrl.ShouldContain("/workitems/100");
    }

    [Fact]
    public async Task RemoveLinkAsync_PatchBodyContainsRemoveOp()
    {
        var handler = new RemoveLinkTrackingHandler(
            [("System.LinkTypes.Related", 200)], rev: 7);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        var body = handler.LastPatchBody;
        body.ShouldNotBeNull();

        using var doc = JsonDocument.Parse(body);
        var ops = doc.RootElement;
        ops.GetArrayLength().ShouldBe(1);

        var op = ops[0];
        op.GetProperty("op").GetString().ShouldBe("remove");
        op.GetProperty("path").GetString().ShouldBe("/relations/0");
    }

    [Fact]
    public async Task RemoveLinkAsync_PatchIncludesIfMatchHeader()
    {
        var handler = new RemoveLinkTrackingHandler(
            [("System.LinkTypes.Related", 200)], rev: 42);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        handler.LastPatchIfMatch.ShouldBe("42");
    }

    [Fact]
    public async Task RemoveLinkAsync_NoOpWhenNoRelations()
    {
        var handler = new RemoveLinkTrackingHandler([], rev: 1);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        handler.GetRequestCount.ShouldBe(1);
        handler.PatchRequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task RemoveLinkAsync_NoOpWhenRelationTypeDoesNotMatch()
    {
        var handler = new RemoveLinkTrackingHandler(
            [("System.LinkTypes.Hierarchy-Reverse", 200)], rev: 3);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        handler.PatchRequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task RemoveLinkAsync_NoOpWhenTargetIdDoesNotMatch()
    {
        var handler = new RemoveLinkTrackingHandler(
            [("System.LinkTypes.Related", 999)], rev: 3);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        handler.PatchRequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task RemoveLinkAsync_FindsCorrectIndexAmongMultipleRelations()
    {
        var handler = new RemoveLinkTrackingHandler(
            [
                ("System.LinkTypes.Hierarchy-Reverse", 50),
                ("System.LinkTypes.Related", 200),
                ("System.LinkTypes.Related", 300),
            ],
            rev: 10);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        var body = handler.LastPatchBody!;
        using var doc = JsonDocument.Parse(body);
        doc.RootElement[0].GetProperty("path").GetString().ShouldBe("/relations/1");
    }

    [Fact]
    public async Task RemoveLinkAsync_PatchUrlDoesNotExpandRelations()
    {
        var handler = new RemoveLinkTrackingHandler(
            [("System.LinkTypes.Related", 200)], rev: 5);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        handler.LastPatchUrl.ShouldNotBeNull();
        handler.LastPatchUrl.ShouldNotContain("$expand");
    }

    [Fact]
    public async Task RemoveLinkAsync_UsesJsonPatchContentType()
    {
        var handler = new RemoveLinkTrackingHandler(
            [("System.LinkTypes.Related", 200)], rev: 5);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        handler.LastPatchContentType.ShouldNotBeNull();
        handler.LastPatchContentType.ShouldContain("application/json-patch+json");
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

    /// <summary>
    /// HttpMessageHandler for RemoveLinkAsync tests. Responds to the initial GET
    /// (with configurable relations) and captures the subsequent PATCH request details.
    /// </summary>
    private sealed class RemoveLinkTrackingHandler : HttpMessageHandler
    {
        private readonly string _relationsJson;
        private readonly int _rev;

        public int GetRequestCount { get; private set; }
        public int PatchRequestCount { get; private set; }
        public string? LastGetUrl { get; private set; }
        public string? LastPatchUrl { get; private set; }
        public string? LastPatchBody { get; private set; }
        public string? LastPatchIfMatch { get; private set; }
        public string? LastPatchContentType { get; private set; }

        public RemoveLinkTrackingHandler((string RelType, int TargetId)[] relations, int rev)
        {
            _rev = rev;
            if (relations.Length == 0)
            {
                _relationsJson = "null";
            }
            else
            {
                var items = relations.Select(r =>
                    $"{{\"rel\":\"{r.RelType}\",\"url\":\"{OrgUrl}/_apis/wit/workitems/{r.TargetId}\",\"attributes\":{{\"name\":\"Related\"}}}}");
                _relationsJson = $"[{string.Join(',', items)}]";
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                GetRequestCount++;
                LastGetUrl = request.RequestUri?.ToString();

                var responseJson = $"{{\"id\":100,\"rev\":{_rev},\"fields\":{{\"System.WorkItemType\":\"Task\",\"System.Title\":\"Test\",\"System.State\":\"New\"}},\"relations\":{_relationsJson}}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Patch)
            {
                PatchRequestCount++;
                LastPatchUrl = request.RequestUri?.ToString();
                LastPatchContentType = request.Content?.Headers.ContentType?.ToString();

                if (request.Headers.TryGetValues("If-Match", out var values))
                    LastPatchIfMatch = values.FirstOrDefault();

                if (request.Content is not null)
                    LastPatchBody = await request.Content.ReadAsStringAsync(cancellationToken);

                var responseJson = """{"id":100,"rev":6,"fields":{"System.WorkItemType":"Task","System.Title":"Test","System.State":"New"}}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
