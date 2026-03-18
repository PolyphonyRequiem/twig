using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class StatusCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly StatusCommand _cmd;

    public StatusCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);
        var config = new TwigConfiguration { Seed = new SeedConfig { StaleDays = 14 } };
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _cmd = new StatusCommand(_contextStore, _workItemRepo, _pendingChangeStore,
            config, formatterFactory, hintEngine, _activeItemResolver, _workingSetService, _syncCoordinator);
    }

    [Fact]
    public async Task Status_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Status_ActiveItem_ReturnsSuccess()
    {
        var item = CreateWorkItem(1, "Test Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Status_WithPendingChanges_ReturnsSuccess()
    {
        var item = CreateWorkItem(1, "Test Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var pending = new PendingChangeRecord[]
        {
            new(1, "field", "System.Title", "Old", "New"),
            new(1, "note", null, null, "A note"),
            new(1, "note", null, null, "Another note"),
        };
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(pending);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Status_ItemNotInCache_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(99);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Not found")));

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    private static WorkItem CreateWorkItem(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    // ── WS-021: JSON output parity ──────────────────────────────────

    [Fact]
    public async Task Status_JsonOutput_NoSyncIndicators()
    {
        var item = CreateWorkItem(1, "JSON Parity Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await _cmd.ExecuteAsync("json");
            result.ShouldBe(0);
            var output = sw.ToString().Trim();
            // JSON output must not contain sync indicators
            output.ShouldNotContain("syncing");
            output.ShouldNotContain("Syncing");
            output.ShouldNotContain("↻");
            // Must contain valid JSON fields
            output.ShouldContain("\"id\": 1");
            output.ShouldContain("\"title\": \"JSON Parity Item\"");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    [Fact]
    public async Task Status_NoActiveItem_WithMatchingBranch_ShowsBranchDetectionHint()
    {
        var contextStore = Substitute.For<IContextStore>();
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var gitService = Substitute.For<IGitService>();
        var config = new TwigConfiguration
        {
            Seed = new SeedConfig { StaleDays = 14 },
            Git = new GitConfig { BranchPattern = BranchNameTemplate.DefaultPattern },
        };
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = true });

        contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-login");
        workItemRepo.ExistsByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(true);

        var adoService = Substitute.For<IAdoWorkItemService>();
        var activeItemResolver = new ActiveItemResolver(contextStore, workItemRepo, adoService);
        var pendingCs = Substitute.For<IPendingChangeStore>();
        var protectedWriter = new ProtectedCacheWriter(workItemRepo, pendingCs);
        var sc = new SyncCoordinator(workItemRepo, adoService, protectedWriter, 30);
        var iterSvc = Substitute.For<IIterationService>();
        iterSvc.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var wss = new WorkingSetService(contextStore, workItemRepo, pendingCs, iterSvc, null);

        var cmd = new StatusCommand(contextStore, workItemRepo, pendingChangeStore,
            config, formatterFactory, hintEngine, activeItemResolver, wss, sc, gitService: gitService);

        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var result = await cmd.ExecuteAsync();

            result.ShouldBe(1);
            var errOutput = stderr.ToString();
            errOutput.ShouldContain("twig set 12345");
            errOutput.ShouldContain("#12345");
        }
        finally
        {
            Console.SetError(savedErr);
        }
    }

    [Fact]
    public async Task Status_NoActiveItem_NoMatchingBranch_NoBranchHint()
    {
        var contextStore = Substitute.For<IContextStore>();
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var gitService = Substitute.For<IGitService>();
        var config = new TwigConfiguration
        {
            Seed = new SeedConfig { StaleDays = 14 },
            Git = new GitConfig { BranchPattern = BranchNameTemplate.DefaultPattern },
        };
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = true });

        contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("main");

        var adoService2 = Substitute.For<IAdoWorkItemService>();
        var activeItemResolver2 = new ActiveItemResolver(contextStore, workItemRepo, adoService2);
        var pendingCs2 = Substitute.For<IPendingChangeStore>();
        var protectedWriter2 = new ProtectedCacheWriter(workItemRepo, pendingCs2);
        var sc2 = new SyncCoordinator(workItemRepo, adoService2, protectedWriter2, 30);
        var iterSvc2 = Substitute.For<IIterationService>();
        iterSvc2.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var wss2 = new WorkingSetService(contextStore, workItemRepo, pendingCs2, iterSvc2, null);

        var cmd = new StatusCommand(contextStore, workItemRepo, pendingChangeStore,
            config, formatterFactory, hintEngine, activeItemResolver2, wss2, sc2, gitService: gitService);

        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var result = await cmd.ExecuteAsync();

            result.ShouldBe(1);
            var errOutput = stderr.ToString();
            errOutput.ShouldNotContain("branch matches");
            errOutput.ShouldContain("No active work item");
        }
        finally
        {
            Console.SetError(savedErr);
        }
    }
}
