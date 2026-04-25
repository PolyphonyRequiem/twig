using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoRestClient.RemoveLinkAsync"/>.
/// Uses fake HttpMessageHandlers to verify outbound HTTP request structure,
/// index calculation, idempotency, If-Match header, and error propagation.
/// </summary>
public sealed class AdoRestClientRemoveLinkTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "testproject";

    // ── Success path ────────────────────────────────────────────────

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

    // ── If-Match header ─────────────────────────────────────────────

    [Fact]
    public async Task RemoveLinkAsync_IfMatchReflectsRevisionFromGetResponse()
    {
        var handler = new RemoveLinkTrackingHandler(
            [("System.LinkTypes.Related", 200)], rev: 999);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        handler.LastPatchIfMatch.ShouldBe("999");
    }

    // ── Index calculation ───────────────────────────────────────────

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
    public async Task RemoveLinkAsync_FindsRelationAtEndOfArray()
    {
        var handler = new RemoveLinkTrackingHandler(
            [
                ("System.LinkTypes.Hierarchy-Reverse", 50),
                ("System.LinkTypes.Hierarchy-Forward", 60),
                ("System.LinkTypes.Related", 200),
            ],
            rev: 4);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        var body = handler.LastPatchBody!;
        using var doc = JsonDocument.Parse(body);
        doc.RootElement[0].GetProperty("path").GetString().ShouldBe("/relations/2");
    }

    // ── Idempotency (graceful no-op) ────────────────────────────────

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

    // ── URL construction / matching ─────────────────────────────────

    [Fact]
    public async Task RemoveLinkAsync_MatchesUrlWithProjectSegment()
    {
        // ADO can return URLs with a project segment: /{org}/{project}/_apis/wit/workItems/{id}
        var handler = new RemoveLinkTrackingHandler(
            [("System.LinkTypes.Related", 200)], rev: 5,
            urlPrefix: $"{OrgUrl}/{Project}/_apis/wit/workItems");
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        handler.PatchRequestCount.ShouldBe(1);
        var body = handler.LastPatchBody!;
        using var doc = JsonDocument.Parse(body);
        doc.RootElement[0].GetProperty("path").GetString().ShouldBe("/relations/0");
    }

    [Fact]
    public async Task RemoveLinkAsync_RelationTypeCaseInsensitiveMatch()
    {
        // Implementation uses StringComparison.OrdinalIgnoreCase for rel type matching
        var handler = new RemoveLinkTrackingHandler(
            [("system.linktypes.related", 200)], rev: 5);
        var client = CreateClient(handler);

        await client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related");

        handler.PatchRequestCount.ShouldBe(1);
    }

    // ── Error propagation ───────────────────────────────────────────

    [Fact]
    public async Task RemoveLinkAsync_PropagatesExceptionWhenGetFails()
    {
        var handler = new ErrorHandler(HttpStatusCode.NotFound);
        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoNotFoundException>(
            () => client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related"));
    }

    [Fact]
    public async Task RemoveLinkAsync_PropagatesServerErrorFromGet()
    {
        var handler = new ErrorHandler(HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoServerException>(
            () => client.RemoveLinkAsync(sourceId: 100, targetId: 200, adoLinkType: "System.LinkTypes.Related"));
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

        public RemoveLinkTrackingHandler((string RelType, int TargetId)[] relations, int rev, string? urlPrefix = null)
        {
            _rev = rev;
            var prefix = urlPrefix ?? $"{OrgUrl}/_apis/wit/workitems";
            if (relations.Length == 0)
            {
                _relationsJson = "null";
            }
            else
            {
                var items = relations.Select(r =>
                    $"{{\"rel\":\"{r.RelType}\",\"url\":\"{prefix}/{r.TargetId}\",\"attributes\":{{\"name\":\"Related\"}}}}");
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

    /// <summary>
    /// HttpMessageHandler that always returns the specified HTTP error status code.
    /// Used to verify error propagation from the underlying SendAsync path.
    /// </summary>
    private sealed class ErrorHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
