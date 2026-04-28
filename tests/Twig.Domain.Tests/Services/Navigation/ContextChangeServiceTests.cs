using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Navigation;

public class ContextChangeServiceTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();
    private readonly IWorkItemLinkRepository _linkRepo = Substitute.For<IWorkItemLinkRepository>();

    private readonly ContextChangeService _sut;
    private readonly ContextChangeService _sutWithoutLinks;

    private const int CacheStaleMinutes = 30;

    public ContextChangeServiceTests()
    {
        // Default: no protected items (required for ProtectedCacheWriter/SyncGuard)
        _workItemRepo.GetDirtyItemsAsync().Returns(Array.Empty<WorkItem>());
        _pendingStore.GetDirtyItemIdsAsync().Returns(Array.Empty<int>());

        // Default: no children (prevents null ref in level-2 child enumeration)
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Default: FetchChildrenAsync returns empty (prevents null ref in SyncChildrenAsync)
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingStore);

        var syncPairWithLinks = new SyncCoordinatorPair(
            _workItemRepo, _adoService, protectedWriter, _pendingStore, _linkRepo, readOnlyStaleMinutes: CacheStaleMinutes, readWriteStaleMinutes: CacheStaleMinutes);
        var syncPairWithoutLinks = new SyncCoordinatorPair(
            _workItemRepo, _adoService, protectedWriter, _pendingStore, null, readOnlyStaleMinutes: CacheStaleMinutes, readWriteStaleMinutes: CacheStaleMinutes);

        _sut = new ContextChangeService(
            _workItemRepo, _adoService, syncPairWithLinks.ReadWrite, protectedWriter, _linkRepo);
        _sutWithoutLinks = new ContextChangeService(
            _workItemRepo, _adoService, syncPairWithoutLinks.ReadWrite, protectedWriter);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent chain — item with no parent
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_ItemWithNoParent_DoesNotFetchParents()
    {
        var item = new WorkItemBuilder(100, "Root Item").Build();
        _workItemRepo.GetByIdAsync(100).Returns(item);

        await _sut.ExtendWorkingSetAsync(100);

        // Should not fetch any parents from ADO
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent chain — walks up to root
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_ItemWithParentChain_FetchesMissingParents()
    {
        // Chain: 100 → parent 200 → grandparent 300 (root)
        var item = new WorkItemBuilder(100, "Task").WithParent(200).Build();
        var parent = new WorkItemBuilder(200, "Issue").WithParent(300).Build();
        var grandparent = new WorkItemBuilder(300, "Epic").Build();

        _workItemRepo.GetByIdAsync(100).Returns(item);
        _workItemRepo.GetByIdAsync(200).Returns((WorkItem?)null);
        _workItemRepo.GetByIdAsync(300).Returns((WorkItem?)null);

        _adoService.FetchAsync(200).Returns(parent);
        _adoService.FetchAsync(300).Returns(grandparent);

        await _sut.ExtendWorkingSetAsync(100);

        // Should fetch both parents from ADO
        await _adoService.Received(1).FetchAsync(200, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchAsync(300, Arg.Any<CancellationToken>());

        // Should save both parents to cache
        await _workItemRepo.Received(1).SaveAsync(parent, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveAsync(grandparent, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent chain — parent already cached → no re-fetch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_ParentAlreadyInCache_DoesNotRefetchFromAdo()
    {
        var item = new WorkItemBuilder(100, "Task").WithParent(200).Build();
        var parent = new WorkItemBuilder(200, "Issue").Build(); // root — no parent

        _workItemRepo.GetByIdAsync(100).Returns(item);
        _workItemRepo.GetByIdAsync(200).Returns(parent); // already in cache

        await _sut.ExtendWorkingSetAsync(100);

        // Should NOT fetch parent from ADO — it's already cached
        await _adoService.DidNotReceive().FetchAsync(200, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent chain — initial item not in cache → fetches from ADO
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_ItemNotInCache_FetchesFromAdo()
    {
        var item = new WorkItemBuilder(100, "Task").Build(); // no parent

        _workItemRepo.GetByIdAsync(100).Returns((WorkItem?)null);
        _adoService.FetchAsync(100).Returns(item);

        await _sut.ExtendWorkingSetAsync(100);

        await _adoService.Received(1).FetchAsync(100, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveAsync(item, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent chain — cycle protection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_ParentCycle_DoesNotLoopForever()
    {
        // Pathological case: 100 → 200 → 100 (cycle)
        var item = new WorkItemBuilder(100, "Task").WithParent(200).Build();
        var parent = new WorkItemBuilder(200, "Issue").WithParent(100).Build();

        _workItemRepo.GetByIdAsync(100).Returns(item);
        _workItemRepo.GetByIdAsync(200).Returns((WorkItem?)null);
        _adoService.FetchAsync(200).Returns(parent);

        // Should complete without hanging
        await _sut.ExtendWorkingSetAsync(100);

        await _adoService.Received(1).FetchAsync(200, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Children — level 1 fetched
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_FetchesLevel1Children()
    {
        var item = new WorkItemBuilder(100, "Issue").Build();
        _workItemRepo.GetByIdAsync(100).Returns(item);

        var child1 = new WorkItemBuilder(201, "Task 1").WithParent(100).Build();
        var child2 = new WorkItemBuilder(202, "Task 2").WithParent(100).Build();
        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        await _sut.ExtendWorkingSetAsync(100);

        await _adoService.Received(1).FetchChildrenAsync(100, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Children — level 2 fetched in parallel
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_FetchesLevel2Children()
    {
        var item = new WorkItemBuilder(100, "Epic").Build();
        _workItemRepo.GetByIdAsync(100).Returns(item);

        var child1 = new WorkItemBuilder(201, "Issue 1").WithParent(100).Build();
        var child2 = new WorkItemBuilder(202, "Issue 2").WithParent(100).Build();
        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        // After level 1 sync, repo returns children for level 2 enumeration
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        var grandchild = new WorkItemBuilder(301, "Task 1").WithParent(201).Build();
        _adoService.FetchChildrenAsync(201, Arg.Any<CancellationToken>())
            .Returns(new[] { grandchild });
        _adoService.FetchChildrenAsync(202, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        await _sut.ExtendWorkingSetAsync(100);

        // Level 1
        await _adoService.Received(1).FetchChildrenAsync(100, Arg.Any<CancellationToken>());
        // Level 2 — both children queried
        await _adoService.Received(1).FetchChildrenAsync(201, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchChildrenAsync(202, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Children — no children → no errors
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_NoChildren_CompletesWithoutError()
    {
        var item = new WorkItemBuilder(100, "Leaf Task").Build();
        _workItemRepo.GetByIdAsync(100).Returns(item);

        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        await _sut.ExtendWorkingSetAsync(100);

        await _adoService.Received(1).FetchChildrenAsync(100, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Links — fetched when linkRepo is present
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_WithLinkRepo_FetchesLinks()
    {
        var item = new WorkItemBuilder(100, "Item").Build();
        _workItemRepo.GetByIdAsync(100).Returns(item);

        var links = new[] { new WorkItemLink(100, 999, LinkTypes.Related) };
        _adoService.FetchWithLinksAsync(100, Arg.Any<CancellationToken>())
            .Returns((item, (IReadOnlyList<WorkItemLink>)links));

        await _sut.ExtendWorkingSetAsync(100);

        await _adoService.Received(1).FetchWithLinksAsync(100, Arg.Any<CancellationToken>());
        await _linkRepo.Received(1).SaveLinksAsync(100, Arg.Any<IReadOnlyList<WorkItemLink>>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Links — silently skipped when linkRepo is null
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_WithNullLinkRepo_SkipsLinks()
    {
        var item = new WorkItemBuilder(100, "Item").Build();
        _workItemRepo.GetByIdAsync(100).Returns(item);

        await _sutWithoutLinks.ExtendWorkingSetAsync(100);

        await _adoService.DidNotReceive().FetchWithLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Error handling — parent chain error → children and links still run
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_ParentChainError_ContinuesToChildrenAndLinks()
    {
        // Item not in cache and ADO fetch fails for parent chain
        _workItemRepo.GetByIdAsync(100).Returns((WorkItem?)null);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO unreachable"));

        var links = Array.Empty<WorkItemLink>();
        var itemForLinks = new WorkItemBuilder(100, "Item").Build();
        _adoService.FetchWithLinksAsync(100, Arg.Any<CancellationToken>())
            .Returns((itemForLinks, (IReadOnlyList<WorkItemLink>)links));

        await _sut.ExtendWorkingSetAsync(100);

        // Children phase should still run (FetchChildrenAsync called with default empty return)
        await _adoService.Received(1).FetchChildrenAsync(100, Arg.Any<CancellationToken>());
        // Links phase should still run
        await _adoService.Received(1).FetchWithLinksAsync(100, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Error handling — child fetch error → links still run
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_ChildFetchError_ContinuesToLinks()
    {
        var item = new WorkItemBuilder(100, "Item").Build();
        _workItemRepo.GetByIdAsync(100).Returns(item);

        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Child fetch failed"));

        var links = Array.Empty<WorkItemLink>();
        _adoService.FetchWithLinksAsync(100, Arg.Any<CancellationToken>())
            .Returns((item, (IReadOnlyList<WorkItemLink>)links));

        await _sut.ExtendWorkingSetAsync(100);

        // Links phase should still run despite child error
        await _adoService.Received(1).FetchWithLinksAsync(100, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Error handling — link fetch error → does not throw
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_LinkFetchError_DoesNotThrow()
    {
        var item = new WorkItemBuilder(100, "Item").Build();
        _workItemRepo.GetByIdAsync(100).Returns(item);

        _adoService.FetchWithLinksAsync(100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Link fetch failed"));

        // Should complete without throwing
        await _sut.ExtendWorkingSetAsync(100);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Error handling — all phases fail → does not throw
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_AllPhasesFail_DoesNotThrow()
    {
        _workItemRepo.GetByIdAsync(100).Returns((WorkItem?)null);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Parent failed"));
        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Children failed"));
        _adoService.FetchWithLinksAsync(100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Links failed"));

        // Should complete without throwing
        await _sut.ExtendWorkingSetAsync(100);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Error handling — OperationCanceledException propagates
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_OperationCanceled_Propagates()
    {
        _workItemRepo.GetByIdAsync(100).Returns((WorkItem?)null);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.ExtendWorkingSetAsync(100));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Full integration — all three phases execute in order
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExtendWorkingSetAsync_HappyPath_AllPhasesExecute()
    {
        // Item 100 → parent 200 (root)
        var item = new WorkItemBuilder(100, "Task").WithParent(200).Build();
        var parent = new WorkItemBuilder(200, "Issue").Build();

        _workItemRepo.GetByIdAsync(100).Returns(item);
        _workItemRepo.GetByIdAsync(200).Returns((WorkItem?)null);
        _adoService.FetchAsync(200).Returns(parent);

        // Level 1 children
        var child = new WorkItemBuilder(301, "Sub-Task").WithParent(100).Build();
        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child });

        // Level 2 children
        _adoService.FetchChildrenAsync(301, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Links
        var links = new[] { new WorkItemLink(100, 500, LinkTypes.Related) };
        _adoService.FetchWithLinksAsync(100, Arg.Any<CancellationToken>())
            .Returns((item, (IReadOnlyList<WorkItemLink>)links));

        await _sut.ExtendWorkingSetAsync(100);

        // Parent chain: fetched parent 200
        await _adoService.Received(1).FetchAsync(200, Arg.Any<CancellationToken>());
        // Children: level 1 + level 2
        await _adoService.Received(1).FetchChildrenAsync(100, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchChildrenAsync(301, Arg.Any<CancellationToken>());
        // Links
        await _adoService.Received(1).FetchWithLinksAsync(100, Arg.Any<CancellationToken>());
        await _linkRepo.Received(1).SaveLinksAsync(100, Arg.Any<IReadOnlyList<WorkItemLink>>(), Arg.Any<CancellationToken>());
    }
}
