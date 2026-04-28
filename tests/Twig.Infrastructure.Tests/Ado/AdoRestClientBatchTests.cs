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
/// Unit tests for <see cref="AdoRestClient"/> batch chunking logic.
/// Uses a fake HttpMessageHandler to verify the number of outbound requests
/// and result concatenation when IDs exceed MaxBatchSize (200).
/// </summary>
public class AdoRestClientBatchTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "testproject";

    [Fact]
    public async Task FetchChildrenAsync_Exactly200Ids_SendsSingleBatchRequest()
    {
        var ids = Enumerable.Range(1, 200).ToList();
        var handler = new TrackingHandler(ids);
        var client = CreateClient(handler);

        // FetchChildrenAsync: first WIQL returns ids, then batch fetch
        handler.EnqueueWiqlResponse(ids);

        var result = await client.FetchChildrenAsync(parentId: 999);

        result.Count.ShouldBe(200);
        // 1 WIQL + 1 batch GET = 2 total requests
        handler.RequestCount.ShouldBe(2);
        handler.BatchRequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task FetchChildrenAsync_201Ids_SendsTwoBatchRequests()
    {
        var ids = Enumerable.Range(1, 201).ToList();
        var handler = new TrackingHandler(ids);
        var client = CreateClient(handler);

        handler.EnqueueWiqlResponse(ids);

        var result = await client.FetchChildrenAsync(parentId: 999);

        result.Count.ShouldBe(201);
        // 1 WIQL + 2 batch GETs = 3 total requests
        handler.RequestCount.ShouldBe(3);
        handler.BatchRequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task FetchChildrenAsync_400Ids_SendsTwoBatchRequests()
    {
        var ids = Enumerable.Range(1, 400).ToList();
        var handler = new TrackingHandler(ids);
        var client = CreateClient(handler);

        handler.EnqueueWiqlResponse(ids);

        var result = await client.FetchChildrenAsync(parentId: 999);

        result.Count.ShouldBe(400);
        // 1 WIQL + 2 batch GETs (200 + 200) = 3 total requests
        handler.RequestCount.ShouldBe(3);
        handler.BatchRequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task FetchChildrenAsync_401Ids_SendsThreeBatchRequests()
    {
        var ids = Enumerable.Range(1, 401).ToList();
        var handler = new TrackingHandler(ids);
        var client = CreateClient(handler);

        handler.EnqueueWiqlResponse(ids);

        var result = await client.FetchChildrenAsync(parentId: 999);

        result.Count.ShouldBe(401);
        // 1 WIQL + 3 batch GETs (200 + 200 + 1) = 4 total requests
        handler.RequestCount.ShouldBe(4);
        handler.BatchRequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task FetchChildrenAsync_ChunkedResults_ConcatenatesAllItems()
    {
        var ids = Enumerable.Range(1, 250).ToList();
        var handler = new TrackingHandler(ids);
        var client = CreateClient(handler);

        handler.EnqueueWiqlResponse(ids);

        var result = await client.FetchChildrenAsync(parentId: 999);

        // All 250 items should be returned, concatenated from 2 chunks
        result.Count.ShouldBe(250);
        var resultIds = result.Select(wi => wi.Id).OrderBy(id => id).ToList();
        resultIds.ShouldBe(ids);
    }

    [Fact]
    public async Task FetchChildrenAsync_NoChildren_ReturnsEmpty()
    {
        var handler = new TrackingHandler(new List<int>());
        var client = CreateClient(handler);

        handler.EnqueueWiqlResponse(new List<int>());

        var result = await client.FetchChildrenAsync(parentId: 999);

        result.Count.ShouldBe(0);
        // Only the WIQL request, no batch requests
        handler.RequestCount.ShouldBe(1);
        handler.BatchRequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task FetchChildrenAsync_SingleId_SendsSingleBatchRequest()
    {
        var ids = new List<int> { 42 };
        var handler = new TrackingHandler(ids);
        var client = CreateClient(handler);

        handler.EnqueueWiqlResponse(ids);

        var result = await client.FetchChildrenAsync(parentId: 999);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(42);
        // 1 WIQL + 1 batch GET = 2 total requests
        handler.RequestCount.ShouldBe(2);
        handler.BatchRequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task FetchChildrenAsync_BatchRequestUrls_ContainCorrectIdCsv()
    {
        var ids = Enumerable.Range(1, 201).ToList();
        var handler = new TrackingHandler(ids);
        var client = CreateClient(handler);

        handler.EnqueueWiqlResponse(ids);

        await client.FetchChildrenAsync(parentId: 999);

        // First batch request should have IDs 1-200
        var firstBatchUrl = handler.BatchRequestUrls[0];
        firstBatchUrl.ShouldContain("ids=1,2,3,");
        firstBatchUrl.ShouldContain(",200");

        // Second batch request should have only ID 201
        var secondBatchUrl = handler.BatchRequestUrls[1];
        secondBatchUrl.ShouldContain("ids=201");
        secondBatchUrl.ShouldNotContain(",202");
    }

    // ── Direct FetchBatchAsync tests ────────────────────────────────

    [Fact]
    public async Task FetchBatchAsync_DirectCall_ReturnsMappedWorkItems()
    {
        var ids = new List<int> { 10, 20, 30 };
        var handler = new TrackingHandler(ids);
        var client = CreateClient(handler);

        var result = await client.FetchBatchAsync(ids, CancellationToken.None);

        result.Count.ShouldBe(3);
        result.Select(wi => wi.Id).OrderBy(id => id).ShouldBe(ids);
        handler.BatchRequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task FetchBatchAsync_DirectCall_ChunksOver200()
    {
        var ids = Enumerable.Range(1, 250).ToList();
        var handler = new TrackingHandler(ids);
        var client = CreateClient(handler);

        var result = await client.FetchBatchAsync(ids, CancellationToken.None);

        result.Count.ShouldBe(250);
        handler.BatchRequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task FetchBatchAsync_IsPublicOnInterface()
    {
        // Verify that AdoRestClient satisfies IAdoWorkItemService.FetchBatchAsync
        var ids = new List<int> { 1 };
        var handler = new TrackingHandler(ids);
        IAdoWorkItemService service = CreateClient(handler);

        var result = await service.FetchBatchAsync(ids);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(1);
    }

    [Fact]
    public async Task FetchBatchAsync_201Items_NoSilentTruncation_AllItemsReturned()
    {
        // EPIC-003 Task 3: Verify that a WIQL result with 201 IDs pages correctly
        // and does not silently truncate at the 200-item ADO batch limit.
        var ids = Enumerable.Range(1, 201).ToList();
        var handler = new TrackingHandler(ids);
        IAdoWorkItemService service = CreateClient(handler);

        var result = await service.FetchBatchAsync(ids);

        // All 201 items must be returned — no silent truncation at the 200 boundary
        result.Count.ShouldBe(201);
        var resultIds = result.Select(wi => wi.Id).OrderBy(id => id).ToList();
        resultIds.ShouldBe(ids);
        // Should have made 2 batch requests: 200 + 1
        handler.BatchRequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task FetchBatchAsync_ExactlyMaxBatchSize_SendsSingleRequest()
    {
        // Verify exactly 200 IDs (the limit) doesn't split
        var ids = Enumerable.Range(1, AdoRestClient.MaxBatchSize).ToList();
        var handler = new TrackingHandler(ids);
        IAdoWorkItemService service = CreateClient(handler);

        var result = await service.FetchBatchAsync(ids);

        result.Count.ShouldBe(200);
        handler.BatchRequestCount.ShouldBe(1);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoRestClient CreateClient(TrackingHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var auth = new FakeAuthProvider();
        return new AdoRestClient(http, auth, OrgUrl, Project, new WorkItemMapper());
    }

    /// <summary>
    /// Fake auth provider that returns a static Bearer token.
    /// </summary>
    private sealed class FakeAuthProvider : IAuthenticationProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult("fake-bearer-token");

        public void InvalidateToken() { }
    }

    /// <summary>
    /// HttpMessageHandler that tracks requests and returns appropriate JSON responses
    /// for WIQL and batch work item endpoints.
    /// </summary>
    private sealed class TrackingHandler : HttpMessageHandler
    {
        private readonly Dictionary<int, string> _workItemJsonCache = new();
        private string? _pendingWiqlJson;
        public int RequestCount { get; private set; }
        public int BatchRequestCount { get; private set; }
        public List<string> BatchRequestUrls { get; } = new();

        public TrackingHandler(List<int> allIds)
        {
            // Pre-build work item JSON for each ID
            foreach (var id in allIds)
            {
                _workItemJsonCache[id] = BuildWorkItemJson(id);
            }
        }

        public void EnqueueWiqlResponse(List<int> ids)
        {
            _pendingWiqlJson = BuildWiqlResponseJson(ids);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var url = request.RequestUri!.ToString();

            // WIQL endpoint
            if (url.Contains("/_apis/wit/wiql"))
            {
                var json = _pendingWiqlJson ?? BuildWiqlResponseJson(new List<int>());
                _pendingWiqlJson = null;
                return Task.FromResult(JsonResponse(json));
            }

            // Batch work items endpoint (GET with ids= parameter)
            if (url.Contains("/_apis/wit/workitems?ids="))
            {
                BatchRequestCount++;
                BatchRequestUrls.Add(url);

                var idsParam = ExtractIdsFromUrl(url);
                var items = idsParam
                    .Where(id => _workItemJsonCache.ContainsKey(id))
                    .Select(id => _workItemJsonCache[id])
                    .ToList();

                var json = $"{{\"count\":{items.Count},\"value\":[{string.Join(',', items)}]}}";
                return Task.FromResult(JsonResponse(json));
            }

            // Fallback
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        private static List<int> ExtractIdsFromUrl(string url)
        {
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var idsStr = query["ids"];
            if (string.IsNullOrEmpty(idsStr))
                return new List<int>();

            return idsStr.Split(',').Select(int.Parse).ToList();
        }

        private static string BuildWiqlResponseJson(List<int> ids)
        {
            var items = ids.Select(id => $"{{\"id\":{id},\"url\":\"\"}}");
            return $"{{\"queryType\":\"flat\",\"workItems\":[{string.Join(',', items)}]}}";
        }

        private static string BuildWorkItemJson(int id)
        {
            return $"{{\"id\":{id},\"rev\":1,\"fields\":{{\"System.WorkItemType\":\"Task\",\"System.Title\":\"Item {id}\",\"System.State\":\"New\"}}}}";
        }
    }
}
