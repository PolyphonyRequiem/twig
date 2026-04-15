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

    // ── ProcessConfiguration cache ──────────────────────────────────

    [Fact]
    public async Task GetProcessConfigurationAsync_MultipleCalls_OnlyOneHttpCall()
    {
        var handler = new CountingHandler();
        handler.SetRawResponse("/_apis/work/processconfiguration",
            """{"taskBacklog":{"name":"Tasks","workItemTypes":[{"name":"Task"}]},"requirementBacklog":{"name":"Stories","workItemTypes":[{"name":"User Story"}]},"portfolioBacklogs":[{"name":"Epics","workItemTypes":[{"name":"Epic"}]}]}""");
        var service = CreateService(handler);

        _ = await service.GetProcessConfigurationAsync();
        _ = await service.GetProcessConfigurationAsync();
        _ = await service.GetProcessConfigurationAsync();

        handler.GetCallCount("/_apis/work/processconfiguration").ShouldBe(1);
    }

    [Fact]
    public async Task GetProcessConfigurationAsync_ReturnsSameData_FromCache()
    {
        var handler = new CountingHandler();
        handler.SetRawResponse("/_apis/work/processconfiguration",
            """{"taskBacklog":{"name":"Tasks","workItemTypes":[{"name":"Task"}]},"requirementBacklog":{"name":"Stories","workItemTypes":[{"name":"User Story"}]}}""");
        var service = CreateService(handler);

        var first = await service.GetProcessConfigurationAsync();
        var second = await service.GetProcessConfigurationAsync();

        first.TaskBacklog.ShouldNotBeNull();
        first.TaskBacklog.Name.ShouldBe("Tasks");
        first.RequirementBacklog.ShouldNotBeNull();
        first.RequirementBacklog.Name.ShouldBe("Stories");
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact]
    public async Task GetProcessConfigurationAsync_ErrorCached_NoRetry()
    {
        var handler = new CountingHandler();
        // No response set for processconfiguration → returns 404 → AdoNotFoundException
        var service = CreateService(handler);

        var first = await service.GetProcessConfigurationAsync();
        var second = await service.GetProcessConfigurationAsync();

        // Fallback returns empty ProcessConfigurationData
        first.TaskBacklog.ShouldBeNull();
        ReferenceEquals(first, second).ShouldBeTrue();
        handler.GetCallCount("/_apis/work/processconfiguration").ShouldBe(0, "No matching URL fragment means CountingHandler returns 404 with empty content");
    }

    // ── FieldDefinitions cache ──────────────────────────────────────

    [Fact]
    public async Task GetFieldDefinitionsAsync_MultipleCalls_OnlyOneHttpCall()
    {
        var handler = new CountingHandler();
        handler.SetRawResponse("/_apis/wit/fields",
            """{"count":2,"value":[{"referenceName":"System.Title","name":"Title","type":"string","readOnly":false},{"referenceName":"System.State","name":"State","type":"string","readOnly":false}]}""");
        var service = CreateService(handler);

        _ = await service.GetFieldDefinitionsAsync();
        _ = await service.GetFieldDefinitionsAsync();
        _ = await service.GetFieldDefinitionsAsync();

        handler.GetCallCount("/_apis/wit/fields").ShouldBe(1);
    }

    [Fact]
    public async Task GetFieldDefinitionsAsync_ReturnsSameData_FromCache()
    {
        var handler = new CountingHandler();
        handler.SetRawResponse("/_apis/wit/fields",
            """{"count":2,"value":[{"referenceName":"System.Title","name":"Title","type":"string","readOnly":false},{"referenceName":"System.State","name":"State","type":"string","readOnly":false}]}""");
        var service = CreateService(handler);

        var first = await service.GetFieldDefinitionsAsync();
        var second = await service.GetFieldDefinitionsAsync();

        first.Count.ShouldBe(2);
        first[0].ReferenceName.ShouldBe("System.Title");
        first[1].ReferenceName.ShouldBe("System.State");
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact]
    public async Task GetFieldDefinitionsAsync_ErrorCached_NoRetry()
    {
        var handler = new CountingHandler();
        // No response set for fields → returns 404
        var service = CreateService(handler);

        var first = await service.GetFieldDefinitionsAsync();
        var second = await service.GetFieldDefinitionsAsync();

        first.Count.ShouldBe(0);
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact]
    public async Task AllCachesIndependent_EachEndpointCalledOnce()
    {
        var handler = new CountingHandler();
        handler.SetWorkItemTypesResponseWithStates(
            ("Task", "0000FF", "icon_task", false, Array.Empty<(string, string)>()));
        handler.SetRawResponse("/_apis/work/processconfiguration",
            """{"taskBacklog":{"name":"Tasks","workItemTypes":[{"name":"Task"}]}}""");
        handler.SetRawResponse("/_apis/wit/fields",
            """{"count":1,"value":[{"referenceName":"System.Title","name":"Title","type":"string","readOnly":false}]}""");
        var service = CreateService(handler);

        _ = await service.GetWorkItemTypeAppearancesAsync();
        _ = await service.GetProcessConfigurationAsync();
        _ = await service.GetFieldDefinitionsAsync();
        // Second round — all from cache
        _ = await service.GetWorkItemTypeAppearancesAsync();
        _ = await service.GetProcessConfigurationAsync();
        _ = await service.GetFieldDefinitionsAsync();

        handler.GetCallCount("/_apis/wit/workitemtypes").ShouldBe(1);
        handler.GetCallCount("/_apis/work/processconfiguration").ShouldBe(1);
        handler.GetCallCount("/_apis/wit/fields").ShouldBe(1);
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
