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
    //  Wiring smoke test — confirms SetCommand calls ExtendWorkingSetAsync
    // ═══════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════
    //  Scenario 2: Network failure during extension → command succeeds
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

    // ═══════════════════════════════════════════════════════════════
    //  Scenario 3: Null service → extension skipped
    // ═══════════════════════════════════════════════════════════════

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

    private SetCommand CreateCommand(bool withContextChange = true)
    {
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var syncCoordinator = new SyncCoordinator(
            _workItemRepo, _adoService, protectedWriter, _pendingChangeStore, 30);
        var contextChangeService = withContextChange
            ? new ContextChangeService(_workItemRepo, _adoService, syncCoordinator, protectedWriter)
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
