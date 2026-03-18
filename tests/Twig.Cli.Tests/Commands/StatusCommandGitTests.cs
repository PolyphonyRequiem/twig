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
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for StatusCommand git context enrichment (EPIC-006 ITEM-035).
/// </summary>
public class StatusCommandGitTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly IGitService _gitService;
    private readonly IAdoGitService _adoGitService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;

    public StatusCommandGitTests()
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
        _gitService = Substitute.For<IGitService>();
        _adoGitService = Substitute.For<IAdoGitService>();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration { Seed = new SeedConfig { StaleDays = 14 } };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
    }

    private StatusCommand CreateCommand(IGitService? git = null, IAdoGitService? adoGit = null) =>
        new(_contextStore, _workItemRepo, _pendingChangeStore, _config,
            _formatterFactory, _hintEngine, _activeItemResolver, _workingSetService, _syncCoordinator,
            pipelineFactory: null, gitService: git, adoGitService: adoGit);

    private static WorkItem CreateWorkItem(int id, string title) => new()
    {
        Id = id,
        Type = WorkItemType.UserStory,
        Title = title,
        State = "Active",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

    // ── Branch info shown ───────────────────────────────────────────

    [Fact]
    public async Task Status_WithGit_ShowsBranchName()
    {
        var item = CreateWorkItem(42, "Test item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/42-test-item");

        var cmd = CreateCommand(_gitService);

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync();
            result.ShouldBe(0);
            sw.ToString().ShouldContain("feature/42-test-item");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    // ── PR info shown ───────────────────────────────────────────────

    [Fact]
    public async Task Status_WithPRs_ShowsPrStatus()
    {
        var item = CreateWorkItem(42, "Test item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/42-test-item");
        _adoGitService.GetPullRequestsForBranchAsync("feature/42-test-item", Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PullRequestInfo(101, "PR Title", "active",
                    "refs/heads/feature/42-test-item", "refs/heads/main", "https://dev.azure.com/pr/101"),
            });

        var cmd = CreateCommand(_gitService, _adoGitService);

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync();
            result.ShouldBe(0);
            var output = sw.ToString();
            output.ShouldContain("PR !101");
            output.ShouldContain("PR Title");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    // ── Graceful degradation — no git service ───────────────────────

    [Fact]
    public async Task Status_NoGitService_StillWorks()
    {
        var item = CreateWorkItem(42, "Test item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var cmd = CreateCommand(git: null);

        var result = await cmd.ExecuteAsync();
        result.ShouldBe(0);
    }

    // ── Graceful degradation — not in work tree ─────────────────────

    [Fact]
    public async Task Status_NotInWorkTree_StillWorks()
    {
        var item = CreateWorkItem(42, "Test item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync();
        result.ShouldBe(0);
    }

    // ── Graceful degradation — git throws ───────────────────────────

    [Fact]
    public async Task Status_GitThrows_StillSucceeds()
    {
        var item = CreateWorkItem(42, "Test item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git not found"));

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync();
        result.ShouldBe(0);
    }

    // ── Graceful degradation — PR lookup fails ──────────────────────

    [Fact]
    public async Task Status_PrLookupFails_StillSucceeds()
    {
        var item = CreateWorkItem(42, "Test item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("main");
        _adoGitService.GetPullRequestsForBranchAsync("main", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var cmd = CreateCommand(_gitService, _adoGitService);

        var result = await cmd.ExecuteAsync();
        result.ShouldBe(0);
    }
}
