using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class FlowTransitionServiceTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly ProtectedCacheWriter _protectedCacheWriter;
    private readonly FlowTransitionService _service;

    public FlowTransitionServiceTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();

        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);

        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Agile());

        _service = new FlowTransitionService(
            _activeItemResolver, _adoService, _processConfigProvider, _protectedCacheWriter);
    }

    // ── ResolveItemAsync tests ──────────────────────────────────────

    [Fact]
    public async Task ResolveItem_ExplicitId_ReturnsItem()
    {
        var item = new WorkItemBuilder(42, "Test Item").AsUserStory().InState("Active").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _service.ResolveItemAsync(42);

        result.IsSuccess.ShouldBeTrue();
        result.Item!.Id.ShouldBe(42);
        result.IsExplicitId.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveItem_ActiveContext_ReturnsItem()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = new WorkItemBuilder(1, "Active Item").AsUserStory().InState("Active").Build();
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _service.ResolveItemAsync(null);

        result.IsSuccess.ShouldBeTrue();
        result.Item!.Id.ShouldBe(1);
        result.IsExplicitId.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveItem_NoActiveContext_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _service.ResolveItemAsync(null);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("No active work item");
    }

    [Fact]
    public async Task ResolveItem_ItemNotInCacheAndFetchFails_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Not found")));

        var result = await _service.ResolveItemAsync(99);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("#99");
    }

    // ── TransitionStateAsync tests ──────────────────────────────────

    [Fact]
    public async Task TransitionState_ActiveToResolved_Transitions()
    {
        var item = new WorkItemBuilder(1, "Story").AsUserStory().InState("Active").Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var result = await _service.TransitionStateAsync(item, StateCategory.Resolved);

        result.Transitioned.ShouldBeTrue();
        result.OriginalState.ShouldBe("Active");
        result.NewState.ShouldBe("Resolved");
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State" && f.NewValue == "Resolved")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionState_AlreadyResolved_SkipsTransition()
    {
        var item = new WorkItemBuilder(1, "Story").AsUserStory().InState("Resolved").Build();

        var result = await _service.TransitionStateAsync(item, StateCategory.Resolved);

        result.Transitioned.ShouldBeFalse();
        result.AlreadyInTargetCategory.ShouldBeTrue();
        result.OriginalState.ShouldBe("Resolved");
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransitionState_AlreadyCompleted_SkipsTransition()
    {
        var item = new WorkItemBuilder(1, "Story").AsUserStory().InState("Closed").Build();

        var result = await _service.TransitionStateAsync(item, StateCategory.Resolved, StateCategory.Completed);

        result.Transitioned.ShouldBeFalse();
        result.AlreadyInTargetCategory.ShouldBeTrue();
    }

    [Fact]
    public async Task TransitionState_NoResolvedCategory_FallsBackToCompleted()
    {
        // Task type has no Resolved state — should fall back to Completed ("Closed")
        var item = new WorkItemBuilder(1, "A task").AsTask().InState("Active").Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var result = await _service.TransitionStateAsync(item, StateCategory.Resolved, StateCategory.Completed);

        result.Transitioned.ShouldBeTrue();
        result.NewState.ShouldBe("Closed");
    }

    [Fact]
    public async Task TransitionState_ToCompleted_TransitionsDirectly()
    {
        var item = new WorkItemBuilder(1, "Feature").AsUserStory().InState("Resolved").Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var result = await _service.TransitionStateAsync(item, StateCategory.Completed);

        result.Transitioned.ShouldBeTrue();
        result.NewState.ShouldBe("Closed");
    }

    [Fact]
    public async Task TransitionState_UnknownType_DoesNotTransition()
    {
        // Use a type that's not in the Agile process config
        var item = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Parse("Custom Type").Value,
            Title = "Custom",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = await _service.TransitionStateAsync(item, StateCategory.Resolved);

        result.Transitioned.ShouldBeFalse();
        result.AlreadyInTargetCategory.ShouldBeFalse();
    }

    [Fact]
    public async Task TransitionState_SavesItemThroughProtectedCacheWriter()
    {
        var item = new WorkItemBuilder(1, "Story").AsUserStory().InState("Active").Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        await _service.TransitionStateAsync(item, StateCategory.Resolved);

        // SaveProtectedAsync is called (through ProtectedCacheWriter → IWorkItemRepository)
        await _workItemRepo.Received().SaveAsync(Arg.Is<WorkItem>(w => w.Id == 1), Arg.Any<CancellationToken>());
    }
}
