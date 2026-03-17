using System.Net;
using System.Text;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Verifies that <see cref="AdoIterationService"/> caches the workitemtypes HTTP response
/// so that calling <c>DetectTemplateNameAsync</c>, <c>GetWorkItemTypeAppearancesAsync</c>,
/// and <c>GetWorkItemTypesWithStatesAsync</c> in sequence produces only a single HTTP request.
/// </summary>
public class AdoIterationServiceCacheTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "testproject";
    private const string Team = "testteam";

    [Fact]
    public async Task AllThreeMethods_OnlyOneHttpCallToWorkItemTypes()
    {
        var handler = new CountingHandler();
        handler.SetWorkItemTypesResponseWithStates(
            ("Epic", "FF0000", "icon_epic", false, new[] { ("New", "Proposed"), ("Active", "InProgress") }),
            ("User Story", "00FF00", "icon_story", false, new[] { ("New", "Proposed"), ("Done", "Completed") }),
            ("Task", "0000FF", "icon_task", false, new[] { ("To Do", "Proposed"), ("Doing", "InProgress") }));
        var service = CreateService(handler);

        _ = await service.DetectTemplateNameAsync();
        _ = await service.GetWorkItemTypeAppearancesAsync();
        _ = await service.GetWorkItemTypesWithStatesAsync();

        handler.GetCallCount("/_apis/wit/workitemtypes").ShouldBe(1);
    }

    [Fact]
    public async Task DetectTemplateNameAsync_ReturnsCorrectResult_FromCache()
    {
        var handler = new CountingHandler();
        handler.SetWorkItemTypesResponseWithStates(
            ("Epic", "FF0000", "icon_epic", false, Array.Empty<(string, string)>()),
            ("User Story", "00FF00", "icon_story", false, Array.Empty<(string, string)>()));
        var service = CreateService(handler);

        var template = await service.DetectTemplateNameAsync();

        template.ShouldBe("Agile");
    }

    [Fact]
    public async Task GetWorkItemTypeAppearancesAsync_ReturnsCorrectResult_FromCache()
    {
        var handler = new CountingHandler();
        handler.SetWorkItemTypesResponseWithStates(
            ("Epic", "FF0000", "icon_epic", false, Array.Empty<(string, string)>()),
            ("User Story", "00FF00", "icon_story", false, Array.Empty<(string, string)>()),
            ("DisabledType", "AAAAAA", "icon_disabled", true, Array.Empty<(string, string)>()));
        var service = CreateService(handler);

        // Prime the cache via DetectTemplateNameAsync
        _ = await service.DetectTemplateNameAsync();

        var appearances = await service.GetWorkItemTypeAppearancesAsync();

        appearances.Count.ShouldBe(2);
        appearances[0].Name.ShouldBe("Epic");
        appearances[1].Name.ShouldBe("User Story");
        handler.GetCallCount("/_apis/wit/workitemtypes").ShouldBe(1);
    }

    [Fact]
    public async Task GetWorkItemTypesWithStatesAsync_ReturnsCorrectResult_FromCache()
    {
        var handler = new CountingHandler();
        handler.SetWorkItemTypesResponseWithStates(
            ("Task", "F2CB1D", "icon_task", false, new[]
            {
                ("Active", "InProgress"),
                ("New", "Proposed"),
                ("Done", "Completed"),
            }));
        var service = CreateService(handler);

        // Prime the cache via DetectTemplateNameAsync
        _ = await service.DetectTemplateNameAsync();

        var types = await service.GetWorkItemTypesWithStatesAsync();

        types.Count.ShouldBe(1);
        types[0].Name.ShouldBe("Task");
        var stateNames = types[0].States.Select(s => s.Name).ToArray();
        stateNames.ShouldBe(new[] { "New", "Active", "Done" });
        handler.GetCallCount("/_apis/wit/workitemtypes").ShouldBe(1);
    }

    [Fact]
    public async Task SingleMethod_StillMakesExactlyOneCall()
    {
        var handler = new CountingHandler();
        handler.SetWorkItemTypesResponseWithStates(
            ("Epic", "FF0000", "icon_epic", false, Array.Empty<(string, string)>()));
        var service = CreateService(handler);

        _ = await service.GetWorkItemTypeAppearancesAsync();

        handler.GetCallCount("/_apis/wit/workitemtypes").ShouldBe(1);
    }

    [Fact]
    public async Task CacheDoesNotAffectOtherEndpoints()
    {
        var handler = new CountingHandler();
        handler.SetWorkItemTypesResponseWithStates(
            ("Epic", "FF0000", "icon_epic", false, Array.Empty<(string, string)>()));
        handler.SetRawResponse("/_apis/work/teamsettings/iterations",
            """{"count":1,"value":[{"id":"guid-1","name":"Sprint 1","path":"testproject\\Sprint 1","attributes":{"startDate":"2026-01-01","finishDate":"2026-01-14","timeFrame":"current"}}]}""");
        var service = CreateService(handler);

        _ = await service.DetectTemplateNameAsync();
        _ = await service.GetCurrentIterationAsync();

        handler.GetCallCount("/_apis/wit/workitemtypes").ShouldBe(1);
        handler.GetCallCount("/_apis/work/teamsettings/iterations").ShouldBe(1);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoIterationService CreateService(CountingHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var auth = new FakeAuthProvider();
        return new AdoIterationService(http, auth, OrgUrl, Project, Team);
    }

    private sealed class FakeAuthProvider : IAuthenticationProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult("fake-bearer-token");
    }

    /// <summary>
    /// HttpMessageHandler that counts calls per URL fragment and returns canned responses.
    /// </summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _callCounts = new(StringComparer.OrdinalIgnoreCase);

        public int GetCallCount(string urlFragment)
        {
            _callCounts.TryGetValue(urlFragment, out var count);
            return count;
        }

        public void SetRawResponse(string urlFragment, string json)
        {
            _responses[urlFragment] = json;
        }

        public void SetWorkItemTypesResponseWithStates(params (string name, string? color, string? iconId, bool isDisabled, (string name, string category)[] states)[] types)
        {
            var typeJsons = types.Select(t =>
            {
                var colorPart = t.color is not null ? $"\"color\":\"{t.color}\"" : "\"color\":null";
                var iconPart = t.iconId is not null
                    ? $"\"icon\":{{\"id\":\"{t.iconId}\",\"url\":\"https://example.com\"}}"
                    : "\"icon\":null";
                var stateJsons = t.states.Select(s => $"{{\"name\":\"{s.name}\",\"color\":\"FFFFFF\",\"category\":\"{s.category}\"}}");
                var statesJson = $"\"states\":[{string.Join(',', stateJsons)}]";
                return $"{{\"name\":\"{t.name}\",\"description\":\"\",\"referenceName\":\"System.{t.name.Replace(" ", "")}\",{colorPart},{iconPart},\"isDisabled\":{t.isDisabled.ToString().ToLowerInvariant()},{statesJson}}}";
            });
            var json = $"{{\"count\":{types.Length},\"value\":[{string.Join(',', typeJsons)}]}}";
            _responses["/_apis/wit/workitemtypes"] = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            foreach (var kvp in _responses)
            {
                if (url.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    // Increment call count
                    _callCounts.TryGetValue(kvp.Key, out var count);
                    _callCounts[kvp.Key] = count + 1;

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(kvp.Value, Encoding.UTF8, "application/json"),
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(""),
            });
        }
    }
}
