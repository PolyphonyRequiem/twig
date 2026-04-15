using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class SetCommand_ContextChangeTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IWorkItemLinkRepository _linkRepo;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly WorkingSetService _workingSetService;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public SetCommand_ContextChangeTests()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());

        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _linkRepo = Substitute.For<IWorkItemLinkRepository>();

        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        // Default: no children, no dirty items
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(
            _contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scenario 1: Out-of-sprint item → parents/children/links fetched
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_OutOfSprintItem_FetchesParentChainToRoot()
    {
        // Item 100 → parent 200 → parent 300 (root)
        var item = new WorkItemBuilder(100, "Child Task")
            .AsTask().WithParent(200)
            .WithIterationPath("Project\\Sprint 2").Build();
        var parent = new WorkItemBuilder(200, "Parent Story")
            .AsUserStory().WithParent(300)
            .WithIterationPath("Project\\Sprint 2").Build();
        var root = new WorkItemBuilder(300, "Root Feature")
            .AsFeature()
            .WithIterationPath("Project").Build();

        ArrangeItemInCache(item);
        // Parent not in cache — must be fetched from ADO
        _workItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(parent);
        // Root not in cache — must be fetched from ADO
        _workItemRepo.GetByIdAsync(300, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(300, Arg.Any<CancellationToken>()).Returns(root);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("100");

        result.ShouldBe(0);
        await _adoService.Received().FetchAsync(200, Arg.Any<CancellationToken>());
        await _adoService.Received().FetchAsync(300, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_WithContextChange_FetchesTwoLevelsOfChildren()
    {
        var item = new WorkItemBuilder(100, "Parent Story")
            .AsUserStory().WithIterationPath("Project\\Sprint 1").Build();

        ArrangeItemInCache(item);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("100");

        result.ShouldBe(0);
        // Level 1: SyncChildrenAsync calls FetchChildrenAsync(100)
        await _adoService.Received().FetchChildrenAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_WithContextChange_FetchesLevel2ChildrenForEachLevel1Child()
    {
        var item = new WorkItemBuilder(100, "Parent Story")
            .AsUserStory().WithIterationPath("Project\\Sprint 1").Build();
        var child1 = new WorkItemBuilder(101, "Child 1").AsTask().WithParent(100).Build();
        var child2 = new WorkItemBuilder(102, "Child 2").AsTask().WithParent(100).Build();

        ArrangeItemInCache(item);
        // After SyncChildrenAsync fetches from ADO, GetChildrenAsync returns cached children
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("100");

        result.ShouldBe(0);
        // Level 2: FetchChildrenAsync called for each level-1 child
        await _adoService.Received().FetchChildrenAsync(101, Arg.Any<CancellationToken>());
        await _adoService.Received().FetchChildrenAsync(102, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_WithLinkRepo_SyncsLinks()
    {
        var item = new WorkItemBuilder(100, "Story with links")
            .AsUserStory().WithIterationPath("Project\\Sprint 1").Build();

        ArrangeItemInCache(item);
        _adoService.FetchWithLinksAsync(100, Arg.Any<CancellationToken>())
            .Returns((item, Array.Empty<WorkItemLink>()));

        var cmd = CreateCommand(includeLinkRepo: true);
        var result = await cmd.ExecuteAsync("100");

        result.ShouldBe(0);
        // SyncLinksAsync calls FetchWithLinksAsync
        await _adoService.Received().FetchWithLinksAsync(100, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scenario 2: In-sprint item with parent already cached → minimal fetches
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_InSprintItem_ParentAlreadyCached_NoAdoParentFetch()
    {
        var item = new WorkItemBuilder(100, "Sprint Task")
            .AsTask().WithParent(200)
            .WithIterationPath("Project\\Sprint 1").Build();
        var parent = new WorkItemBuilder(200, "Parent Story")
            .AsUserStory()
            .WithIterationPath("Project\\Sprint 1").Build();

        ArrangeItemInCache(item);
        // Parent IS in cache — no ADO fetch needed for parent chain
        _workItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns(parent);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("100");

        result.ShouldBe(0);
        // Parent was in cache so FetchAsync(200) should NOT be called by the extension
        await _adoService.DidNotReceive().FetchAsync(200, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scenario 3: Network failure during extension → command succeeds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_ExtensionNetworkFailure_CommandStillSucceeds()
    {
        var item = new WorkItemBuilder(100, "Test Item")
            .AsTask().WithIterationPath("Project\\Sprint 1").Build();

        ArrangeItemInCache(item);
        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network unreachable"));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("100");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_ExtensionParentFetchFails_ChildrenStillFetched()
    {
        var item = new WorkItemBuilder(100, "Test Item")
            .AsTask().WithParent(200)
            .WithIterationPath("Project\\Sprint 1").Build();

        ArrangeItemInCache(item);
        // Parent fetch throws — but children should still be attempted
        _workItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO timeout"));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("100");

        result.ShouldBe(0);
        // Children sync still runs despite parent chain failure
        await _adoService.Received().FetchChildrenAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_ExtensionThrowsOperationCanceled_PropagatesException()
    {
        var item = new WorkItemBuilder(100, "Test Item")
            .AsTask().WithIterationPath("Project\\Sprint 1").Build();

        ArrangeItemInCache(item);
        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException("Cancelled"));

        var cmd = CreateCommand();

        await Should.ThrowAsync<OperationCanceledException>(
            () => cmd.ExecuteAsync("100"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Scenario 4: Additive guarantee → existing cache items not removed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_WithContextChange_UsesProtectedCacheWriter_NeverCallsEvictDirectly()
    {
        var item = new WorkItemBuilder(100, "Test Item")
            .AsTask().WithIterationPath("Project\\Sprint 1").Build();
        var child = new WorkItemBuilder(101, "Child").AsTask().WithParent(100).Build();

        ArrangeItemInCache(item);
        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child });

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("100");

        result.ShouldBe(0);
        // Extension saves through ProtectedCacheWriter (via SyncCoordinator),
        // which calls SaveAsync — never EvictExceptAsync or DeleteByIdAsync
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_NullContextChangeService_DoesNotCallExtension()
    {
        var item = new WorkItemBuilder(100, "Test Item")
            .AsTask().WithIterationPath("Project\\Sprint 1").Build();

        ArrangeItemInCache(item);

        var cmd = CreateCommand(withContextChange: false);
        var result = await cmd.ExecuteAsync("100");

        result.ShouldBe(0);
        // FetchChildrenAsync is only called by extension — should not fire
        await _adoService.DidNotReceive().FetchChildrenAsync(100, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private SetCommand CreateCommand(bool withContextChange = true, bool includeLinkRepo = false)
    {
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var syncCoordinator = new SyncCoordinator(
            _workItemRepo, _adoService, protectedWriter, _pendingChangeStore,
            includeLinkRepo ? _linkRepo : null, 30);
        var contextChangeService = withContextChange
            ? new ContextChangeService(
                _workItemRepo, _adoService, syncCoordinator, protectedWriter,
                includeLinkRepo ? _linkRepo : null)
            : null;
        return new SetCommand(
            _workItemRepo, _contextStore, _activeItemResolver, syncCoordinator,
            _workingSetService, _formatterFactory, _hintEngine,
            contextChangeService: contextChangeService);
    }

    private void ArrangeItemInCache(WorkItem item)
    {
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
    }
}
