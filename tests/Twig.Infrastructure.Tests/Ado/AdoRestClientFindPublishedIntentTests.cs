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
/// Unit tests for <see cref="AdoRestClient.FindPublishedIntentAsync"/> — the recovery half of
/// wayfinder 0015's intent record.
/// <para>
/// <b>These exist because the first implementation was wrong against live ADO and no mocked
/// test could see it.</b> The query carried a time component on a <c>System.CreatedDate</c>
/// comparison without the <c>timePrecision</c> query-string parameter, and ADO rejected it:
/// </para>
/// <code>
/// HTTP 400 — "You cannot supply a time with the date when running a query using date
/// precision. The error is caused by «[System.CreatedDate] >= '2026-07-27T15:02:58Z'»."
/// </code>
/// <para>
/// That failure is silent in the worst possible way: the recovery query returns nothing, the
/// orchestrator concludes the create never landed, and the retry duplicates the work item —
/// exactly the #270 bug this ticket exists to close. Verified against a real project
/// (dangreen-msft/Twig) with a throwaway item; a body-level <c>timePrecision</c> field is
/// silently ignored, and dropping the time entirely degrades the fence to day granularity.
/// </para>
/// <para>
/// So these assert the URL, not just the result: the shape of the request is the thing that
/// was broken.
/// </para>
/// </summary>
public sealed class AdoRestClientFindPublishedIntentTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "testproject";

    private static readonly DateTimeOffset Fence =
        new(2026, 7, 27, 15, 2, 58, TimeSpan.Zero);

    private static PublishIntent Intent(
        string title = "A staged seed", string typeName = "Task", DateTimeOffset? recordedAt = null)
        => new()
        {
            Identity = StagedIdentity.New(),
            Title = title,
            TypeName = typeName,
            RecordedAt = recordedAt ?? Fence,
        };

    [Fact]
    public async Task FindPublishedIntentAsync_SetsTimePrecision_SoAdoAcceptsATimedFence()
    {
        var handler = new WiqlTrackingHandler([3329]);
        var client = CreateClient(handler);

        await client.FindPublishedIntentAsync(Intent());

        handler.LastWiqlUrl.ShouldNotBeNull();

        // Without this, live ADO answers HTTP 400 and recovery silently finds nothing.
        handler.LastWiqlUrl.ShouldContain("timePrecision=true");
    }

    [Fact]
    public async Task FindPublishedIntentAsync_FencesOnCreatedDate_WithAWholeSecondUtcStamp()
    {
        var handler = new WiqlTrackingHandler([3329]);
        var client = CreateClient(handler);

        await client.FindPublishedIntentAsync(Intent());

        var query = handler.LastQuery.ShouldNotBeNull();

        // Truncated to the second and rounded DOWN — the fence is a lower bound, so rounding up
        // would exclude an item created in the same second, i.e. the one we are looking for.
        query.ShouldContain("[System.CreatedDate] >= '2026-07-27T15:02:58Z'");

        // Sub-second precision is what ADO rejects outright.
        query.ShouldNotContain(".0000000Z");
    }

    [Fact]
    public async Task FindPublishedIntentAsync_RoundsAFractionalFenceDown_NotUp()
    {
        var handler = new WiqlTrackingHandler([3329]);
        var client = CreateClient(handler);

        // An intent recorded 900ms into the second must not fence past its own create.
        var fractional = new DateTimeOffset(2026, 7, 27, 15, 2, 58, 900, TimeSpan.Zero);
        await client.FindPublishedIntentAsync(Intent(recordedAt: fractional));

        handler.LastQuery.ShouldNotBeNull()
            .ShouldContain("[System.CreatedDate] >= '2026-07-27T15:02:58Z'");
    }

    [Fact]
    public async Task FindPublishedIntentAsync_NarrowsByTheConstantTagAndIdentifiesByTitleAndType()
    {
        var handler = new WiqlTrackingHandler([3329]);
        var client = CreateClient(handler);

        await client.FindPublishedIntentAsync(Intent());

        var query = handler.LastQuery.ShouldNotBeNull();
        query.ShouldContain($"[System.Tags] CONTAINS '{PublishIntent.IntentTag}'");
        query.ShouldContain("[System.Title] = 'A staged seed'");
        query.ShouldContain("[System.WorkItemType] = 'Task'");
    }

    [Fact]
    public async Task FindPublishedIntentAsync_EscapesQuotesInTheTitle()
    {
        var handler = new WiqlTrackingHandler([]);
        var client = CreateClient(handler);

        // Titles are user-supplied. WIQL escapes a single quote by doubling it; without this the
        // query is malformed and the recovery path fails open into a duplicate.
        await client.FindPublishedIntentAsync(Intent(title: "Bob's seed"));

        handler.LastQuery.ShouldNotBeNull().ShouldContain("'Bob''s seed'");
    }

    [Fact]
    public async Task FindPublishedIntentAsync_NoMatch_ReturnsNull()
    {
        var handler = new WiqlTrackingHandler([]);
        var client = CreateClient(handler);

        var result = await client.FindPublishedIntentAsync(Intent());

        result.ShouldBeNull();
    }

    [Fact]
    public async Task FindPublishedIntentAsync_MultipleMatches_ReturnsTheEarliestCreate()
    {
        var handler = new WiqlTrackingHandler([4444, 3329, 5555]);
        var client = CreateClient(handler);

        var result = await client.FindPublishedIntentAsync(Intent());

        // More than one match means a duplicate already exists. Adopt the FIRST create rather
        // than an accidental copy; the extras stay visible in ADO for the user to reconcile.
        result.ShouldBe(3329);
    }

    [Theory]
    [InlineData("", "Task")]
    [InlineData("A staged seed", "")]
    [InlineData("   ", "Task")]
    public async Task FindPublishedIntentAsync_WithoutBothIdentifiers_DoesNotQuery(
        string title, string typeName)
    {
        var handler = new WiqlTrackingHandler([3329]);
        var client = CreateClient(handler);

        var result = await client.FindPublishedIntentAsync(Intent(title, typeName));

        // The tag alone does not identify an item, so a half-specified lookup must not run and
        // must not return someone else's in-flight create.
        result.ShouldBeNull();
        handler.LastWiqlUrl.ShouldBeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoRestClient CreateClient(WiqlTrackingHandler handler)
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

    private sealed class WiqlTrackingHandler : HttpMessageHandler
    {
        private readonly string _wiqlJson;

        public string? LastWiqlUrl { get; private set; }

        /// <summary>The WIQL text the client actually sent, decoded from the request body.</summary>
        public string? LastQuery { get; private set; }

        public WiqlTrackingHandler(IReadOnlyList<int> ids)
        {
            var items = ids.Select(id => $"{{\"id\":{id},\"url\":\"\"}}");
            _wiqlJson = $"{{\"queryType\":\"flat\",\"workItems\":[{string.Join(',', items)}]}}";
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/_apis/wit/wiql"))
            {
                LastWiqlUrl = url;

                if (request.Content is not null)
                {
                    var body = await request.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    LastQuery = doc.RootElement.GetProperty("query").GetString();
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_wiqlJson, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
