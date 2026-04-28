using Shouldly;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public sealed class AdoIterationServiceCacheTests
{
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

    // ── TeamIterations cache ───────────────────────────────────────

    [Fact]
    public async Task GetTeamIterationsAsync_MultipleCalls_OnlyOneHttpCall()
    {
        var handler = new CountingHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/iterations",
            """{"count":2,"value":[{"id":"g1","name":"Sprint 1","path":"testproject\\Sprint 1","attributes":{"startDate":"2026-01-01","finishDate":"2026-01-14"}},{"id":"g2","name":"Sprint 2","path":"testproject\\Sprint 2","attributes":{"startDate":"2026-01-15","finishDate":"2026-01-28"}}]}""");
        var service = CreateService(handler);

        _ = await service.GetTeamIterationsAsync();
        _ = await service.GetTeamIterationsAsync();
        _ = await service.GetTeamIterationsAsync();

        handler.GetCallCount("/_apis/work/teamsettings/iterations").ShouldBe(1);
    }

    [Fact]
    public async Task GetTeamIterationsAsync_ReturnsSameData_FromCache()
    {
        var handler = new CountingHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/iterations",
            """{"count":1,"value":[{"id":"g1","name":"Sprint 1","path":"testproject\\Sprint 1","attributes":{"startDate":"2026-01-01","finishDate":"2026-01-14"}}]}""");
        var service = CreateService(handler);

        var first = await service.GetTeamIterationsAsync();
        var second = await service.GetTeamIterationsAsync();

        first.Count.ShouldBe(1);
        first[0].Path.ShouldBe(@"testproject\Sprint 1");
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoIterationService CreateService(CountingHandler handler) =>
        FakeHandler.CreateService(handler);

    /// <summary>
    /// FakeHandler subclass that counts HTTP calls per URL fragment.
    /// </summary>
    private sealed class CountingHandler : FakeHandler
    {
        private readonly Dictionary<string, int> _callCounts = new(StringComparer.OrdinalIgnoreCase);

        public int GetCallCount(string urlFragment)
        {
            _callCounts.TryGetValue(urlFragment, out var count);
            return count;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            foreach (var key in _responses.Keys)
            {
                if (url.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    _callCounts[key] = _callCounts.GetValueOrDefault(key) + 1;
                    break;
                }
            }
            return base.SendAsync(request, ct);
        }
    }
}
