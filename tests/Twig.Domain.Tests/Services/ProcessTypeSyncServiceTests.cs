using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class ProcessTypeSyncServiceTests
{
    private readonly IIterationService _iterationService = Substitute.For<IIterationService>();
    private readonly IProcessTypeStore _processTypeStore = Substitute.For<IProcessTypeStore>();

    [Fact]
    public async Task SyncAsync_CallsBothFetchMethods()
    {
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemTypeWithStates>());
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData());

        await ProcessTypeSyncService.SyncAsync(_iterationService, _processTypeStore);

        await _iterationService.Received(1).GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>());
        await _iterationService.Received(1).GetProcessConfigurationAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ReturnsCountOfTypesSynced()
    {
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeWithStates>
            {
                new() { Name = "Bug", States = [] },
                new() { Name = "Task", States = [] },
                new() { Name = "User Story", States = [] },
            });
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData());

        var count = await ProcessTypeSyncService.SyncAsync(_iterationService, _processTypeStore);

        count.ShouldBe(3);
    }

    [Fact]
    public async Task SyncAsync_PersistsCorrectProcessTypeRecords()
    {
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeWithStates>
            {
                new()
                {
                    Name = "User Story",
                    Color = "009CCC",
                    IconId = "icon_book",
                    States =
                    [
                        new() { Name = "New", Category = "Proposed", Color = "b2b2b2" },
                        new() { Name = "Active", Category = "InProgress", Color = "007acc" },
                        new() { Name = "Closed", Category = "Completed", Color = "339933" },
                    ]
                },
                new()
                {
                    Name = "Task",
                    Color = "F2CB1D",
                    IconId = "icon_clipboard",
                    States =
                    [
                        new() { Name = "To Do", Category = "Proposed" },
                        new() { Name = "In Progress", Category = "InProgress" },
                        new() { Name = "Done", Category = "Completed" },
                    ]
                },
            });
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData
            {
                RequirementBacklog = new BacklogLevelConfiguration
                {
                    Name = "Stories",
                    WorkItemTypeNames = new[] { "User Story" }
                },
                TaskBacklog = new BacklogLevelConfiguration
                {
                    Name = "Tasks",
                    WorkItemTypeNames = new[] { "Task" }
                },
            });

        await ProcessTypeSyncService.SyncAsync(_iterationService, _processTypeStore);

        // Verify SaveAsync was called twice (one per type)
        await _processTypeStore.Received(2).SaveAsync(Arg.Any<ProcessTypeRecord>(), Arg.Any<CancellationToken>());

        // Verify User Story record has correct child types (from InferParentChildMap)
        await _processTypeStore.Received(1).SaveAsync(
            Arg.Is<ProcessTypeRecord>(r =>
                r.TypeName == "User Story" &&
                r.DefaultChildType == "Task" &&
                r.ValidChildTypes.Count == 1 &&
                r.ValidChildTypes[0] == "Task" &&
                r.ColorHex == "009CCC" &&
                r.IconId == "icon_book" &&
                r.States.Count == 3 &&
                r.States[0].Name == "New" &&
                r.States[0].Category == StateCategory.Proposed &&
                r.States[1].Name == "Active" &&
                r.States[1].Category == StateCategory.InProgress &&
                r.States[2].Name == "Closed" &&
                r.States[2].Category == StateCategory.Completed),
            Arg.Any<CancellationToken>());

        // Verify Task record has no children (leaf level)
        await _processTypeStore.Received(1).SaveAsync(
            Arg.Is<ProcessTypeRecord>(r =>
                r.TypeName == "Task" &&
                r.DefaultChildType == null &&
                r.ValidChildTypes.Count == 0 &&
                r.ColorHex == "F2CB1D" &&
                r.IconId == "icon_clipboard"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ZeroTypes_ReturnsZeroAndDoesNotCallSave()
    {
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemTypeWithStates>());
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData());

        var count = await ProcessTypeSyncService.SyncAsync(_iterationService, _processTypeStore);

        count.ShouldBe(0);
        await _processTypeStore.DidNotReceive().SaveAsync(Arg.Any<ProcessTypeRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_NullTypesResponse_ReturnsZero()
    {
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<WorkItemTypeWithStates>>(null!));
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData());

        var count = await ProcessTypeSyncService.SyncAsync(_iterationService, _processTypeStore);

        count.ShouldBe(0);
    }

    [Fact]
    public async Task SyncAsync_NullProcessConfigResponse_StillSyncsWithEmptyHierarchy()
    {
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeWithStates>
            {
                new() { Name = "Bug", States = [] },
            });
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ProcessConfigurationData>(null!));

        var count = await ProcessTypeSyncService.SyncAsync(_iterationService, _processTypeStore);

        count.ShouldBe(1);
        await _processTypeStore.Received(1).SaveAsync(
            Arg.Is<ProcessTypeRecord>(r =>
                r.TypeName == "Bug" &&
                r.DefaultChildType == null &&
                r.ValidChildTypes.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ExceptionFromGetWorkItemTypes_PropagatesUncaught()
    {
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("API unavailable"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => ProcessTypeSyncService.SyncAsync(_iterationService, _processTypeStore));
    }

    [Fact]
    public async Task SyncAsync_ExceptionFromGetProcessConfiguration_PropagatesUncaught()
    {
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeWithStates> { new() { Name = "Bug", States = [] } });
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Config unavailable"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => ProcessTypeSyncService.SyncAsync(_iterationService, _processTypeStore));
    }

    [Fact]
    public async Task SyncAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemTypeWithStates>());
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData());

        await ProcessTypeSyncService.SyncAsync(_iterationService, _processTypeStore, cts.Token);

        await _iterationService.Received(1).GetWorkItemTypesWithStatesAsync(cts.Token);
        await _iterationService.Received(1).GetProcessConfigurationAsync(cts.Token);
    }

    [Fact]
    public async Task SyncAsync_PersistsProcessConfigurationData()
    {
        var processConfig = new ProcessConfigurationData
        {
            RequirementBacklog = new BacklogLevelConfiguration
            {
                Name = "Stories",
                WorkItemTypeNames = new[] { "User Story" }
            },
            TaskBacklog = new BacklogLevelConfiguration
            {
                Name = "Tasks",
                WorkItemTypeNames = new[] { "Task" }
            },
            PortfolioBacklogs = new List<BacklogLevelConfiguration>
            {
                new() { Name = "Epics", WorkItemTypeNames = new[] { "Epic" } },
                new() { Name = "Features", WorkItemTypeNames = new[] { "Feature" } },
            },
        };

        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemTypeWithStates>());
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(processConfig);

        await ProcessTypeSyncService.SyncAsync(_iterationService, _processTypeStore);

        await _processTypeStore.Received(1).SaveProcessConfigurationDataAsync(
            Arg.Is<ProcessConfigurationData>(c =>
                c.RequirementBacklog != null &&
                c.RequirementBacklog.Name == "Stories" &&
                c.RequirementBacklog.WorkItemTypeNames.Count == 1 &&
                c.RequirementBacklog.WorkItemTypeNames[0] == "User Story" &&
                c.TaskBacklog != null &&
                c.TaskBacklog.Name == "Tasks" &&
                c.PortfolioBacklogs.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_NullProcessConfig_PersistsEmptyProcessConfigurationData()
    {
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemTypeWithStates>());
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ProcessConfigurationData>(null!));

        await ProcessTypeSyncService.SyncAsync(_iterationService, _processTypeStore);

        // Should still persist — the fallback empty ProcessConfigurationData is saved
        await _processTypeStore.Received(1).SaveProcessConfigurationDataAsync(
            Arg.Any<ProcessConfigurationData>(),
            Arg.Any<CancellationToken>());
    }
}
