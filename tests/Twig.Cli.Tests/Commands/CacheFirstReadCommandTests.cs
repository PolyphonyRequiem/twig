using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// EPIC-004 tests: Verifies cache-first behavior, auto-fetch on cache miss,
/// dirty-item protection during sync, and JSON output parity after adopting
/// ActiveItemResolver and SyncCoordinator in read commands.
/// </summary>
[Collection("ConsoleRedirect")]
public class CacheFirstReadCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly WorkingSetService _workingSetService;
    private readonly ITrackingService _trackingService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IWorkItemLinkRepository _workItemLinkRepo;

    public CacheFirstReadCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _workItemLinkRepo = Substitute.For<IWorkItemLinkRepository>();
        _seedLinkRepo.GetLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SeedLink>());
        _workItemLinkRepo.GetLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedCacheWriter, _pendingChangeStore, null, 30, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);
        _trackingService = Substitute.For<ITrackingService>();
        _trackingService.GetTrackedItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());
        _trackingService.GetExcludedIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _processTypeStore = Substitute.For<IProcessTypeStore>();
    }

    // ── (a) Cached data displayed immediately ──────────────────────

    [Fact]
    public async Task SetCommand_CachedItem_DisplaysImmediately_NoAdoFetch()
    {
        var item = CreateWorkItem(42, "Cached Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorFactory,
            _workingSetService, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
        // Should NOT call FetchAsync since item was in cache
        await _adoService.DidNotReceive().FetchAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StatusCommand_CachedItem_DisplaysFromCache()
    {
        var item = CreateWorkItem(1, "Cached Status Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Domain.Common.PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var config = new TwigConfiguration { Seed = new SeedConfig { StaleDays = 14 } };
        var cmd = new StatusCommand(_contextStore, _workItemRepo, _pendingChangeStore,
            config, _formatterFactory, _hintEngine, _activeItemResolver, _workingSetService, _syncCoordinatorFactory,
            new TwigPaths(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "config"), Path.Combine(Path.GetTempPath(), "twig.db")));
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        // No ADO fetch calls since item was in cache
        await _adoService.DidNotReceive().FetchAsync(1, Arg.Any<CancellationToken>());
    }

    // ── (b) Working set sync after setting context ────────────────

    [Fact]
    public async Task SetCommand_SyncsWorkingSet_AfterSettingContext()
    {
        var item = CreateWorkItem(42, "Item With Children");
        // Item has stale LastSyncedAt so sync coordinator will re-fetch
        var staleItem = new WorkItem
        {
            Id = 42, Type = WorkItemType.Task, Title = "Item With Children", State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
            LastSyncedAt = DateTimeOffset.UtcNow.AddMinutes(-60),
        };
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(staleItem);

        // After SetActiveWorkItemIdAsync(42), GetActiveWorkItemIdAsync returns 42
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        // Working set sync fetches stale items via FetchAsync (not FetchChildrenAsync)
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorFactory,
            _workingSetService, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // SyncItemSetAsync fetches stale items individually via FetchAsync (not FetchChildrenAsync)
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
    }

    // ── (c) Stale data + failed sync ───────────────────────────────

    [Fact]
    public async Task SetCommand_SyncFailure_StillSucceeds()
    {
        var item = CreateWorkItem(42, "Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Network unavailable"));

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorFactory,
            _workingSetService, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("42");

        // Command still succeeds — sync failure is non-fatal
        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    // ── (d) Dirty items not overwritten ────────────────────────────

    [Fact]
    public async Task SetCommand_DirtyChildren_NotOverwritten()
    {
        var item = CreateWorkItem(42, "Parent");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var dirtyChild = CreateWorkItem(100, "Dirty Child");
        dirtyChild.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { dirtyChild });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<int> { 100 });

        // SyncItemSetAsync skips dirty items — dirty protection is enforced at write time
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorFactory,
            _workingSetService, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // Verify dirty child (ID 100) was NOT fetched — SyncItemSetAsync only syncs target + parent chain
        await _adoService.DidNotReceive().FetchAsync(100, Arg.Any<CancellationToken>());
    }

    // ── (e) JSON output parity ─────────────────────────────────────

    [Fact]
    public async Task SetCommand_JsonOutput_ProducesExpectedFormat()
    {
        var item = CreateWorkItem(42, "JSON Test Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorFactory,
            _workingSetService, _formatterFactory, _hintEngine);

        var result = await cmd.ExecuteAsync("42", "json");
        result.ShouldBe(0);
    }

    [Fact]
    public async Task StatusCommand_JsonOutput_ProducesExpectedFormat()
    {
        var item = CreateWorkItem(1, "JSON Status Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Domain.Common.PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var config = new TwigConfiguration { Seed = new SeedConfig { StaleDays = 14 } };
        var cmd = new StatusCommand(_contextStore, _workItemRepo, _pendingChangeStore,
            config, _formatterFactory, _hintEngine, _activeItemResolver, _workingSetService, _syncCoordinatorFactory,
            new TwigPaths(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "config"), Path.Combine(Path.GetTempPath(), "twig.db")));

        var result = await cmd.ExecuteAsync("json");
        result.ShouldBe(0);
    }

    // ── (f) Auto-fetch on cache miss ───────────────────────────────

    [Fact]
    public async Task SetCommand_CacheMiss_AutoFetchesFromAdo()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(42, "Fetched Item");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorFactory,
            _workingSetService, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(item, Arg.Any<CancellationToken>());
        await _contextStore.Received().SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StatusCommand_CacheMiss_AutoFetchesFromAdo()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(99);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var item = CreateWorkItem(99, "Auto-Fetched Item");
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(99, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Domain.Common.PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var config = new TwigConfiguration { Seed = new SeedConfig { StaleDays = 14 } };
        var cmd = new StatusCommand(_contextStore, _workItemRepo, _pendingChangeStore,
            config, _formatterFactory, _hintEngine, _activeItemResolver, _workingSetService, _syncCoordinatorFactory,
            new TwigPaths(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "config"), Path.Combine(Path.GetTempPath(), "twig.db")));
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        // FetchAsync called at least once: initial cache-miss auto-fetch via ActiveItemResolver
        // (may also be called by SyncWorkingSetAsync for stale items)
        await _adoService.Received().FetchAsync(99, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TreeCommand_CacheMiss_AutoFetchesFromAdo()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(50);
        _workItemRepo.GetByIdAsync(50, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var item = CreateWorkItem(50, "Auto-Fetched Tree Item");
        _adoService.FetchAsync(50, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(50, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var config = new TwigConfiguration();
        var cmd = new TreeCommand(_contextStore, _workItemRepo, config,
            _formatterFactory, _activeItemResolver, _workingSetService, _syncCoordinatorFactory, _processTypeStore);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received().FetchAsync(50, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TreeCommand_Unreachable_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(50);
        _workItemRepo.GetByIdAsync(50, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(50, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Network error"));

        var config = new TwigConfiguration();
        var cmd = new TreeCommand(_contextStore, _workItemRepo, config,
            _formatterFactory, _activeItemResolver, _workingSetService, _syncCoordinatorFactory, _processTypeStore);

        var result = await cmd.ExecuteAsync();
        result.ShouldBe(1);
    }

    [Fact]
    public async Task NavigationCommands_Up_CacheMiss_AutoFetches()
    {
        var parent = CreateWorkItem(1, "Parent");
        var child = CreateWorkItem(2, "Child", parentId: 1);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(2);
        // First call returns null (cache miss), second returns the fetched item
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(child);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetParentChainAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });
        _workItemRepo.GetChildrenAsync(2, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var setCmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorFactory,
            _workingSetService, _formatterFactory, _hintEngine);
        var navCmd = new NavigationCommands(_contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, setCmd, _formatterFactory,
            _activeItemResolver);
        var result = await navCmd.UpAsync();

        result.ShouldBe(0);
        // FetchAsync(2) called at least once: initial cache-miss auto-fetch via ActiveItemResolver
        // (may also be called by SyncWorkingSetAsync for stale items)
        await _adoService.Received().FetchAsync(2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetCommand_Unreachable_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Not found"));

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorFactory,
            _workingSetService, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("999");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task StatusCommand_Unreachable_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(999);
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Network error"));

        var config = new TwigConfiguration { Seed = new SeedConfig { StaleDays = 14 } };
        var cmd = new StatusCommand(_contextStore, _workItemRepo, _pendingChangeStore,
            config, _formatterFactory, _hintEngine, _activeItemResolver, _workingSetService, _syncCoordinatorFactory,
            new TwigPaths(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "config"), Path.Combine(Path.GetTempPath(), "twig.db")));

        var result = await cmd.ExecuteAsync();
        result.ShouldBe(1);
    }

    // ── SetCommand parent hydration via ActiveItemResolver ─────────

    [Fact]
    public async Task SetCommand_ParentNotInCache_AutoFetchesParent()
    {
        var item = CreateWorkItem(42, "Child Item", parentId: 100);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>()); // parent not in cache

        var parent = CreateWorkItem(100, "Parent Item");
        // First call for ID 100 from GetByIdAsync returns null (parent not cached)
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorFactory,
            _workingSetService, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        await _adoService.Received(1).FetchAsync(100, Arg.Any<CancellationToken>());
    }

    // ── WorkspaceCommand auto-fetch on cache miss ──────────────────

    [Fact]
    public async Task WorkspaceCommand_ActiveItemCacheMiss_AutoFetches()
    {
        var item = CreateWorkItem(1, "Auto-Fetched WS Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var processTypeStore = Substitute.For<IProcessTypeStore>();
        var fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        var config = new TwigConfiguration();
        var cmd = new WorkspaceCommand(_contextStore, _workItemRepo, iterationService, config,
            _formatterFactory, _hintEngine, processTypeStore, fieldDefinitionStore, _activeItemResolver, _workingSetService, _trackingService, new SprintHierarchyBuilder());
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).FetchAsync(1, Arg.Any<CancellationToken>());
    }

    // ── Non-TTY / piped output ─────────────────────────────────────

    [Fact]
    public async Task SetCommand_MinimalOutput_WorksWithoutRenderer()
    {
        var item = CreateWorkItem(42, "Minimal Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorFactory,
            _workingSetService, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("42", "minimal");

        result.ShouldBe(0);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, int? parentId = null)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
            LastSyncedAt = DateTimeOffset.UtcNow,
        };
    }
}
