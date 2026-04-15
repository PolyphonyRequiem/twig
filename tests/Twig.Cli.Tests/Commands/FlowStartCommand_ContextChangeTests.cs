using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class FlowStartCommand_ContextChangeTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IConsoleInput _consoleInput;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly ProtectedCacheWriter _protectedCacheWriter;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public FlowStartCommand_ContextChangeTests()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());

        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _consoleInput = Substitute.For<IConsoleInput>();

        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration
        {
            User = new UserConfig { DisplayName = "Test User" },
            Git = new GitConfig { BranchTemplate = "feature/{id}-{title}", DefaultTarget = "main" },
        };

        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Agile());
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
    }

    [Fact]
    public async Task FlowStart_WithContextChangeService_InvokesExtension()
    {
        var item = CreateWorkItem(42, "Add login", "New");
        ArrangeItemResolution(item);

        var cmd = CreateCommandWithContextChange();
        var result = await cmd.ExecuteAsync("42", noBranch: true, noState: true, noAssign: true);

        result.ShouldBe(0);
        // Extension triggers child sync via SyncCoordinator.SyncChildrenAsync
        await _adoService.Received().FetchChildrenAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlowStart_ExtensionFailure_DoesNotFailCommand()
    {
        var item = CreateWorkItem(42, "Add login", "New");
        ArrangeItemResolution(item);
        _adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO unreachable"));

        var cmd = CreateCommandWithContextChange();
        var result = await cmd.ExecuteAsync("42", noBranch: true, noState: true, noAssign: true);

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlowStart_NullContextChangeService_DoesNotCallExtension()
    {
        var item = CreateWorkItem(42, "Add login", "New");
        ArrangeItemResolution(item);

        var cmd = new FlowStartCommand(
            _workItemRepo, _adoService, _contextStore, _activeItemResolver, _protectedCacheWriter,
            _processConfigProvider, _consoleInput, _formatterFactory, _hintEngine, _config);

        var result = await cmd.ExecuteAsync("42", noBranch: true, noState: true, noAssign: true);

        result.ShouldBe(0);
        // FetchChildrenAsync is only called by extension — should not be called
        await _adoService.DidNotReceive().FetchChildrenAsync(42, Arg.Any<CancellationToken>());
    }

    private FlowStartCommand CreateCommandWithContextChange()
    {
        var syncCoordinator = new SyncCoordinator(
            _workItemRepo, _adoService, _protectedCacheWriter, _pendingChangeStore, 30);
        var contextChangeService = new ContextChangeService(
            _workItemRepo, _adoService, syncCoordinator, _protectedCacheWriter);
        return new FlowStartCommand(
            _workItemRepo, _adoService, _contextStore, _activeItemResolver, _protectedCacheWriter,
            _processConfigProvider, _consoleInput, _formatterFactory, _hintEngine, _config,
            contextChangeService: contextChangeService);
    }

    private void ArrangeItemResolution(WorkItem item)
    {
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchChildrenAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
    }

    private static WorkItem CreateWorkItem(int id, string title, string state) =>
        new WorkItemBuilder(id, title).AsUserStory().InState(state)
            .AssignedTo("Test User").WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project").Build();
}
