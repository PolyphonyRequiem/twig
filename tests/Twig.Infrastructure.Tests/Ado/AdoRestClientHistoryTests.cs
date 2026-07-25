using System.Net;
using System.Text;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// HTTP-seam tests for work-item history (twig#241), faking ADO at the
/// <see cref="HttpMessageHandler"/> boundary in line with the established prior art in
/// <see cref="AdoRestClientBatchTests"/>. Assertions are on the emitted contract and on
/// observable request behavior — the number and shape of outbound calls — not on internal
/// parser structure. Runs with no network and no tenant.
/// </summary>
public class AdoRestClientHistoryTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "testproject";

    // ── Pagination ──────────────────────────────────────────────────

    [Fact]
    public async Task History_SinglePartialPage_MakesOneUpdatesRequest()
    {
        var handler = new HistoryHandler();
        handler.SetUpdates(TotalUpdates(5));
        var client = CreateClient(handler);

        var history = await client.FetchHistoryAsync(1, WorkItemHistoryOptions.Brief);

        history.Events.Count.ShouldBe(5);
        history.Complete.ShouldBeTrue();
        handler.UpdatesRequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task History_ExactPageMultiple_FetchesAnExtraPageToSeeAShortPage()
    {
        // With exactly PageSize updates the first page is full, so termination cannot be
        // assumed — the traversal must request another page and see it come back short.
        var handler = new HistoryHandler();
        handler.SetUpdates(TotalUpdates(AdoRestClient.HistoryPageSize));
        var client = CreateClient(handler);

        var history = await client.FetchHistoryAsync(1, WorkItemHistoryOptions.Brief);

        history.Events.Count.ShouldBe(AdoRestClient.HistoryPageSize);
        handler.UpdatesRequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task History_MultiPage_TraversesEveryPageAndConcatenates()
    {
        var total = (AdoRestClient.HistoryPageSize * 3) + 7;
        var handler = new HistoryHandler();
        handler.SetUpdates(TotalUpdates(total));
        var client = CreateClient(handler);

        var history = await client.FetchHistoryAsync(1, WorkItemHistoryOptions.Brief);

        history.Events.Count.ShouldBe(total);
        history.Events.Select(e => e.UpdateId).ShouldBe(Enumerable.Range(1, total));
        history.Complete.ShouldBeTrue();
        handler.UpdatesRequestCount.ShouldBe(4);
    }

    [Fact]
    public async Task History_UsesTopAndSkipOffsetPaging()
    {
        var handler = new HistoryHandler();
        handler.SetUpdates(TotalUpdates(AdoRestClient.HistoryPageSize + 1));
        var client = CreateClient(handler);

        await client.FetchHistoryAsync(1, WorkItemHistoryOptions.Brief);

        handler.UpdatesRequestUrls[0].ShouldContain($"$top={AdoRestClient.HistoryPageSize}");
        handler.UpdatesRequestUrls[0].ShouldContain("$skip=0");
        handler.UpdatesRequestUrls[1].ShouldContain($"$skip={AdoRestClient.HistoryPageSize}");
    }

    [Fact]
    public async Task History_TerminatesOnShortPage_NotOnCount()
    {
        // The per-response `count` reflects the current page, not the total history. A handler
        // that reports a misleading count must not change the traversal.
        var handler = new HistoryHandler { LieAboutCount = true };
        handler.SetUpdates(TotalUpdates(AdoRestClient.HistoryPageSize + 3));
        var client = CreateClient(handler);

        var history = await client.FetchHistoryAsync(1, WorkItemHistoryOptions.Brief);

        history.Events.Count.ShouldBe(AdoRestClient.HistoryPageSize + 3);
        handler.UpdatesRequestCount.ShouldBe(2);
    }

    // ── Complete-or-error ───────────────────────────────────────────

    [Fact]
    public async Task History_FailureOnLaterPage_ThrowsRatherThanReturningPartialSuccess()
    {
        var handler = new HistoryHandler { FailUpdatesRequestNumber = 2 };
        handler.SetUpdates(TotalUpdates(AdoRestClient.HistoryPageSize + 5));
        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoException>(
            () => client.FetchHistoryAsync(1, WorkItemHistoryOptions.Brief));
    }

    [Fact]
    public async Task History_NotFound_SurfacesTypedError_NotAnEmptyTimeline()
    {
        var handler = new HistoryHandler { UpdatesStatusCode = HttpStatusCode.NotFound };
        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoNotFoundException>(
            () => client.FetchHistoryAsync(999999, WorkItemHistoryOptions.Brief));
    }

    // ── Relation target enrichment ──────────────────────────────────

    [Fact]
    public async Task History_EnrichesRelationTargets_WithSingleBatchCallUsingErrorPolicyOmit()
    {
        var handler = new HistoryHandler();
        handler.SetUpdates([
            RelationUpdate(1, added: 10),
            RelationUpdate(2, added: 11),
            RelationUpdate(3, removed: 10),
        ]);
        handler.EnrichableIds.Add(10);
        handler.EnrichableIds.Add(11);
        var client = CreateClient(handler);

        var history = await client.FetchHistoryAsync(1, WorkItemHistoryOptions.Brief);

        // One updates page + exactly one batch enrichment call — flat regardless of count.
        handler.BatchRequestCount.ShouldBe(1);
        // errorPolicy=omit is mandatory: without it one deleted target 404s the whole call.
        handler.BatchRequestUrls[0].ShouldContain("errorPolicy=omit");
        handler.BatchRequestUrls[0].ShouldContain("ids=10,11");

        var target = history.Events.Single(e => e.UpdateId == 1).Relations.Single().Target;
        target!.Deleted.ShouldBeFalse();
        target.Title.ShouldBe("Item 10");
    }

    [Fact]
    public async Task History_DeletedRelationTarget_IsReportedAsDeleted()
    {
        var handler = new HistoryHandler();
        handler.SetUpdates([RelationUpdate(1, added: 3323)]);
        // 3323 is intentionally not enrichable — errorPolicy=omit drops it from the batch.
        var client = CreateClient(handler);

        var history = await client.FetchHistoryAsync(1, WorkItemHistoryOptions.Brief);

        var target = history.Events.Single().Relations.Single().Target;
        target!.Deleted.ShouldBeTrue();
        target.Id.ShouldBe(3323);
        target.Title.ShouldBeNull();
    }

    [Fact]
    public async Task History_EnrichmentFailure_LeavesCompleteTrue()
    {
        // The traversal was complete regardless of whether decoration succeeded. Conflating
        // the two would fail the command on the items most worth reading.
        var handler = new HistoryHandler { FailBatchRequests = true };
        handler.SetUpdates([RelationUpdate(1, added: 10)]);
        var client = CreateClient(handler);

        var history = await client.FetchHistoryAsync(1, WorkItemHistoryOptions.Brief);

        history.Complete.ShouldBeTrue();
        history.Events.Single().Relations.Single().Target!.Deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task History_NoRelations_MakesNoEnrichmentCall()
    {
        var handler = new HistoryHandler();
        handler.SetUpdates(TotalUpdates(3));
        var client = CreateClient(handler);

        await client.FetchHistoryAsync(1, WorkItemHistoryOptions.Brief);

        handler.BatchRequestCount.ShouldBe(0);
    }

    // ── Interface surface + emitted contract ────────────────────────

    [Fact]
    public async Task FetchHistoryAsync_IsAvailableOnTheInterface()
    {
        var handler = new HistoryHandler();
        handler.SetUpdates(TotalUpdates(1));
        IAdoWorkItemService service = CreateClient(handler);

        var history = await service.FetchHistoryAsync(7, WorkItemHistoryOptions.Brief);

        history.WorkItemId.ShouldBe(7);
    }

    [Fact]
    public async Task History_EmittedJson_HasTheExpectedV1Shape()
    {
        var handler = new HistoryHandler();
        handler.SetUpdates([RelationUpdate(1, added: 10)]);
        handler.EnrichableIds.Add(10);
        var client = CreateClient(handler);

        var json = WorkItemHistoryJsonWriter.Write(
            await client.FetchHistoryAsync(42, WorkItemHistoryOptions.Brief));

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("workItemId").GetInt32().ShouldBe(42);
        root.GetProperty("complete").GetBoolean().ShouldBeTrue();
        root.GetProperty("eventCount").GetInt32().ShouldBe(1);

        var evt = root.GetProperty("events")[0];
        evt.GetProperty("updateId").GetInt32().ShouldBe(1);
        evt.TryGetProperty("revision", out _).ShouldBeTrue();
        evt.TryGetProperty("changedAt", out _).ShouldBeTrue();
        evt.TryGetProperty("changed", out _).ShouldBeTrue();
        // Brief events carry no `fields` block.
        evt.TryGetProperty("fields", out _).ShouldBeFalse();

        var relation = evt.GetProperty("relations")[0];
        relation.GetProperty("kind").GetString().ShouldBe("added");
        relation.GetProperty("targetId").GetInt32().ShouldBe(10);
        relation.GetProperty("target").GetProperty("deleted").GetBoolean().ShouldBeFalse();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static AdoRestClient CreateClient(HistoryHandler handler)
        => new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) },
            new FakeAuthProvider(), OrgUrl, Project, new Domain.Services.WorkItemMapper());

    private static List<string> TotalUpdates(int count)
        => Enumerable.Range(1, count).Select(i =>
            "{\"id\":" + i + ",\"rev\":" + i +
            ",\"revisedDate\":\"2026-01-0" + ((i % 9) + 1) + "T00:00:00Z\"," +
            "\"fields\":{\"System.State\":{\"oldValue\":\"To Do\",\"newValue\":\"Doing\"}}}").ToList();

    private static string RelationUpdate(int updateId, int? added = null, int? removed = null)
    {
        var parts = new List<string>();
        if (added.HasValue)
            parts.Add("\"added\":[{\"rel\":\"System.LinkTypes.Hierarchy-Forward\",\"url\":\"" +
                      OrgUrl + "/p/_apis/wit/workItems/" + added.Value + "\"}]");
        if (removed.HasValue)
            parts.Add("\"removed\":[{\"rel\":\"System.LinkTypes.Hierarchy-Forward\",\"url\":\"" +
                      OrgUrl + "/p/_apis/wit/workItems/" + removed.Value + "\"}]");

        return "{\"id\":" + updateId + ",\"rev\":1,\"revisedDate\":\"2026-01-01T00:00:00Z\"," +
               "\"relations\":{" + string.Join(',', parts) + "}}";
    }

    private sealed class FakeAuthProvider : IAuthenticationProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult("fake-bearer-token");

        public void InvalidateToken() { }
    }

    /// <summary>
    /// Serves paged <c>/updates</c> responses and batch enrichment responses, and records
    /// outbound request counts and URLs so tests can assert on request behavior.
    /// </summary>
    private sealed class HistoryHandler : HttpMessageHandler
    {
        private List<string> _updates = [];

        public int UpdatesRequestCount { get; private set; }
        public int BatchRequestCount { get; private set; }
        public List<string> UpdatesRequestUrls { get; } = [];
        public List<string> BatchRequestUrls { get; } = [];
        public HashSet<int> EnrichableIds { get; } = [];

        public HttpStatusCode UpdatesStatusCode { get; init; } = HttpStatusCode.OK;
        public int? FailUpdatesRequestNumber { get; init; }
        public bool FailBatchRequests { get; init; }

        /// <summary>Reports a bogus <c>count</c> so a count-based terminator would misbehave.</summary>
        public bool LieAboutCount { get; init; }

        public void SetUpdates(List<string> updates) => _updates = updates;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/updates", StringComparison.OrdinalIgnoreCase))
            {
                UpdatesRequestCount++;
                UpdatesRequestUrls.Add(url);

                if (UpdatesStatusCode != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(UpdatesStatusCode));

                if (FailUpdatesRequestNumber == UpdatesRequestCount)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var skip = int.Parse(query["$skip"] ?? "0");
                var top = int.Parse(query["$top"] ?? "100");
                var page = _updates.Skip(skip).Take(top).ToList();
                var reported = LieAboutCount ? _updates.Count : page.Count;

                return Task.FromResult(JsonResponse(
                    $$"""{"count":{{reported}},"value":[{{string.Join(',', page)}}]}"""));
            }

            if (url.Contains("/_apis/wit/workitems?ids=", StringComparison.OrdinalIgnoreCase))
            {
                BatchRequestCount++;
                BatchRequestUrls.Add(url);

                if (FailBatchRequests)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var ids = (query["ids"] ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse)
                    // errorPolicy=omit semantics: unresolvable targets are simply absent.
                    .Where(EnrichableIds.Contains)
                    .ToList();

                var items = ids.Select(id =>
                    "{\"id\":" + id + ",\"rev\":1,\"fields\":{\"System.Title\":\"Item " + id +
                    "\",\"System.WorkItemType\":\"Task\",\"System.State\":\"Doing\"}}");

                return Task.FromResult(JsonResponse(
                    $$"""{"count":{{ids.Count}},"value":[{{string.Join(',', items)}}]}"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
