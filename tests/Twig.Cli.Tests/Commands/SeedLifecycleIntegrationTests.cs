using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Integration tests verifying the full seed lifecycle:
/// new → edit → view → discard.
/// Each step uses mocked dependencies to catch cross-command regressions.
/// </summary>
public class SeedLifecycleIntegrationTests : IDisposable
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IFieldDefinitionStore _fieldDefStore;
    private readonly IEditorLauncher _editorLauncher;
    private readonly IConsoleInput _consoleInput;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TextWriter _originalOut;

    private static readonly List<FieldDefinition> DefaultFieldDefs =
    [
        new("System.Title", "Title", "String", false),
        new("System.Description", "Description", "html", false),
        new("System.State", "State", "string", true),
    ];

    public SeedLifecycleIntegrationTests()
    {
        _originalOut = Console.Out;

        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        _editorLauncher = Substitute.For<IEditorLauncher>();
        _consoleInput = Substitute.For<IConsoleInput>();

        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Agile());
        _fieldDefStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(DefaultFieldDefs);
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    public void Dispose() => Console.SetOut(_originalOut);

    /// <summary>
    /// Happy-path lifecycle: create a seed, edit it, view the dashboard, then discard.
    /// Verifies each command exits with 0 and that the repository is called correctly at each step.
    /// </summary>
    [Fact]
    public async Task FullSeedLifecycle_NewEditViewDiscard_AllSucceed()
    {
        // ── Arrange shared ─────────────────────────────────────────────
        var parent = new WorkItem
        {
            Id = 100,
            Type = WorkItemType.Feature,
            Title = "Parent Feature",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(100);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetMinSeedIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // ── Step 1: twig seed new ──────────────────────────────────────
        var seedNewCmd = new SeedNewCommand(
            new ActiveItemResolver(_contextStore, _workItemRepo, _adoService),
            _workItemRepo, _processConfigProvider,
            _fieldDefStore, _editorLauncher, _formatterFactory, _hintEngine);

        Console.SetOut(new StringWriter());
        var newResult = await seedNewCmd.ExecuteAsync("My Lifecycle Seed");

        newResult.ShouldBe(0);
        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed && w.Title == "My Lifecycle Seed"),
            Arg.Any<CancellationToken>());

        // Capture the saved seed to use in subsequent steps
        var savedSeed = (WorkItem)_workItemRepo.ReceivedCalls()
            .First(c => c.GetMethodInfo().Name == "SaveAsync")
            .GetArguments()[0]!;

        // ── Step 2: twig seed edit <id> ────────────────────────────────
        _workItemRepo.GetByIdAsync(savedSeed.Id, Arg.Any<CancellationToken>()).Returns(savedSeed);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Title\nUpdated Title\n\n# Description\nAdded description\n");

        var seedEditCmd = new SeedEditCommand(
            _workItemRepo, _fieldDefStore, _editorLauncher, _formatterFactory);

        Console.SetOut(new StringWriter());
        var editResult = await seedEditCmd.ExecuteAsync(savedSeed.Id);

        editResult.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == savedSeed.Id && w.Title == "Updated Title"),
            Arg.Any<CancellationToken>());

        // ── Step 3: twig seed view ─────────────────────────────────────
        var updatedSeed = new WorkItem
        {
            Id = savedSeed.Id,
            Type = savedSeed.Type,
            Title = "Updated Title",
            State = savedSeed.State,
            IsSeed = true,
            IterationPath = savedSeed.IterationPath,
            AreaPath = savedSeed.AreaPath,
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { updatedSeed });

        var renderingPipelineFactory = new RenderingPipelineFactory(
            _formatterFactory,
            Substitute.For<IAsyncRenderer>(),
            isOutputRedirected: () => true);

        var seedViewCmd = new SeedViewCommand(
            _workItemRepo, _fieldDefStore,
            new TwigConfiguration(), renderingPipelineFactory);

        var viewWriter = new StringWriter();
        Console.SetOut(viewWriter);
        var viewResult = await seedViewCmd.ExecuteAsync("human");

        viewResult.ShouldBe(0);
        viewWriter.ToString().ShouldContain("Updated Title");

        // ── Step 4: twig seed discard <id> ────────────────────────────
        _workItemRepo.GetByIdAsync(savedSeed.Id, Arg.Any<CancellationToken>()).Returns(updatedSeed);

        var seedDiscardCmd = new SeedDiscardCommand(
            _workItemRepo, _consoleInput, _formatterFactory);

        Console.SetOut(new StringWriter());
        var discardResult = await seedDiscardCmd.ExecuteAsync(savedSeed.Id, yes: true);

        discardResult.ShouldBe(0);
        await _workItemRepo.Received().DeleteByIdAsync(savedSeed.Id, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that a storage-layer exception thrown by SaveAsync propagates out of
    /// SeedNewCommand (commands propagate exceptions unhandled by design).
    /// </summary>
    [Fact]
    public async Task SeedNew_StorageFailure_PropagatesException()
    {
        var parent = new WorkItem
        {
            Id = 200,
            Type = WorkItemType.Feature,
            Title = "Parent",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(200);
        _workItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetMinSeedIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SQLite write error")));

        var cmd = new SeedNewCommand(
            new ActiveItemResolver(_contextStore, _workItemRepo, _adoService),
            _workItemRepo, _processConfigProvider,
            _fieldDefStore, _editorLauncher, _formatterFactory, _hintEngine);

        Console.SetOut(new StringWriter());
        await Should.ThrowAsync<InvalidOperationException>(
            () => cmd.ExecuteAsync("Storage Fail Seed"));
    }

    /// <summary>
    /// Verifies that a storage-layer exception thrown by SaveAsync propagates out of
    /// SeedEditCommand.
    /// </summary>
    [Fact]
    public async Task SeedEdit_StorageFailure_PropagatesException()
    {
        var seed = new WorkItem
        {
            Id = -1,
            Type = WorkItemType.Task,
            Title = "Existing Seed",
            State = "New",
            IsSeed = true,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Title\nChanged Title\n\n# Description\n\n");
        _workItemRepo.SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SQLite write error")));

        var cmd = new SeedEditCommand(
            _workItemRepo, _fieldDefStore, _editorLauncher, _formatterFactory);

        Console.SetOut(new StringWriter());
        await Should.ThrowAsync<InvalidOperationException>(
            () => cmd.ExecuteAsync(-1));
    }
}

