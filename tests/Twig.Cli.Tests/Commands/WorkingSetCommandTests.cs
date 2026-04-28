using NSubstitute;
using Shouldly;
using Spectre.Console.Rendering;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// WS-013: Working set eviction + sync tests for <see cref="SetCommand"/>.
/// Verifies eviction on cache miss, no eviction on cache hit, dirty item survival,
/// targeted sync via SyncItemSetAsync, ComputeAsync receives IterationPath,
/// and TTY path uses unified output (no RenderWithSyncAsync wrapper).
/// </summary>
public class WorkingSetCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IIterationService _iterationService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly WorkingSetService _workingSetService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public WorkingSetCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _iterationService = Substitute.For<IIterationService>();
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedCacheWriter, _pendingChangeStore, null, 30, 30);
        _workingSetService = new WorkingSetService(
            _contextStore, _workItemRepo, _pendingChangeStore, _iterationService, null);
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    private SetCommand CreateCommand(RenderingPipelineFactory? pipelineFactory = null) =>
        new(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorFactory,
            _workingSetService, _formatterFactory, _hintEngine, pipelineFactory);

    // ── (a) Cache miss → eviction fires ────────────────────────────

    [Fact]
    public async Task CacheMiss_EvictionFires_NonWorkingSetItemsDeleted()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(42, "New Item");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        // Sprint items in working set
        var sprintItem = CreateWorkItem(50, "Sprint Item");
        _workItemRepo.GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { sprintItem });

        IReadOnlySet<int>? capturedKeepIds = null;
        await _workItemRepo.EvictExceptAsync(
            Arg.Do<IReadOnlySet<int>>(ids => capturedKeepIds = ids),
            Arg.Any<CancellationToken>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        capturedKeepIds.ShouldNotBeNull();
        capturedKeepIds.ShouldContain(42);  // active item
        capturedKeepIds.ShouldContain(50);  // sprint item (working set member)
    }

    // ── (b) Cache hit → no eviction, sync still fires ──────────────

    [Fact]
    public async Task CacheHit_NoEviction_SyncStillFires()
    {
        var item = CreateWorkItem(42, "Cached Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // No eviction on cache hit (FR-012)
        await _workItemRepo.DidNotReceive().EvictExceptAsync(
            Arg.Any<IReadOnlySet<int>>(), Arg.Any<CancellationToken>());
        // But SyncItemSetAsync still fires (fetches stale active item)
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
    }

    // ── (c) Dirty items survive eviction ───────────────────────────

    [Fact]
    public async Task DirtyItems_SurviveEviction()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(42, "New Item");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        // Dirty item from prior context
        var dirtyItem = CreateWorkItem(99, "Dirty Orphan");
        dirtyItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { dirtyItem });

        IReadOnlySet<int>? capturedKeepIds = null;
        await _workItemRepo.EvictExceptAsync(
            Arg.Do<IReadOnlySet<int>>(ids => capturedKeepIds = ids),
            Arg.Any<CancellationToken>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        capturedKeepIds.ShouldNotBeNull();
        capturedKeepIds.ShouldContain(99); // dirty item preserved
    }

    // ── (d) Working set items survive eviction ─────────────────────

    [Fact]
    public async Task WorkingSetItems_SurviveEviction()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(42, "New Item", parentId: 10);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        // Parent chain — mock both hydration path (ID 10) and ComputeAsync path (ID 42)
        var parent = CreateWorkItem(10, "Parent");
        _workItemRepo.GetParentChainAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });
        _workItemRepo.GetParentChainAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });

        // Children
        var child = CreateWorkItem(100, "Child");
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { child });

        // Seeds
        var seed = CreateWorkItem(-1, "Seed");
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { seed });

        IReadOnlySet<int>? capturedKeepIds = null;
        await _workItemRepo.EvictExceptAsync(
            Arg.Do<IReadOnlySet<int>>(ids => capturedKeepIds = ids),
            Arg.Any<CancellationToken>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        capturedKeepIds.ShouldNotBeNull();
        capturedKeepIds.ShouldContain(42);   // active item
        capturedKeepIds.ShouldContain(10);   // parent
        capturedKeepIds.ShouldContain(100);  // child
        capturedKeepIds.ShouldContain(-1);   // seed
    }

    // ── (e) Targeted sync uses FetchAsync per-item, never FetchChildrenAsync ─

    [Fact]
    public async Task TargetedSync_DoesNotCallFetchChildren_OnCacheMiss()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(42, "New Item");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        var cmd = CreateCommand();
        await cmd.ExecuteAsync("42");

        // SyncItemSetAsync uses FetchAsync per-item (not FetchChildrenAsync)
        await _adoService.DidNotReceive().FetchChildrenAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TargetedSync_DoesNotCallFetchChildren_OnCacheHit()
    {
        var item = CreateWorkItem(42, "Cached Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = CreateCommand();
        await cmd.ExecuteAsync("42");

        // FetchChildrenAsync NOT called — SetCommand uses SyncItemSetAsync, not SyncChildrenAsync
        await _adoService.DidNotReceive().FetchChildrenAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── (f) ComputeAsync receives item.IterationPath ───────────────

    [Fact]
    public async Task CacheHit_SkipsComputeAsync_GetCurrentIterationNotCalled()
    {
        // Cache hit → fetchedFromAdo = false → ComputeAsync never called
        var item = CreateWorkItem(42, "Item", iterationPath: "Project\\Sprint 2");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // ComputeAsync skipped on cache hit — no iteration queries at all
        await _iterationService.DidNotReceive().GetCurrentIterationAsync(Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheMiss_ComputeAsync_ReceivesItemIterationPath()
    {
        // Cache miss → fetchedFromAdo = true → ComputeAsync called with item.IterationPath
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(42, "Item", iterationPath: "Project\\Sprint 2");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // DD-06: ComputeAsync uses item.IterationPath directly, not GetCurrentIterationAsync
        await _iterationService.DidNotReceive().GetCurrentIterationAsync(Arg.Any<CancellationToken>());
        // GetByIterationAsync called with item's iteration path (Sprint 2, not the default Sprint 1)
        await _workItemRepo.Received().GetByIterationAsync(
            Arg.Is<IterationPath>(ip => ip.ToString() == "Project\\Sprint 2"),
            Arg.Any<CancellationToken>());
    }

    // ── (g) TTY path no longer wraps sync in RenderWithSyncAsync (DD-5) ───

    [Fact]
    public async Task TtyPath_DoesNotUseRenderWithSyncAsync()
    {
        var item = CreateWorkItem(42, "TTY Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var mockRenderer = Substitute.For<IAsyncRenderer>();

        var pipelineFactory = new RenderingPipelineFactory(
            _formatterFactory, mockRenderer, isOutputRedirected: () => false);
        var cmd = CreateCommand(pipelineFactory);
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // DD-5: targeted sync is fast enough — no spinner wrapper needed
        await mockRenderer.DidNotReceive().RenderWithSyncAsync(
            Arg.Any<Func<Task<IRenderable>>>(),
            Arg.Any<Func<Task<SyncResult>>>(),
            Arg.Any<Func<SyncResult, Task<IRenderable?>>>(),
            Arg.Any<CancellationToken>());
    }

    private static WorkItem CreateWorkItem(int id, string title, int? parentId = null, string iterationPath = "Project\\Sprint 1")
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            ParentId = parentId,
            IterationPath = IterationPath.Parse(iterationPath).Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
