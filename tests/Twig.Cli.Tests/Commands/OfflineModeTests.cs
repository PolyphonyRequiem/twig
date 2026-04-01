using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Config;
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
    private readonly SyncCoordinator _syncCoordinator;
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
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, _pendingChangeStore, 30);
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

        var statusOrchestrator = new StatusOrchestrator(
            _contextStore, _workItemRepo, _pendingChangeStore, _activeItemResolver, _workingSetService, _syncCoordinator);
        var statusCmd = new StatusCommand(
            _contextStore, _workItemRepo, _pendingChangeStore,
            new TwigConfiguration(), _formatterFactory, _hintEngine,
            _activeItemResolver, _workingSetService, _syncCoordinator,
            new TwigPaths(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "config"), Path.Combine(Path.GetTempPath(), "twig.db")));
        var result = await statusCmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Save_OfflineAdoThrows_ExceptionPropagates()
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

        var saveCmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            new ActiveItemResolver(_contextStore, _workItemRepo, _adoService), _consoleInput, _formatterFactory);

        await Should.ThrowAsync<AdoOfflineException>(() => saveCmd.ExecuteAsync(all: true));
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
