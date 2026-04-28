using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

public class WorkingSetServiceTests
{
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();
    private readonly IIterationService _iterationService = Substitute.For<IIterationService>();
    private readonly ITrackingRepository _trackingRepo = Substitute.For<ITrackingRepository>();

    private static readonly IterationPath TestIteration = IterationPath.Parse(@"Project\Sprint1").Value;

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private WorkingSetService CreateSut(string? userDisplayName = null, bool withTracking = true)
        => new(_contextStore, _workItemRepo, _pendingStore, _iterationService, userDisplayName,
            withTracking ? _trackingRepo : null);

    private void SetupDefaults(int? activeId = null)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(activeId);
        _workItemRepo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetByIterationAndAssigneeAsync(Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _pendingStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>()).Returns(TestIteration);
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<TrackedItem>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Correct membership for each category
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ComputeAsync_ActiveItem_IncludedInAllIds()
    {
        SetupDefaults(activeId: 42);
        var sut = CreateSut();

        var ws = await sut.ComputeAsync(TestIteration);

        ws.ActiveItemId.ShouldBe(42);
        ws.AllIds.ShouldContain(42);
    }

    [Fact]
    public async Task ComputeAsync_ParentChain_IncludedInAllIds()
    {
        SetupDefaults(activeId: 10);
        _workItemRepo.GetParentChainAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WorkItemBuilder(1, "Item 1").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
                new WorkItemBuilder(5, "Item 5").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
                new WorkItemBuilder(10, "Item 10").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
            });
        var sut = CreateSut();

        var ws = await sut.ComputeAsync(TestIteration);

        ws.ParentChainIds.ShouldBe(new[] { 1, 5, 10 });
        ws.AllIds.ShouldContain(1);
        ws.AllIds.ShouldContain(5);
        ws.AllIds.ShouldContain(10);
    }

    [Fact]
    public async Task ComputeAsync_Children_IncludedInAllIds()
    {
        SetupDefaults(activeId: 10);
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WorkItemBuilder(20, "Item 20").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
                new WorkItemBuilder(30, "Item 30").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
            });
        var sut = CreateSut();

        var ws = await sut.ComputeAsync(TestIteration);

        ws.ChildrenIds.ShouldBe(new[] { 20, 30 });
        ws.AllIds.ShouldContain(20);
        ws.AllIds.ShouldContain(30);
    }

    [Fact]
    public async Task ComputeAsync_SprintItems_IncludedInAllIds()
    {
        SetupDefaults(activeId: 10);
        _workItemRepo.GetByIterationAsync(TestIteration, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WorkItemBuilder(50, "Item 50").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
                new WorkItemBuilder(60, "Item 60").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
            });
        var sut = CreateSut();

        var ws = await sut.ComputeAsync(TestIteration);

        ws.SprintItemIds.ShouldBe(new[] { 50, 60 });
        ws.AllIds.ShouldContain(50);
        ws.AllIds.ShouldContain(60);
    }

    [Fact]
    public async Task ComputeAsync_Seeds_IncludedInAllIds()
    {
        SetupDefaults(activeId: 10);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WorkItemBuilder(-1, "Item -1").InState("Active").AsSeed().WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
                new WorkItemBuilder(-2, "Item -2").InState("Active").AsSeed().WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
            });
        var sut = CreateSut();

        var ws = await sut.ComputeAsync(TestIteration);

        ws.SeedIds.ShouldBe(new[] { -1, -2 });
        ws.AllIds.ShouldContain(-1);
        ws.AllIds.ShouldContain(-2);
    }

    [Fact]
    public async Task ComputeAsync_DirtyItems_IncludedInAllIds()
    {
        SetupDefaults(activeId: 10);
        var dirtyItem = new WorkItemBuilder(99, "Item 99").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build();
        dirtyItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { dirtyItem });
        _pendingStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 100 });
        var sut = CreateSut();

        var ws = await sut.ComputeAsync(TestIteration);

        ws.DirtyItemIds.ShouldContain(99);
        ws.DirtyItemIds.ShouldContain(100);
        ws.AllIds.ShouldContain(99);
        ws.AllIds.ShouldContain(100);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty cache
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ComputeAsync_EmptyCache_ReturnsEmptyWorkingSet()
    {
        SetupDefaults(activeId: null);
        var sut = CreateSut();

        var ws = await sut.ComputeAsync(TestIteration);

        ws.ActiveItemId.ShouldBeNull();
        ws.ParentChainIds.ShouldBeEmpty();
        ws.ChildrenIds.ShouldBeEmpty();
        ws.SprintItemIds.ShouldBeEmpty();
        ws.SeedIds.ShouldBeEmpty();
        ws.DirtyItemIds.ShouldBeEmpty();
        ws.TrackedItemIds.ShouldBeEmpty();
        ws.AllIds.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  No active item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ComputeAsync_NoActiveItem_DoesNotQueryParentChainOrChildren()
    {
        SetupDefaults(activeId: null);
        var sut = CreateSut();

        await sut.ComputeAsync(TestIteration);

        await _workItemRepo.DidNotReceive().GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Missing parent chain (item not in cache)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ComputeAsync_MissingParentChain_ReturnsEmptyParentList()
    {
        SetupDefaults(activeId: 42);
        _workItemRepo.GetParentChainAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        var sut = CreateSut();

        var ws = await sut.ComputeAsync(TestIteration);

        ws.ParentChainIds.ShouldBeEmpty();
        ws.ActiveItemId.ShouldBe(42);
        ws.AllIds.ShouldContain(42); // active ID still in AllIds
    }

    // ═══════════════════════════════════════════════════════════════
    //  No sprint items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ComputeAsync_NoSprintItems_ReturnsEmptySprintList()
    {
        SetupDefaults(activeId: 10);
        var sut = CreateSut();

        var ws = await sut.ComputeAsync(TestIteration);

        ws.SprintItemIds.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Assignee filtering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ComputeAsync_WithUserDisplayName_FiltersSprintItemsByAssignee()
    {
        SetupDefaults(activeId: 10);
        _workItemRepo.GetByIterationAndAssigneeAsync(TestIteration, "Dan Green", Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(50, "Item 50").InState("Active").AssignedTo("Dan Green").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build() });
        var sut = CreateSut(userDisplayName: "Dan Green");

        var ws = await sut.ComputeAsync(TestIteration);

        ws.SprintItemIds.ShouldBe(new[] { 50 });
        await _workItemRepo.Received(1).GetByIterationAndAssigneeAsync(TestIteration, "Dan Green", Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ComputeAsync_WithoutUserDisplayName_QueriesAllSprintItems()
    {
        SetupDefaults(activeId: 10);
        _workItemRepo.GetByIterationAsync(TestIteration, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WorkItemBuilder(50, "Item 50").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
                new WorkItemBuilder(60, "Item 60").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
            });
        var sut = CreateSut(userDisplayName: null);

        var ws = await sut.ComputeAsync(TestIteration);

        ws.SprintItemIds.ShouldBe(new[] { 50, 60 });
        await _workItemRepo.Received(1).GetByIterationAsync(TestIteration, Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetByIterationAndAssigneeAsync(Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  IterationPath passthrough — no ADO call when provided
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ComputeAsync_IterationPathProvided_DoesNotCallGetCurrentIteration()
    {
        SetupDefaults(activeId: 10);
        var sut = CreateSut();

        var ws = await sut.ComputeAsync(TestIteration);

        ws.IterationPath.ShouldBe(TestIteration);
        await _iterationService.DidNotReceive().GetCurrentIterationAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ComputeAsync_NoIterationPath_CallsGetCurrentIteration()
    {
        SetupDefaults(activeId: 10);
        var sut = CreateSut();

        var ws = await sut.ComputeAsync();

        ws.IterationPath.ShouldBe(TestIteration);
        await _iterationService.Received(1).GetCurrentIterationAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  AllIds is the union of all categories
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ComputeAsync_AllIds_IsUnionOfAllCategories()
    {
        SetupDefaults(activeId: 1);
        _workItemRepo.GetParentChainAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WorkItemBuilder(100, "Item 100").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
                new WorkItemBuilder(1, "Item 1").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build(),
            });
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(10, "Item 10").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build() });
        _workItemRepo.GetByIterationAsync(TestIteration, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(50, "Item 50").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build() });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(-1, "Item -1").InState("Active").AsSeed().WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build() });
        var dirtyItem = new WorkItemBuilder(99, "Item 99").InState("Active").WithIterationPath(@"Project\Sprint1").WithAreaPath(@"Project\Area").Build();
        dirtyItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { dirtyItem });
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new TrackedItem(200, TrackingMode.Single, DateTimeOffset.UtcNow) });

        var sut = CreateSut();
        var ws = await sut.ComputeAsync(TestIteration);

        ws.AllIds.ShouldBe(new HashSet<int> { 1, 100, 10, 50, -1, 99, 200 }, ignoreOrder: true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  TrackedItemIds integration
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ComputeAsync_TrackedItems_IncludedInTrackedItemIds()
    {
        SetupDefaults(activeId: 10);
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new TrackedItem(300, TrackingMode.Single, DateTimeOffset.UtcNow),
                new TrackedItem(301, TrackingMode.Tree, DateTimeOffset.UtcNow),
            });
        var sut = CreateSut();

        var ws = await sut.ComputeAsync(TestIteration);

        ws.TrackedItemIds.ShouldBe(new[] { 300, 301 });
        ws.AllIds.ShouldContain(300);
        ws.AllIds.ShouldContain(301);
    }

    [Fact]
    public async Task ComputeAsync_NoTrackedItems_ReturnsEmptyTrackedItemIds()
    {
        SetupDefaults(activeId: 10);
        var sut = CreateSut();

        var ws = await sut.ComputeAsync(TestIteration);

        ws.TrackedItemIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task ComputeAsync_NoTrackingRepo_ReturnsEmptyTrackedItemIds()
    {
        SetupDefaults(activeId: 10);
        var sut = CreateSut(withTracking: false);

        var ws = await sut.ComputeAsync(TestIteration);

        ws.TrackedItemIds.ShouldBeEmpty();
    }
}
