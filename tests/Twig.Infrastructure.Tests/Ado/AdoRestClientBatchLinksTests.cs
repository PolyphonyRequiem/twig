using System.Net;
using System.Text;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Tests for <see cref="AdoRestClient.FetchBatchWithLinksAsync"/> — the plural
/// fetch-with-links added by ADO #154.
/// </summary>
/// <remarks>
/// 🔴 The load-bearing test in this file is
/// <see cref="FetchBatchWithLinksAsync_IssuesTheSameRequestsAsFetchBatchAsync"/>: the card's
/// acceptance criterion is that returning whole-set links costs NO additional ADO requests,
/// because the batch URL already carries <c>$expand=relations</c>. Asserting the links come
/// back would pass equally against an implementation that fetched them with N extra calls, so
/// the request count is asserted against the plain batch call as a control rather than against
/// a hardcoded number.
/// </remarks>
public class AdoRestClientBatchLinksTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "testproject";

    [Fact]
    public async Task FetchBatchWithLinksAsync_ReturnsLinksForEveryItemInTheSet()
    {
        // Two items, each with its own edge — a per-item link map, not one item's links.
        var handler = new RelationsHandler(new Dictionary<int, (int Target, string Rel)[]>
        {
            [10] = [(200, "System.LinkTypes.Dependency-Reverse")],
            [20] = [(201, "System.LinkTypes.Dependency-Forward")],
        });
        var client = CreateClient(handler);

        var (items, links) = await client.FetchBatchWithLinksAsync([10, 20]);

        items.Count.ShouldBe(2);
        links.ShouldContain(l => l.SourceId == 10 && l.TargetId == 200 && l.LinkType == LinkTypes.Predecessor);
        links.ShouldContain(l => l.SourceId == 20 && l.TargetId == 201 && l.LinkType == LinkTypes.Successor);
        links.Count.ShouldBe(2);
    }

    /// <summary>
    /// The card's acceptance criterion, asserted against a control rather than a constant:
    /// the with-links call must issue exactly the request count the plain batch call issues.
    /// </summary>
    [Fact]
    public async Task FetchBatchWithLinksAsync_IssuesTheSameRequestsAsFetchBatchAsync()
    {
        var relations = new Dictionary<int, (int Target, string Rel)[]>
        {
            [10] = [(200, "System.LinkTypes.Dependency-Reverse")],
            [20] = [(201, "System.LinkTypes.Related")],
            [30] = [],
        };

        var controlHandler = new RelationsHandler(relations);
        var controlItems = await CreateClient(controlHandler).FetchBatchAsync([10, 20, 30], CancellationToken.None);

        var handler = new RelationsHandler(relations);
        var (items, links) = await CreateClient(handler).FetchBatchWithLinksAsync([10, 20, 30]);

        // Precondition: the control actually fetched something, or the comparison is vacuous.
        controlItems.Count.ShouldBe(3);
        controlHandler.RequestCount.ShouldBeGreaterThan(0);

        // Same items, same requests — and links on top for free.
        items.Count.ShouldBe(controlItems.Count);
        handler.RequestCount.ShouldBe(controlHandler.RequestCount);
        handler.BatchRequestCount.ShouldBe(controlHandler.BatchRequestCount);
        links.Count.ShouldBe(2);
    }

    /// <summary>
    /// The relations were always on the wire — this pins the query string that makes the
    /// zero-extra-cost property true, so removing it goes red here rather than silently
    /// turning the feature into N extra round trips later.
    /// </summary>
    [Fact]
    public async Task FetchBatchWithLinksAsync_BatchUrlExpandsRelations()
    {
        var handler = new RelationsHandler(new Dictionary<int, (int Target, string Rel)[]> { [10] = [] });
        var client = CreateClient(handler);

        await client.FetchBatchWithLinksAsync([10]);

        handler.BatchRequestUrls.Count.ShouldBe(1);
        handler.BatchRequestUrls[0].ShouldContain("$expand=relations");
    }

    [Fact]
    public async Task FetchBatchWithLinksAsync_ChunksOver200AndConcatenatesLinksFromEveryChunk()
    {
        var relations = new Dictionary<int, (int Target, string Rel)[]>();
        for (var i = 1; i <= 250; i++)
            relations[i] = [(i + 10_000, "System.LinkTypes.Related")];

        var handler = new RelationsHandler(relations);
        var client = CreateClient(handler);

        var (items, links) = await client.FetchBatchWithLinksAsync(Enumerable.Range(1, 250).ToList());

        items.Count.ShouldBe(250);
        handler.BatchRequestCount.ShouldBe(2);
        // 250, not 200 — a mutant returning only the first chunk's links would give 200.
        links.Count.ShouldBe(250);
        // An id from the SECOND chunk specifically.
        links.ShouldContain(l => l.SourceId == 250);
    }

    [Fact]
    public async Task FetchBatchWithLinksAsync_ItemWithNoRelations_YieldsItemAndNoLinks()
    {
        var handler = new RelationsHandler(new Dictionary<int, (int Target, string Rel)[]> { [10] = [] });
        var client = CreateClient(handler);

        var (items, links) = await client.FetchBatchWithLinksAsync([10]);

        items.Count.ShouldBe(1);
        links.ShouldBeEmpty();
    }

    /// <summary>
    /// Hierarchy relations are NOT non-hierarchy edges — they are already carried by
    /// <c>ParentId</c> on the snapshot, and emitting them as links would double-report them.
    /// </summary>
    [Fact]
    public async Task FetchBatchWithLinksAsync_HierarchyRelations_AreNotReturnedAsLinks()
    {
        var handler = new RelationsHandler(new Dictionary<int, (int Target, string Rel)[]>
        {
            [10] = [(999, "System.LinkTypes.Hierarchy-Reverse"), (200, "System.LinkTypes.Related")],
        });
        var client = CreateClient(handler);

        var (_, links) = await client.FetchBatchWithLinksAsync([10]);

        links.Count.ShouldBe(1);
        links[0].TargetId.ShouldBe(200);
    }

    [Fact]
    public async Task FetchBatchWithLinksAsync_IsReachableThroughTheInterface()
    {
        var handler = new RelationsHandler(new Dictionary<int, (int Target, string Rel)[]>
        {
            [10] = [(200, "System.LinkTypes.Dependency-Reverse")],
        });
        IAdoWorkItemService service = CreateClient(handler);

        var (items, links) = await service.FetchBatchWithLinksAsync([10]);

        items.Count.ShouldBe(1);
        links.Count.ShouldBe(1);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoRestClient CreateClient(RelationsHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        return new AdoRestClient(http, new FakeAuthProvider(), OrgUrl, Project, new WorkItemMapper());
    }

    private sealed class FakeAuthProvider : IAuthenticationProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult("fake-bearer-token");

        public void InvalidateToken() { }
    }

    /// <summary>
    /// Serves batch responses whose work items carry a <c>relations</c> array, which the
    /// existing <c>AdoRestClientBatchTests.TrackingHandler</c> does not — a fixture without
    /// relations cannot tell a link-retaining mapper from a link-dropping one.
    /// </summary>
    private sealed class RelationsHandler(IReadOnlyDictionary<int, (int Target, string Rel)[]> relations)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public int BatchRequestCount { get; private set; }
        public List<string> BatchRequestUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var url = request.RequestUri!.ToString();

            if (url.Contains("/_apis/wit/workitems?ids=", StringComparison.Ordinal))
            {
                BatchRequestCount++;
                BatchRequestUrls.Add(url);

                var ids = ExtractIds(url).Where(relations.ContainsKey).ToList();
                var items = ids.Select(BuildWorkItemJson).ToList();
                var json = $"{{\"count\":{items.Count},\"value\":[{string.Join(',', items)}]}}";

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static List<int> ExtractIds(string url)
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            var idsStr = query["ids"];
            return string.IsNullOrEmpty(idsStr) ? [] : idsStr.Split(',').Select(int.Parse).ToList();
        }

        private string BuildWorkItemJson(int id)
        {
            var rels = relations[id].Select(r =>
                $"{{\"rel\":\"{r.Rel}\",\"url\":\"{OrgUrl}/_apis/wit/workitems/{r.Target}\"}}");

            return $"{{\"id\":{id},\"rev\":1,\"fields\":{{\"System.WorkItemType\":\"Task\"," +
                   $"\"System.Title\":\"Item {id}\",\"System.State\":\"New\"}}," +
                   $"\"relations\":[{string.Join(',', rels)}]}}";
        }
    }
}
