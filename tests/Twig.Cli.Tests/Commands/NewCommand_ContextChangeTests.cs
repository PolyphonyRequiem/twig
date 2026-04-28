using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class NewCommand_ContextChangeTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly IFieldDefinitionStore _fieldDefStore;
    private readonly IEditorLauncher _editorLauncher;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public NewCommand_ContextChangeTests()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());

        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        _editorLauncher = Substitute.For<IEditorLauncher>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        _fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>
            {
                new("System.Title", "Title", "String", false),
                new("System.Description", "Description", "String", false),
            });

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        _config = new TwigConfiguration
        {
            Project = "TestProject",
            User = new UserConfig { DisplayName = "Test User" },
            Defaults = new DefaultsConfig
            {
                AreaPath = "TestProject\\Area1",
                IterationPath = "TestProject\\Sprint 1",
            },
        };
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
    }

    [Fact]
    public async Task New_WithSetFlag_InvokesContextChangeExtension()
    {
        ArrangeCreateSuccess(42);

        var cmd = CreateCommandWithContextChange();
        var result = await cmd.ExecuteAsync("My Task", "Task", set: true);

        result.ShouldBe(0);
        // Extension triggers child sync via SyncCoordinator.SyncChildrenAsync
        await _adoService.Received().FetchChildrenAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_WithoutSetFlag_DoesNotInvokeContextChangeExtension()
    {
        ArrangeCreateSuccess(42);

        var cmd = CreateCommandWithContextChange();
        var result = await cmd.ExecuteAsync("My Task", "Task", set: false);

        result.ShouldBe(0);
        // FetchChildrenAsync is only called by extension — should not be called
        await _adoService.DidNotReceive().FetchChildrenAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_ExtensionFailure_DoesNotFailCommand()
    {
        ArrangeCreateSuccess(42);
        _adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO unreachable"));

        var cmd = CreateCommandWithContextChange();
        var result = await cmd.ExecuteAsync("My Task", "Task", set: true);

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task New_NullContextChangeService_DoesNotCallExtension()
    {
        ArrangeCreateSuccess(42);

        var cmd = new NewCommand(
            _adoService, _workItemRepo, _contextStore,
            _fieldDefStore, _editorLauncher, _formatterFactory,
            _hintEngine, _config,
            new SeedFactory(new SeedIdCounter()));

        var result = await cmd.ExecuteAsync("My Task", "Task", set: true);

        result.ShouldBe(0);
        // FetchChildrenAsync is only called by extension — should not be called
        await _adoService.DidNotReceive().FetchChildrenAsync(42, Arg.Any<CancellationToken>());
    }

    private NewCommand CreateCommandWithContextChange()
    {
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var SyncCoordinatorPair = new SyncCoordinatorPair(
            _workItemRepo, _adoService, protectedWriter, _pendingChangeStore, null, readOnlyStaleMinutes: 30, readWriteStaleMinutes: 30);
        var contextChangeService = new ContextChangeService(
            _workItemRepo, _adoService, SyncCoordinatorPair.ReadWrite, protectedWriter);
        return new NewCommand(
            _adoService, _workItemRepo, _contextStore,
            _fieldDefStore, _editorLauncher, _formatterFactory,
            _hintEngine, _config,
            new SeedFactory(new SeedIdCounter()),
            contextChangeService);
    }

    private void ArrangeCreateSuccess(int newId = 100, string title = "My Task")
    {
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(newId);
        _adoService.FetchAsync(newId, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(newId, title)
                .AsTask()
                .WithAreaPath("TestProject\\Area1")
                .WithIterationPath("TestProject\\Sprint 1")
                .Build());
        _adoService.FetchChildrenAsync(newId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
    }
}
