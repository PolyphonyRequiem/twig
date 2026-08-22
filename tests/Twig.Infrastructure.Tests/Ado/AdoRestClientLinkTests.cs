using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
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

    // ── AB#620: the link COMMENT ─────────────────────────────────────
    //
    // The reason for a relationship lives in the relation's own attributes.comment, not in a
    // work item comment. These assert the wire shape the card measured by hand against REST.

    [Fact]
    public async Task AddLinkWithCommentAsync_PutsCommentInRelationAttributes()
    {
        var handler = new LinkTrackingHandler();
        var client = CreateClient(handler);

        await client.AddLinkWithCommentAsync(
            sourceId: 619, targetId: 615,
            adoLinkType: "System.LinkTypes.Related",
            comment: "same root cause");

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var value = doc.RootElement[0].GetProperty("value");
        value.GetProperty("rel").GetString().ShouldBe("System.LinkTypes.Related");
        value.GetProperty("url").GetString().ShouldBe($"{OrgUrl}/_apis/wit/workitems/615");
        value.GetProperty("attributes").GetProperty("comment").GetString().ShouldBe("same root cause");
    }

    /// <summary>
    /// An empty <c>attributes</c> object is not the same request as no attributes at all, and
    /// every pre-AB#620 caller now routes through this method. Emitting one would silently
    /// change the wire shape of every existing link write.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddLinkWithCommentAsync_NoComment_OmitsAttributesEntirely(string? comment)
    {
        var handler = new LinkTrackingHandler();
        var client = CreateClient(handler);

        await client.AddLinkWithCommentAsync(1, 2, "System.LinkTypes.Related", comment);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        doc.RootElement[0].GetProperty("value")
            .TryGetProperty("attributes", out _).ShouldBeFalse();
    }

    /// <summary>
    /// The un-commented entry point must send BYTE-IDENTICAL JSON to the commentless path of
    /// the new one — that equivalence is what makes routing every caller through the new method
    /// a safe change rather than a silent request rewrite.
    /// </summary>
    [Fact]
    public async Task AddLinkAsync_AndCommentlessAddLinkWithComment_SendIdenticalBodies()
    {
        var plainHandler = new LinkTrackingHandler();
        await CreateClient(plainHandler).AddLinkAsync(7, 8, "System.LinkTypes.Related");

        var commentHandler = new LinkTrackingHandler();
        await CreateClient(commentHandler).AddLinkWithCommentAsync(7, 8, "System.LinkTypes.Related", comment: null);

        commentHandler.LastRequestBody.ShouldBe(plainHandler.LastRequestBody);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoRestClient CreateClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var auth = new FakeAuthProvider();
        return new AdoRestClient(http, auth, OrgUrl, Project, new WorkItemMapper());
    }

    private sealed class FakeAuthProvider : IAuthenticationProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult("fake-bearer-token");

        public void InvalidateToken() { }
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
