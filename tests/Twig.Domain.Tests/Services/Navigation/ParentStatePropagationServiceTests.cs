using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Navigation;

public class ParentStatePropagationServiceTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly ProtectedCacheWriter _protectedCacheWriter;
    private readonly ParentStatePropagationService _service;

    public ParentStatePropagationServiceTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);

        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Basic());

        // Default: no dirty items (for ProtectedCacheWriter)
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        _service = new ParentStatePropagationService(
            _workItemRepo, _adoService, _processConfigProvider, _protectedCacheWriter);
    }

    // ── NotApplicable ──────────────────────────────────────────────

    [Theory]
    [InlineData(StateCategory.Proposed)]
    [InlineData(StateCategory.Resolved)]
    [InlineData(StateCategory.Completed)]
    [InlineData(StateCategory.Removed)]
    [InlineData(StateCategory.Unknown)]
    public async Task NotApplicable_WhenChildCategoryIsNotInProgress(StateCategory category)
    {
        var child = new WorkItemBuilder(1, "Child Task").AsTask().InState("To Do").WithParent(100).Build();

        var result = await _service.TryPropagateToParentAsync(child, category);

        result.Outcome.ShouldBe(ParentPropagationOutcome.NotApplicable);
    }

    // ── NoParent ───────────────────────────────────────────────────

    [Fact]
    public async Task NoParent_WhenChildHasNoParentId()
    {
        var child = new WorkItemBuilder(1, "Orphan Task").AsTask().InState("Doing").Build();

        var result = await _service.TryPropagateToParentAsync(child, StateCategory.InProgress);

        result.Outcome.ShouldBe(ParentPropagationOutcome.NoParent);
    }

    // ── AlreadyActive ──────────────────────────────────────────────

    [Fact]
    public async Task AlreadyActive_WhenParentInCacheIsAlreadyInProgress()
    {
        var child = new WorkItemBuilder(1, "Child Task").AsTask().InState("Doing").WithParent(100).Build();
        var parent = new WorkItemBuilder(100, "Parent Issue").AsIssue().InState("Doing").Build();

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _service.TryPropagateToParentAsync(child, StateCategory.InProgress);

        result.Outcome.ShouldBe(ParentPropagationOutcome.AlreadyActive);
        result.ParentId.ShouldBe(100);
        result.ParentOldState.ShouldBe("Doing");
    }

    [Fact]
    public async Task AlreadyActive_WhenParentIsCompleted()
    {
        var child = new WorkItemBuilder(1, "Child Task").AsTask().InState("Doing").WithParent(100).Build();
        var parent = new WorkItemBuilder(100, "Parent Issue").AsIssue().InState("Done").Build();

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _service.TryPropagateToParentAsync(child, StateCategory.InProgress);

        result.Outcome.ShouldBe(ParentPropagationOutcome.AlreadyActive);
        result.ParentId.ShouldBe(100);
        result.ParentOldState.ShouldBe("Done");
    }

    // ── Cache-miss + ADO fetch failure ─────────────────────────────

    [Fact]
    public async Task Failed_WhenParentNotInCacheAndAdoFetchFails()
    {
        var child = new WorkItemBuilder(1, "Child Task").AsTask().InState("Doing").WithParent(100).Build();

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network error"));

        var result = await _service.TryPropagateToParentAsync(child, StateCategory.InProgress);

        result.Outcome.ShouldBe(ParentPropagationOutcome.Failed);
        result.ParentId.ShouldBe(100);
        result.Error!.ShouldContain("network error");
    }

    // ── Parent type not in process config ──────────────────────────

    [Fact]
    public async Task Failed_WhenParentTypeNotInProcessConfig()
    {
        var child = new WorkItemBuilder(1, "Child Task").AsTask().InState("Doing").WithParent(100).Build();
        // Use a type that's not in Basic config (Feature is not in Basic — Basic has Epic, Issue, Task)
        var parent = new WorkItemBuilder(100, "Parent Feature").AsFeature().InState("New").Build();

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _service.TryPropagateToParentAsync(child, StateCategory.InProgress);

        result.Outcome.ShouldBe(ParentPropagationOutcome.Failed);
        result.ParentId.ShouldBe(100);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("not found in process configuration");
    }

    // ── Happy path: Propagated ─────────────────────────────────────

    [Fact]
    public async Task Propagated_WhenParentInProposedAndChildMovesToInProgress()
    {
        var child = new WorkItemBuilder(1, "Child Task").AsTask().InState("Doing").WithParent(100).Build();
        var parent = new WorkItemBuilder(100, "Parent Issue").AsIssue().InState("To Do").Build();
        parent.MarkSynced(5);

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.PatchAsync(100, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);

        var result = await _service.TryPropagateToParentAsync(child, StateCategory.InProgress);

        result.Outcome.ShouldBe(ParentPropagationOutcome.Propagated);
        result.ParentId.ShouldBe(100);
        result.ParentOldState.ShouldBe("To Do");
        result.ParentNewState.ShouldBe("Doing");

        // Verify ADO patch was called with correct state change
        await _adoService.Received(1).PatchAsync(
            100,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == "System.State" &&
                c[0].OldValue == "To Do" &&
                c[0].NewValue == "Doing"),
            5,
            Arg.Any<CancellationToken>());

        // Verify cache was updated
        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == 100),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Propagated_WhenParentNotInCacheButFetchedFromAdo()
    {
        var child = new WorkItemBuilder(1, "Child Task").AsTask().InState("Doing").WithParent(100).Build();
        var parent = new WorkItemBuilder(100, "Parent Issue").AsIssue().InState("To Do").Build();
        parent.MarkSynced(3);

        // Parent not in cache — will be fetched from ADO
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.PatchAsync(100, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .Returns(4);

        var result = await _service.TryPropagateToParentAsync(child, StateCategory.InProgress);

        result.Outcome.ShouldBe(ParentPropagationOutcome.Propagated);
        result.ParentId.ShouldBe(100);
        result.ParentOldState.ShouldBe("To Do");
        result.ParentNewState.ShouldBe("Doing");
    }

    // ── PatchAsync failure → Failed ────────────────────────────────

    [Fact]
    public async Task Failed_WhenPatchAsyncThrows()
    {
        var child = new WorkItemBuilder(1, "Child Task").AsTask().InState("Doing").WithParent(100).Build();
        var parent = new WorkItemBuilder(100, "Parent Issue").AsIssue().InState("To Do").Build();
        parent.MarkSynced(5);

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.PatchAsync(100, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("409 Conflict"));

        var result = await _service.TryPropagateToParentAsync(child, StateCategory.InProgress);

        result.Outcome.ShouldBe(ParentPropagationOutcome.Failed);
        result.ParentId.ShouldBe(100);
        result.Error!.ShouldContain("409 Conflict");
    }

    // ── OperationCanceledException is not swallowed ────────────────

    [Fact]
    public async Task OperationCanceledException_IsNotSwallowed()
    {
        var child = new WorkItemBuilder(1, "Child Task").AsTask().InState("Doing").WithParent(100).Build();

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _service.TryPropagateToParentAsync(child, StateCategory.InProgress));
    }

    // ── Propagated with Agile config (Epic hierarchy) ──────────────

    [Fact]
    public async Task Propagated_AgileConfig_EpicParent()
    {
        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Agile());

        var child = new WorkItemBuilder(10, "Feature X").AsFeature().InState("Active").WithParent(1).Build();
        var parent = new WorkItemBuilder(1, "Epic A").AsEpic().InState("New").Build();
        parent.MarkSynced(2);

        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 2, Arg.Any<CancellationToken>())
            .Returns(3);

        var result = await _service.TryPropagateToParentAsync(child, StateCategory.InProgress);

        result.Outcome.ShouldBe(ParentPropagationOutcome.Propagated);
        result.ParentId.ShouldBe(1);
        result.ParentOldState.ShouldBe("New");
        result.ParentNewState.ShouldBe("Active");
    }
}
