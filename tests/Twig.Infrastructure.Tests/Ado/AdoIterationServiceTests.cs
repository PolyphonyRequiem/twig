using System.Net;
using System.Text;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoIterationService"/>.
/// Uses a fake HttpMessageHandler to verify process template detection heuristics
/// and current iteration error paths without making real network calls.
/// </summary>
public class AdoIterationServiceTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "testproject";
    private const string Team = "testteam";

    // ── DetectTemplateNameAsync ──────────────────────────────────

    [Fact]
    public async Task DetectTemplateNameAsync_HasUserStory_ReturnsAgile()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponse("Epic", "Feature", "User Story", "Task", "Bug", "Issue");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("Agile");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_HasProductBacklogItem_ReturnsScrum()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponse("Epic", "Feature", "Product Backlog Item", "Task", "Bug", "Impediment");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("Scrum");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_HasRequirement_ReturnsCMMI()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponse("Epic", "Feature", "Requirement", "Task", "Bug", "Change Request", "Review");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("CMMI");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_NoDistinguishingType_ReturnsBasic()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponse("Epic", "Issue", "Task");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("Basic");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_EmptyTypeList_ReturnsBasic()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponse(); // no types
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("Basic");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_NullValueInResponse_ReturnsBasic()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/wit/workitemtypes", """{"count":0,"value":null}""");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("Basic");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_CaseInsensitiveTypeNames_ReturnsCorrectTemplate()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponse("epic", "feature", "user story", "task");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("Agile");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_UserStoryTakesPrecedenceOverPBI()
    {
        // If both User Story and Product Backlog Item exist, User Story (Agile) wins
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponse("User Story", "Product Backlog Item", "Requirement");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("Agile");
    }

    // ── DetectTemplateNameAsync (API-first + heuristic fallback) ───

    [Fact]
    public async Task DetectTemplateNameAsync_ApiReturnsTemplateName_ReturnsApiResult()
    {
        var handler = new FakeHandler();
        handler.SetProjectCapabilitiesResponse("Agile");
        handler.SetWorkItemTypesResponse("Epic", "Issue", "Task"); // heuristic would return Basic
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("Agile");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_ApiReturnsCustomTemplate_ReturnsCustomName()
    {
        var handler = new FakeHandler();
        handler.SetProjectCapabilitiesResponse("MyCustomProcess");
        handler.SetWorkItemTypesResponse("Epic", "Feature", "User Story", "Task");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("MyCustomProcess");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_ApiFails_FallsBackToHeuristic()
    {
        var handler = new FakeHandler();
        // No project capabilities response configured — 404 triggers heuristic fallback
        handler.SetWorkItemTypesResponse("Epic", "Feature", "User Story", "Task", "Bug");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("Agile");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_ApiReturnsEmptyTemplateName_FallsBackToHeuristic()
    {
        var handler = new FakeHandler();
        handler.SetProjectCapabilitiesResponse("");
        handler.SetWorkItemTypesResponse("Epic", "Feature", "Product Backlog Item", "Task");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("Scrum");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_ApiReturnsNullCapabilities_FallsBackToHeuristic()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/projects/", """{"capabilities":null}""");
        handler.SetWorkItemTypesResponse("Epic", "Feature", "Requirement", "Task");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("CMMI");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_ApiReturnsNullProcessTemplate_FallsBackToHeuristic()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/projects/", """{"capabilities":{"processTemplate":null}}""");
        handler.SetWorkItemTypesResponse("Epic", "Issue", "Task");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("Basic");
    }

    [Fact]
    public async Task DetectTemplateNameAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var handler = new CancelingHandler();
        var service = CreateService(handler);

        await Should.ThrowAsync<OperationCanceledException>(
            () => service.DetectTemplateNameAsync(CancellationToken.None));
    }

    // ── GetCurrentIterationAsync ────────────────────────────────────

    [Fact]
    public async Task GetCurrentIterationAsync_ValidIteration_ReturnsIterationPath()
    {
        var handler = new FakeHandler();
        handler.SetIterationResponse(@"TestProject\Sprint 1");
        var service = CreateService(handler);

        var result = await service.GetCurrentIterationAsync();

        result.Value.ShouldBe(@"TestProject\Sprint 1");
    }

    [Fact]
    public async Task GetCurrentIterationAsync_EmptyValueList_ThrowsAdoException()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/iterations", """{"count":0,"value":[]}""");
        var service = CreateService(handler);

        var ex = await Should.ThrowAsync<AdoException>(
            () => service.GetCurrentIterationAsync());

        ex.Message.ShouldContain("No current iteration");
    }

    [Fact]
    public async Task GetCurrentIterationAsync_NullValueList_ThrowsAdoException()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/iterations", """{"count":0,"value":null}""");
        var service = CreateService(handler);

        var ex = await Should.ThrowAsync<AdoException>(
            () => service.GetCurrentIterationAsync());

        ex.Message.ShouldContain("No current iteration");
    }

    [Fact]
    public async Task GetCurrentIterationAsync_InvalidIterationPath_ThrowsAdoException()
    {
        var handler = new FakeHandler();
        // Empty path segment is invalid for IterationPath.Parse
        handler.SetIterationResponse(@"TestProject\\");
        var service = CreateService(handler);

        var ex = await Should.ThrowAsync<AdoException>(
            () => service.GetCurrentIterationAsync());

        ex.Message.ShouldContain("Invalid iteration path");
    }

    [Fact]
    public async Task GetCurrentIterationAsync_EmptyStringPath_ThrowsAdoException()
    {
        var handler = new FakeHandler();
        handler.SetIterationResponse("");
        var service = CreateService(handler);

        var ex = await Should.ThrowAsync<AdoException>(
            () => service.GetCurrentIterationAsync());

        ex.Message.ShouldContain("Invalid iteration path");
    }

    [Fact]
    public async Task GetCurrentIterationAsync_MultiSegmentPath_ReturnsCorrectPath()
    {
        var handler = new FakeHandler();
        handler.SetIterationResponse(@"MyProject\Release 1\Sprint 3");
        var service = CreateService(handler);

        var result = await service.GetCurrentIterationAsync();

        result.Value.ShouldBe(@"MyProject\Release 1\Sprint 3");
    }

    // ── GetWorkItemTypeAppearancesAsync ────────────────────────────

    [Fact]
    public async Task GetWorkItemTypeAppearancesAsync_ReturnsMappedAppearances()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponseDetailed(
            ("Epic", "FF0000", "icon_epic", false),
            ("Feature", "00FF00", "icon_feature", false),
            ("Task", "0000FF", null, false));
        var service = CreateService(handler);

        var result = await service.GetWorkItemTypeAppearancesAsync();

        result.Count.ShouldBe(3);
        result[0].Name.ShouldBe("Epic");
        result[0].Color.ShouldBe("FF0000");
        result[0].IconId.ShouldBe("icon_epic");
        result[1].Name.ShouldBe("Feature");
        result[1].Color.ShouldBe("00FF00");
        result[1].IconId.ShouldBe("icon_feature");
        result[2].Name.ShouldBe("Task");
        result[2].Color.ShouldBe("0000FF");
        result[2].IconId.ShouldBeNull();
    }

    [Fact]
    public async Task GetWorkItemTypeAppearancesAsync_ExcludesDisabledTypes()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponseDetailed(
            ("Epic", "FF0000", "icon_epic", false),
            ("DisabledType", "AAAAAA", "icon_disabled", true),
            ("Task", "0000FF", "icon_task", false));
        var service = CreateService(handler);

        var result = await service.GetWorkItemTypeAppearancesAsync();

        result.Count.ShouldBe(2);
        result.ShouldNotContain(a => a.Name == "DisabledType");
    }

    [Fact]
    public async Task GetWorkItemTypeAppearancesAsync_EmptyResponse_ReturnsEmptyList()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponse(); // no types
        var service = CreateService(handler);

        var result = await service.GetWorkItemTypeAppearancesAsync();

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetWorkItemTypeAppearancesAsync_NullColor_ExcludesEntry()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponseDetailed(
            ("Epic", "FF0000", "icon_epic", false),
            ("NullColor", null, "icon_nc", false));
        var service = CreateService(handler);

        var result = await service.GetWorkItemTypeAppearancesAsync();

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("Epic");
    }

    [Fact]
    public async Task GetWorkItemTypeAppearancesAsync_NullName_ExcludesEntry()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/wit/workitemtypes",
            """{"count":2,"value":[{"name":null,"color":"FF0000","icon":{"id":"icon_x","url":"https://example.com"},"isDisabled":false},{"name":"Task","color":"0000FF","icon":{"id":"icon_task","url":"https://example.com"},"isDisabled":false}]}""");
        var service = CreateService(handler);

        var result = await service.GetWorkItemTypeAppearancesAsync();

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("Task");
    }

    [Fact]
    public async Task GetWorkItemTypeAppearancesAsync_NullValueInResponse_ReturnsEmptyList()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/wit/workitemtypes", """{"count":0,"value":null}""");
        var service = CreateService(handler);

        var result = await service.GetWorkItemTypeAppearancesAsync();

        result.ShouldBeEmpty();
    }

    // ── GetTeamAreaPathsAsync ──────────────────────────────────────

    [Fact]
    public async Task GetTeamAreaPathsAsync_MultipleValues_ReturnsAllPaths()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/teamfieldvalues",
            """{"defaultValue":"TestProject","values":[{"value":"TestProject\\TeamA","includeChildren":true},{"value":"TestProject\\TeamB","includeChildren":false}]}""");
        var service = CreateService(handler);

        var result = await service.GetTeamAreaPathsAsync();

        result.Count.ShouldBe(2);
        result[0].Path.ShouldBe("TestProject\\TeamA");
        result[0].IncludeChildren.ShouldBeTrue();
        result[1].Path.ShouldBe("TestProject\\TeamB");
        result[1].IncludeChildren.ShouldBeFalse();
    }

    [Fact]
    public async Task GetTeamAreaPathsAsync_SingleValue_ReturnsSinglePath()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/teamfieldvalues",
            """{"defaultValue":"TestProject\\TeamX","values":[{"value":"TestProject\\TeamX","includeChildren":true}]}""");
        var service = CreateService(handler);

        var result = await service.GetTeamAreaPathsAsync();

        result.Count.ShouldBe(1);
        result[0].Path.ShouldBe("TestProject\\TeamX");
        result[0].IncludeChildren.ShouldBeTrue();
    }

    [Fact]
    public async Task GetTeamAreaPathsAsync_EmptyValues_FallsBackToDefaultValue()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/teamfieldvalues",
            """{"defaultValue":"TestProject\\Default","values":[]}""");
        var service = CreateService(handler);

        var result = await service.GetTeamAreaPathsAsync();

        result.Count.ShouldBe(1);
        result[0].Path.ShouldBe("TestProject\\Default");
        result[0].IncludeChildren.ShouldBeTrue();
    }

    [Fact]
    public async Task GetTeamAreaPathsAsync_NullValues_FallsBackToDefaultValue()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/teamfieldvalues",
            """{"defaultValue":"TestProject\\Default","values":null}""");
        var service = CreateService(handler);

        var result = await service.GetTeamAreaPathsAsync();

        result.Count.ShouldBe(1);
        result[0].Path.ShouldBe("TestProject\\Default");
        result[0].IncludeChildren.ShouldBeTrue();
    }

    [Fact]
    public async Task GetTeamAreaPathsAsync_NullValuesAndNullDefault_ReturnsEmpty()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/teamfieldvalues",
            """{"defaultValue":null,"values":null}""");
        var service = CreateService(handler);

        var result = await service.GetTeamAreaPathsAsync();

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTeamAreaPathsAsync_SkipsNullValueEntries()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/teamfieldvalues",
            """{"defaultValue":"TestProject","values":[{"value":"TestProject\\TeamA","includeChildren":true},{"value":null,"includeChildren":false},{"value":"TestProject\\TeamC","includeChildren":true}]}""");
        var service = CreateService(handler);

        var result = await service.GetTeamAreaPathsAsync();

        result.Count.ShouldBe(2);
        result[0].Path.ShouldBe("TestProject\\TeamA");
        result[0].IncludeChildren.ShouldBeTrue();
        result[1].Path.ShouldBe("TestProject\\TeamC");
        result[1].IncludeChildren.ShouldBeTrue();
    }

    [Fact]
    public async Task GetTeamAreaPathsAsync_IncludeChildrenFalse_PreservesFlag()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/teamfieldvalues",
            """{"defaultValue":"TestProject","values":[{"value":"TestProject\\Exact","includeChildren":false}]}""");
        var service = CreateService(handler);

        var result = await service.GetTeamAreaPathsAsync();

        result.Count.ShouldBe(1);
        result[0].Path.ShouldBe("TestProject\\Exact");
        result[0].IncludeChildren.ShouldBeFalse();
    }

    // ── GetAuthenticatedUserDisplayNameAsync ──────────────────────────

    [Fact]
    public async Task GetAuthenticatedUserDisplayNameAsync_ReturnsProviderDisplayName()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/profile/profiles/me",
            """{"displayName":"Alice Smith","emailAddress":"alice@example.com","id":"guid-abc"}""");
        var service = CreateService(handler);

        var result = await service.GetAuthenticatedUserDisplayNameAsync();

        result.ShouldBe("Alice Smith");
    }

    [Fact]
    public async Task GetAuthenticatedUserDisplayNameAsync_NullUser_ReturnsNull()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/profile/profiles/me",
            """{"displayName":null}""");
        var service = CreateService(handler);

        var result = await service.GetAuthenticatedUserDisplayNameAsync();

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAuthenticatedUserDisplayNameAsync_HttpError_ReturnsNull()
    {
        // FakeHandler returns 404 for unknown endpoints — exercises the catch-all fallback
        var handler = new FakeHandler();
        var service = CreateService(handler);

        var result = await service.GetAuthenticatedUserDisplayNameAsync();

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAuthenticatedUserDisplayNameAsync_OperationCanceled_Propagates()
    {
        var handler = new CancelingHandler();
        var service = CreateService(handler);

        await Should.ThrowAsync<OperationCanceledException>(
            () => service.GetAuthenticatedUserDisplayNameAsync());
    }

    // ── GetWorkItemTypesWithStatesAsync ────────────────────────────

    [Fact]
    public async Task GetWorkItemTypesWithStatesAsync_SortsStatesByCategory()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponseWithStates(
            ("UserStory", "AABBCC", "icon_story", false, new[]
            {
                ("Active", "InProgress"),
                ("Draft", "Proposed"),
                ("Review", "InProgress"),
                ("Done", "Completed"),
            }));
        var service = CreateService(handler);

        var result = await service.GetWorkItemTypesWithStatesAsync();

        result.Count.ShouldBe(1);
        var states = result[0].States.Select(s => s.Name).ToArray();
        // Proposed first, then InProgress (original order preserved within category), then Completed
        states.ShouldBe(new[] { "Draft", "Active", "Review", "Done" });
    }

    [Fact]
    public async Task GetWorkItemTypesWithStatesAsync_DisabledTypesExcluded()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponseWithStates(
            ("Epic", "FF0000", "icon_epic", false, new[] { ("New", "Proposed") }),
            ("DisabledType", "AAAAAA", "icon_x", true, new[] { ("Active", "InProgress") }));
        var service = CreateService(handler);

        var result = await service.GetWorkItemTypesWithStatesAsync();

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("Epic");
    }

    [Fact]
    public async Task GetWorkItemTypesWithStatesAsync_NullColorTypeRetained()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponseWithStates(
            ("CustomType", null, null, false, new[] { ("Draft", "Proposed"), ("Done", "Completed") }));
        var service = CreateService(handler);

        var result = await service.GetWorkItemTypesWithStatesAsync();

        // Null-color types are retained (unlike GetWorkItemTypeAppearancesAsync which excludes them)
        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("CustomType");
        result[0].Color.ShouldBeNull();
    }

    [Fact]
    public async Task GetWorkItemTypesWithStatesAsync_EmptyResponse_ReturnsEmpty()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponse(); // no types
        var service = CreateService(handler);

        var result = await service.GetWorkItemTypesWithStatesAsync();

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetWorkItemTypesWithStatesAsync_RemovedCategoryLast()
    {
        var handler = new FakeHandler();
        handler.SetWorkItemTypesResponseWithStates(
            ("Task", "F2CB1D", "icon_task", false, new[]
            {
                ("Removed", "Removed"),
                ("Active", "InProgress"),
                ("New", "Proposed"),
            }));
        var service = CreateService(handler);

        var result = await service.GetWorkItemTypesWithStatesAsync();

        var states = result[0].States.Select(s => s.Name).ToArray();
        // Proposed first, InProgress second, Removed last
        states.ShouldBe(new[] { "New", "Active", "Removed" });
    }

    // ── GetProcessConfigurationAsync ──────────────────────────────

    [Fact]
    public async Task GetProcessConfigurationAsync_MapsPortfolioAndRequirementAndTask()
    {
        var handler = new FakeHandler();
        handler.SetProcessConfigurationResponse("""
            {
              "portfolioBacklogs": [
                { "name": "Epics", "referenceName": "Microsoft.EpicCategory", "workItemTypes": [{"name": "Epic", "url": "..."}] },
                { "name": "Features", "referenceName": "Microsoft.FeatureCategory", "workItemTypes": [{"name": "Feature", "url": "..."}] }
              ],
              "requirementBacklog": { "name": "Stories", "referenceName": "Microsoft.RequirementCategory", "workItemTypes": [{"name": "User Story", "url": "..."}] },
              "taskBacklog": { "name": "Tasks", "referenceName": "Microsoft.TaskCategory", "workItemTypes": [{"name": "Task", "url": "..."}] },
              "bugWorkItems": { "name": "Bugs", "referenceName": "Microsoft.BugCategory", "workItemTypes": [{"name": "Bug", "url": "..."}] }
            }
            """);
        var service = CreateService(handler);

        var result = await service.GetProcessConfigurationAsync();

        result.PortfolioBacklogs.Count.ShouldBe(2);
        result.PortfolioBacklogs[0].Name.ShouldBe("Epics");
        result.PortfolioBacklogs[0].WorkItemTypeNames.ShouldBe(new[] { "Epic" });
        result.PortfolioBacklogs[1].Name.ShouldBe("Features");
        result.RequirementBacklog.ShouldNotBeNull();
        result.RequirementBacklog!.WorkItemTypeNames.ShouldBe(new[] { "User Story" });
        result.TaskBacklog.ShouldNotBeNull();
        result.TaskBacklog!.WorkItemTypeNames.ShouldBe(new[] { "Task" });
        result.BugWorkItems.ShouldNotBeNull();
        result.BugWorkItems!.WorkItemTypeNames.ShouldBe(new[] { "Bug" });
    }

    [Fact]
    public async Task GetProcessConfigurationAsync_404_ReturnsEmptyData()
    {
        // FakeHandler returns 404 for unknown endpoints
        var handler = new FakeHandler();
        var service = CreateService(handler);

        // Should not throw — gracefully returns empty
        var result = await service.GetProcessConfigurationAsync();

        result.ShouldNotBeNull();
        result.PortfolioBacklogs.ShouldBeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoIterationService CreateService(FakeHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var auth = new FakeAuthProvider();
        return new AdoIterationService(http, auth, OrgUrl, Project, Team);
    }

    private static AdoIterationService CreateService(CancelingHandler handler)
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
    /// Fake HttpMessageHandler that returns canned JSON responses for iteration and work item type endpoints.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);

        public void SetWorkItemTypesResponse(params string[] typeNames)
        {
            var types = typeNames.Select(n =>
                $"{{\"name\":\"{n}\",\"description\":\"\",\"referenceName\":\"System.{n.Replace(" ", "")}\",\"color\":\"AABBCC\",\"icon\":{{\"id\":\"icon_test\",\"url\":\"https://example.com\"}},\"isDisabled\":false}}");
            var json = $"{{\"count\":{typeNames.Length},\"value\":[{string.Join(',', types)}]}}";
            _responses["/_apis/wit/workitemtypes"] = json;
        }

        public void SetWorkItemTypesResponseDetailed(params (string name, string? color, string? iconId, bool isDisabled)[] types)
        {
            var typeJsons = types.Select(t =>
            {
                var colorPart = t.color is not null ? $"\"color\":\"{t.color}\"" : "\"color\":null";
                var iconPart = t.iconId is not null
                    ? $"\"icon\":{{\"id\":\"{t.iconId}\",\"url\":\"https://example.com\"}}"
                    : "\"icon\":null";
                return $"{{\"name\":\"{t.name}\",\"description\":\"\",\"referenceName\":\"System.{t.name.Replace(" ", "")}\",{colorPart},{iconPart},\"isDisabled\":{t.isDisabled.ToString().ToLowerInvariant()}}}";
            });
            var json = $"{{\"count\":{types.Length},\"value\":[{string.Join(',', typeJsons)}]}}";
            _responses["/_apis/wit/workitemtypes"] = json;
        }

        public void SetIterationResponse(string iterationPath)
        {
            var escapedPath = iterationPath.Replace(@"\", @"\\");
            var json = $"{{\"count\":1,\"value\":[{{\"id\":\"guid-1\",\"name\":\"Sprint 1\",\"path\":\"{escapedPath}\",\"attributes\":{{\"startDate\":\"2026-01-01\",\"finishDate\":\"2026-01-14\",\"timeFrame\":\"current\"}}}}]}}";
            _responses["/_apis/work/teamsettings/iterations"] = json;
        }

        public void SetRawResponse(string urlFragment, string json)
        {
            _responses[urlFragment] = json;
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

        public void SetProcessConfigurationResponse(string json)
        {
            _responses["/_apis/work/processconfiguration"] = json;
        }

        public void SetProjectCapabilitiesResponse(string templateName)
        {
            var json = $"{{\"capabilities\":{{\"processTemplate\":{{\"templateName\":\"{templateName}\"}}}}}}";
            _responses["/_apis/projects/"] = json;
        }
    }

    /// <summary>
    /// HttpMessageHandler that always throws <see cref="OperationCanceledException"/>.
    /// Used to verify OCE propagation in methods that catch generic exceptions.
    /// </summary>
    private sealed class CancelingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new OperationCanceledException("Simulated cancellation");
        }
    }
}
