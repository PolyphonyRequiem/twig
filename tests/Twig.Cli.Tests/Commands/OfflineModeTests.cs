using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for offline mode (FM-001): when ADO is unreachable, reads succeed
/// from cache, writes queue locally, and the correct banner is shown.
/// </summary>
public class OfflineModeTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IConsoleInput _consoleInput;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinatorPair _syncCoordinatorPair;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public OfflineModeTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinatorPair = new SyncCoordinatorPair(_workItemRepo, _adoService, protectedCacheWriter, _pendingChangeStore, null, 30, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    [Fact]
    public void ExceptionHandler_AdoOfflineException_ShowsBanner()
    {
        var savedExitCode = Environment.ExitCode;
        try
        {
            var ex = new AdoOfflineException(new HttpRequestException("Network error"));
            var stderr = new StringWriter();
            var code = ExceptionHandler.Handle(ex, stderr);

            code.ShouldBe(1);
            stderr.ToString().ShouldContain("ADO unreachable");
            stderr.ToString().ShouldContain("offline mode");
        }
        finally
        {
            Environment.ExitCode = savedExitCode;
        }
    }

    [Fact]
    public async Task Status_ReadsFromCache_WhenAdoUnavailable()
    {
        // Status command reads from cache and should succeed even when ADO is down
        // (status doesn't call ADO directly — it reads from local cache)
        var item = CreateWorkItem(1, "Cached Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var paths = new TwigPaths(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "config"), Path.Combine(Path.GetTempPath(), "twig.db"));
        var statusFieldReader = new StatusFieldConfigReader(paths);
        var redirectedPipeline = new RenderingPipelineFactory(_formatterFactory, new SpectreRenderer(new Spectre.Console.Testing.TestConsole(), new SpectreTheme(new DisplayConfig())), isOutputRedirected: () => true);
        var ctx = new CommandContext(redirectedPipeline, _formatterFactory, _hintEngine, new TwigConfiguration());
        var statusCmd = new StatusCommand(ctx,
            _contextStore, _workItemRepo, _pendingChangeStore,
            _activeItemResolver, _workingSetService, _syncCoordinatorPair,
            statusFieldReader);
        var result = await statusCmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Save_OfflineAdoThrows_LogsErrorAndReturnsFailure()
    {
        var item = CreateWorkItem(1, "Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });

        // ADO throws AdoOfflineException when trying to fetch
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoOfflineException(new HttpRequestException("Connection refused")));

        var stderr = new StringWriter();
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var flusher = new PendingChangeFlusher(_workItemRepo, _adoService, _pendingChangeStore, _consoleInput, _formatterFactory, stderr);
        var saveCmd = new SaveCommand(_workItemRepo, _pendingChangeStore, flusher,
            resolver, _formatterFactory, stderr: stderr);

        // FR-7: Exception is caught and logged, returns error code instead of propagating
        var result = await saveCmd.ExecuteAsync(all: true);

        result.ShouldBe(1);
        stderr.ToString().ShouldContain("#1");
        stderr.ToString().ShouldContain("ADO unreachable");
    }

    private static WorkItem CreateWorkItem(int id, string title) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = title,
        State = "New",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };
}
