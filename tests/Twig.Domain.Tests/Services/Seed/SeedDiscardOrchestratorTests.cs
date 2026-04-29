using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Seed;

/// <summary>
/// Unit tests for <see cref="SeedDiscardOrchestrator"/>.
/// </summary>
public sealed class SeedDiscardOrchestratorTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly ISeedLinkRepository _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly SeedDiscardOrchestrator _orchestrator;

    public SeedDiscardOrchestratorTests()
    {
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem>());

        _orchestrator = new SeedDiscardOrchestrator(_workItemRepo, _seedLinkRepo, _contextStore);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Leaf seed (no children)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildDiscardPlanAsync_LeafSeed_PlanContainsOnlyTarget()
    {
        var seed = new WorkItemBuilder(-1, "Leaf seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { seed });

        var plan = await _orchestrator.BuildDiscardPlanAsync(-1);

        plan.ShouldNotBeNull();
        plan.TargetId.ShouldBe(-1);
        plan.TargetTitle.ShouldBe("Leaf seed");
        plan.AllIds.ShouldBe(new[] { -1 });
        plan.DescendantCount.ShouldBe(0);
        plan.HasDescendants.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent with direct children
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildDiscardPlanAsync_ParentWithTwoChildren_PlanContainsThreeIds()
    {
        var parent = new WorkItemBuilder(-1, "Parent").AsSeed().Build();
        var child1 = new WorkItemBuilder(-2, "Child 1").AsSeed().WithParent(-1).Build();
        var child2 = new WorkItemBuilder(-3, "Child 2").AsSeed().WithParent(-1).Build();

        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { parent, child1, child2 });

        var plan = await _orchestrator.BuildDiscardPlanAsync(-1);

        plan.ShouldNotBeNull();
        plan.AllIds.Count.ShouldBe(3);
        plan.AllIds.ShouldContain(-1);
        plan.AllIds.ShouldContain(-2);
        plan.AllIds.ShouldContain(-3);
        plan.DescendantCount.ShouldBe(2);
        plan.HasDescendants.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  3-level chain (grandchildren)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildDiscardPlanAsync_ThreeLevelChain_PlanContainsAllDescendants()
    {
        var root = new WorkItemBuilder(-1, "Root").AsSeed().Build();
        var child = new WorkItemBuilder(-2, "Child").AsSeed().WithParent(-1).Build();
        var grandchild = new WorkItemBuilder(-3, "Grandchild").AsSeed().WithParent(-2).Build();

        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(root);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { root, child, grandchild });

        var plan = await _orchestrator.BuildDiscardPlanAsync(-1);

        plan.ShouldNotBeNull();
        plan.AllIds.Count.ShouldBe(3);
        plan.AllIds.ShouldContain(-1);
        plan.AllIds.ShouldContain(-2);
        plan.AllIds.ShouldContain(-3);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Target not found → null
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildDiscardPlanAsync_TargetNotFound_ReturnsNull()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var plan = await _orchestrator.BuildDiscardPlanAsync(999);

        plan.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Target is not a seed → null
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildDiscardPlanAsync_TargetNotASeed_ReturnsNull()
    {
        var published = new WorkItemBuilder(100, "Published item").Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(published);

        var plan = await _orchestrator.BuildDiscardPlanAsync(100);

        plan.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Only seeds are traversed (published items with same parent ignored)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildDiscardPlanAsync_PublishedChildrenIgnored_OnlySeedsTraversed()
    {
        var parent = new WorkItemBuilder(-1, "Parent seed").AsSeed().Build();
        var seedChild = new WorkItemBuilder(-2, "Seed child").AsSeed().WithParent(-1).Build();
        // Published work item with same parent — should NOT be included
        var publishedChild = new WorkItemBuilder(200, "Published child").WithParent(-1).Build();

        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(parent);
        // GetSeedsAsync only returns seeds, not published items
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { parent, seedChild });

        var plan = await _orchestrator.BuildDiscardPlanAsync(-1);

        plan.ShouldNotBeNull();
        plan.AllIds.Count.ShouldBe(2);
        plan.AllIds.ShouldContain(-1);
        plan.AllIds.ShouldContain(-2);
        plan.AllIds.ShouldNotContain(200);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Discarding a mid-level node collects only its subtree
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildDiscardPlanAsync_MidLevelTarget_OnlyCollectsSubtree()
    {
        var root = new WorkItemBuilder(-1, "Root").AsSeed().Build();
        var mid = new WorkItemBuilder(-2, "Mid").AsSeed().WithParent(-1).Build();
        var leaf = new WorkItemBuilder(-3, "Leaf").AsSeed().WithParent(-2).Build();

        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(mid);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { root, mid, leaf });

        var plan = await _orchestrator.BuildDiscardPlanAsync(-2);

        plan.ShouldNotBeNull();
        plan.AllIds.Count.ShouldBe(2);
        plan.AllIds.ShouldContain(-2);
        plan.AllIds.ShouldContain(-3);
        plan.AllIds.ShouldNotContain(-1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Wide tree — parent with many children and grandchildren
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildDiscardPlanAsync_WideTree_CollectsAllBranches()
    {
        var root = new WorkItemBuilder(-1, "Root").AsSeed().Build();
        var child1 = new WorkItemBuilder(-2, "C1").AsSeed().WithParent(-1).Build();
        var child2 = new WorkItemBuilder(-3, "C2").AsSeed().WithParent(-1).Build();
        var grandchild1 = new WorkItemBuilder(-4, "GC1").AsSeed().WithParent(-2).Build();
        var grandchild2 = new WorkItemBuilder(-5, "GC2").AsSeed().WithParent(-3).Build();

        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(root);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { root, child1, child2, grandchild1, grandchild2 });

        var plan = await _orchestrator.BuildDiscardPlanAsync(-1);

        plan.ShouldNotBeNull();
        plan.AllIds.Count.ShouldBe(5);
        plan.DescendantCount.ShouldBe(4);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Seed with no parent (root) and no children
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task BuildDiscardPlanAsync_RootSeedNoChildren_PlanContainsOnlyTarget()
    {
        var root = new WorkItemBuilder(-1, "Orphan root").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(root);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { root });

        var plan = await _orchestrator.BuildDiscardPlanAsync(-1);

        plan.ShouldNotBeNull();
        plan.AllIds.ShouldBe(new[] { -1 });
        plan.HasDescendants.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExecuteDiscardAsync — single seed: links deleted, row deleted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteDiscardAsync_SingleSeed_DeletesLinksAndRow()
    {
        var plan = new SeedDiscardPlan
        {
            TargetId = -1,
            TargetTitle = "Single",
            AllIds = [-1],
        };

        await _orchestrator.ExecuteDiscardAsync(plan);

        await _seedLinkRepo.Received(1).DeleteLinksForItemAsync(-1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).DeleteByIdAsync(-1, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExecuteDiscardAsync — tree: all links and rows deleted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteDiscardAsync_TreeWithChildren_DeletesAllLinksAndRows()
    {
        var plan = new SeedDiscardPlan
        {
            TargetId = -1,
            TargetTitle = "Parent",
            AllIds = [-1, -2, -3],
        };

        await _orchestrator.ExecuteDiscardAsync(plan);

        await _seedLinkRepo.Received(1).DeleteLinksForItemAsync(-1, Arg.Any<CancellationToken>());
        await _seedLinkRepo.Received(1).DeleteLinksForItemAsync(-2, Arg.Any<CancellationToken>());
        await _seedLinkRepo.Received(1).DeleteLinksForItemAsync(-3, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).DeleteByIdAsync(-1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).DeleteByIdAsync(-2, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).DeleteByIdAsync(-3, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExecuteDiscardAsync — active context matches target → cleared
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteDiscardAsync_ActiveContextIsTarget_ClearsContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(-1);

        var plan = new SeedDiscardPlan
        {
            TargetId = -1,
            TargetTitle = "Target",
            AllIds = [-1],
        };

        await _orchestrator.ExecuteDiscardAsync(plan);

        await _contextStore.Received(1).ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExecuteDiscardAsync — active context matches descendant → cleared
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteDiscardAsync_ActiveContextIsDescendant_ClearsContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(-3);

        var plan = new SeedDiscardPlan
        {
            TargetId = -1,
            TargetTitle = "Parent",
            AllIds = [-1, -2, -3],
        };

        await _orchestrator.ExecuteDiscardAsync(plan);

        await _contextStore.Received(1).ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExecuteDiscardAsync — active context unrelated → NOT cleared
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteDiscardAsync_ActiveContextUnrelated_DoesNotClearContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(999);

        var plan = new SeedDiscardPlan
        {
            TargetId = -1,
            TargetTitle = "Target",
            AllIds = [-1],
        };

        await _orchestrator.ExecuteDiscardAsync(plan);

        await _contextStore.DidNotReceive().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExecuteDiscardAsync — no active context → NOT cleared
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteDiscardAsync_NoActiveContext_DoesNotClearContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var plan = new SeedDiscardPlan
        {
            TargetId = -1,
            TargetTitle = "Target",
            AllIds = [-1],
        };

        await _orchestrator.ExecuteDiscardAsync(plan);

        await _contextStore.DidNotReceive().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExecuteDiscardAsync — deletion order: links before rows
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteDiscardAsync_DeletesLinksBeforeRows()
    {
        var callOrder = new List<string>();
        _seedLinkRepo.DeleteLinksForItemAsync(-1, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("DeleteLinks:-1"));
        _workItemRepo.DeleteByIdAsync(-1, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("DeleteRow:-1"));

        var plan = new SeedDiscardPlan
        {
            TargetId = -1,
            TargetTitle = "Order Test",
            AllIds = [-1],
        };

        await _orchestrator.ExecuteDiscardAsync(plan);

        callOrder.Count.ShouldBe(2);
        callOrder[0].ShouldBe("DeleteLinks:-1");
        callOrder[1].ShouldBe("DeleteRow:-1");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExecuteDiscardAsync — tree: children processed before parents
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteDiscardAsync_Tree_ChildrenProcessedBeforeParents()
    {
        var callOrder = new List<string>();

        // BFS order in AllIds: parent (-1), child (-2), grandchild (-3)
        // Execution should reverse: grandchild first, then child, then parent
        foreach (var id in new[] { -1, -2, -3 })
        {
            var capturedId = id;
            _seedLinkRepo.DeleteLinksForItemAsync(capturedId, Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask)
                .AndDoes(_ => callOrder.Add($"DeleteLinks:{capturedId}"));
            _workItemRepo.DeleteByIdAsync(capturedId, Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask)
                .AndDoes(_ => callOrder.Add($"DeleteRow:{capturedId}"));
        }

        var plan = new SeedDiscardPlan
        {
            TargetId = -1,
            TargetTitle = "Tree Order",
            AllIds = [-1, -2, -3],
        };

        await _orchestrator.ExecuteDiscardAsync(plan);

        // Should process in reverse order: -3, -2, -1
        callOrder.Count.ShouldBe(6);
        callOrder[0].ShouldBe("DeleteLinks:-3");
        callOrder[1].ShouldBe("DeleteRow:-3");
        callOrder[2].ShouldBe("DeleteLinks:-2");
        callOrder[3].ShouldBe("DeleteRow:-2");
        callOrder[4].ShouldBe("DeleteLinks:-1");
        callOrder[5].ShouldBe("DeleteRow:-1");
    }
}
