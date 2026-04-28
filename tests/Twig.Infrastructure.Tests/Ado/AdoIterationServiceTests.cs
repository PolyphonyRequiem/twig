using System.Net;
using System.Text.Json;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoIterationService"/>.
/// Uses a fake HttpMessageHandler to verify process template detection heuristics
/// and current iteration error paths without making real network calls.
/// </summary>
public class AdoIterationServiceTests
{

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

    [Fact]
    public async Task DetectTemplateNameAsync_ApiThrowsHttpRequestException_FallsBackToHeuristic()
    {
        var handler = new NetworkErrorHandler("/_apis/projects/");
        handler.SetWorkItemTypesResponse("Epic", "Feature", "Product Backlog Item", "Task");
        var service = CreateService(handler);

        var result = await service.DetectTemplateNameAsync();

        result.ShouldBe("Scrum");
    }

    [Fact]
    public void AdoProjectWithCapabilitiesResponse_DeserializesFromCamelCaseJson()
    {
        var json = """{"capabilities":{"processTemplate":{"templateName":"MyCustomProcess"}}}"""u8;
        var dto = JsonSerializer.Deserialize(json, TwigJsonContext.Default.AdoProjectWithCapabilitiesResponse);

        dto.ShouldNotBeNull();
        dto.Capabilities.ShouldNotBeNull();
        dto.Capabilities!.ProcessTemplate.ShouldNotBeNull();
        dto.Capabilities.ProcessTemplate!.TemplateName.ShouldBe("MyCustomProcess");
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

    // ── GetTeamIterationsAsync ────────────────────────────────────

    [Fact]
    public async Task GetTeamIterationsAsync_ReturnsAllIterations()
    {
        var handler = new FakeHandler();
        handler.SetTeamIterationsResponse(
            (@"TestProject\Sprint 1", "2026-01-01T00:00:00Z", "2026-01-14T00:00:00Z"),
            (@"TestProject\Sprint 2", "2026-01-15T00:00:00Z", "2026-01-28T00:00:00Z"),
            (@"TestProject\Sprint 3", "2026-01-29T00:00:00Z", "2026-02-11T00:00:00Z"));
        var service = CreateService(handler);

        var result = await service.GetTeamIterationsAsync();

        result.Count.ShouldBe(3);
        result[0].Path.ShouldBe(@"TestProject\Sprint 1");
        result[1].Path.ShouldBe(@"TestProject\Sprint 2");
        result[2].Path.ShouldBe(@"TestProject\Sprint 3");
    }

    [Fact]
    public async Task GetTeamIterationsAsync_ParsesDatesCorrectly()
    {
        var handler = new FakeHandler();
        handler.SetTeamIterationsResponse(
            (@"TestProject\Sprint 1", "2026-01-01T00:00:00Z", "2026-01-14T00:00:00Z"));
        var service = CreateService(handler);

        var result = await service.GetTeamIterationsAsync();

        result.Count.ShouldBe(1);
        result[0].StartDate.ShouldNotBeNull();
        result[0].StartDate!.Value.Year.ShouldBe(2026);
        result[0].StartDate!.Value.Month.ShouldBe(1);
        result[0].StartDate!.Value.Day.ShouldBe(1);
        result[0].EndDate.ShouldNotBeNull();
        result[0].EndDate!.Value.Day.ShouldBe(14);
    }

    [Fact]
    public async Task GetTeamIterationsAsync_NullDates_ReturnsNullDateTimeOffsets()
    {
        var handler = new FakeHandler();
        handler.SetTeamIterationsResponse(
            (@"TestProject\Sprint 1", null, null));
        var service = CreateService(handler);

        var result = await service.GetTeamIterationsAsync();

        result.Count.ShouldBe(1);
        result[0].Path.ShouldBe(@"TestProject\Sprint 1");
        result[0].StartDate.ShouldBeNull();
        result[0].EndDate.ShouldBeNull();
    }

    [Fact]
    public async Task GetTeamIterationsAsync_EmptyResponse_ReturnsEmptyList()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/iterations", """{"count":0,"value":[]}""");
        var service = CreateService(handler);

        var result = await service.GetTeamIterationsAsync();

        result.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetTeamIterationsAsync_NullValueList_ReturnsEmptyList()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/iterations", """{"count":0,"value":null}""");
        var service = CreateService(handler);

        var result = await service.GetTeamIterationsAsync();

        result.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetTeamIterationsAsync_SkipsEntriesWithNullPath()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/iterations",
            """{"count":2,"value":[{"id":"g1","name":"Sprint 1","path":"TestProject\\Sprint 1","attributes":{"startDate":"2026-01-01","finishDate":"2026-01-14"}},{"id":"g2","name":"Sprint 2","path":null,"attributes":{"startDate":"2026-01-15","finishDate":"2026-01-28"}}]}""");
        var service = CreateService(handler);

        var result = await service.GetTeamIterationsAsync();

        result.Count.ShouldBe(1);
        result[0].Path.ShouldBe(@"TestProject\Sprint 1");
    }

    [Fact]
    public async Task GetTeamIterationsAsync_InvalidDateFormat_ReturnsNullDate()
    {
        var handler = new FakeHandler();
        handler.SetRawResponse("/_apis/work/teamsettings/iterations",
            """{"count":1,"value":[{"id":"g1","name":"Sprint 1","path":"TestProject\\Sprint 1","attributes":{"startDate":"not-a-date","finishDate":"also-invalid"}}]}""");
        var service = CreateService(handler);

        var result = await service.GetTeamIterationsAsync();

        result.Count.ShouldBe(1);
        result[0].StartDate.ShouldBeNull();
        result[0].EndDate.ShouldBeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoIterationService CreateService(HttpMessageHandler handler) =>
        FakeHandler.CreateService(handler);

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

    /// <summary>
    /// HttpMessageHandler that throws <see cref="HttpRequestException"/> for a specific URL
    /// and delegates all other requests to <see cref="FakeHandler"/>. Simulates network-level
    /// failures on a targeted endpoint while keeping remaining endpoints functional.
    /// </summary>
    private sealed class NetworkErrorHandler(string errorUrlFragment) : FakeHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.ToString().Contains(errorUrlFragment, StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("Simulated network failure");

            return base.SendAsync(request, cancellationToken);
        }
    }
}
