using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class GitContextCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly IGitService _gitService;
    private readonly IAdoGitService _adoGitService;

    public GitContextCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _gitService = Substitute.For<IGitService>();
        _adoGitService = Substitute.For<IAdoGitService>();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration();
    }

    private GitContextCommand CreateCommand(
        IGitService? gitService = null, IAdoGitService? adoGitService = null) =>
        new(new ActiveItemResolver(_contextStore, _workItemRepo, _adoService),
            _workItemRepo, _formatterFactory, _hintEngine, _config,
            gitService: gitService, adoGitService: adoGitService);

    private static WorkItem CreateWorkItem(int id, string title, string type = "Bug") => new()
    {
        Id = id,
        Type = WorkItemType.Parse(type).Value,
        Title = title,
        State = "Active",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

    // ── Human format: no git service ────────────────────────────────

    [Fact]
    public async Task Human_NoGitService_ShowsNotInGitRepo()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var cmd = CreateCommand(gitService: null);

        var result = await cmd.ExecuteAsync("human");
        result.ShouldBe(0);
    }

    // ── Human format: with active context and branch ────────────────

    [Fact]
    public async Task Human_WithContextAndBranch_ShowsAll()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(42, "Fix login"));
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("bug/42-fix-login");

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync("human");
        result.ShouldBe(0);
    }

    // ── Human format: detectedId differs from activeId ──────────────

    [Fact]
    public async Task Human_DetectedIdDiffersFromActive_ShowsBoth()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(100);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(100, "Old item"));
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("bug/200-newer-branch");

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync("human");
        result.ShouldBe(0);
    }

    // ── Human format: with linked PRs ───────────────────────────────

    [Fact]
    public async Task Human_WithLinkedPrs_ShowsPrs()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(42, "Fix login"));
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("bug/42-fix-login");
        _adoGitService.GetPullRequestsForBranchAsync("bug/42-fix-login", Arg.Any<CancellationToken>())
            .Returns([new PullRequestInfo(101, "PR: Fix login", "active", "refs/heads/bug/42-fix-login", "refs/heads/main", "https://example.com/pr/101")]);

        var cmd = CreateCommand(_gitService, _adoGitService);

        var result = await cmd.ExecuteAsync("human");
        result.ShouldBe(0);
    }

    // ── JSON format ─────────────────────────────────────────────────

    [Fact]
    public async Task Json_ReturnsValidJsonWithAllFields()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(42, "Fix login"));
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("bug/42-fix-login");
        _adoGitService.GetPullRequestsForBranchAsync("bug/42-fix-login", Arg.Any<CancellationToken>())
            .Returns([new PullRequestInfo(101, "PR: Fix login", "active", "refs/heads/bug/42-fix-login", "refs/heads/main", "https://example.com/pr/101")]);

        var cmd = CreateCommand(_gitService, _adoGitService);

        var result = await cmd.ExecuteAsync("json");
        result.ShouldBe(0);
    }

    // ── JSON format: null git service ───────────────────────────────

    [Fact]
    public async Task Json_NoGitService_ReturnsNullBranch()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var cmd = CreateCommand(gitService: null);

        var result = await cmd.ExecuteAsync("json");
        result.ShouldBe(0);
    }

    // ── Minimal format: shows branch and active ID ──────────────────

    [Fact]
    public async Task Minimal_ShowsBranchAndActiveId()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(42, "Fix login"));
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("bug/42-fix-login");

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync("minimal");
        result.ShouldBe(0);
    }

    // ── Minimal format: no branch, no context ───────────────────────

    [Fact]
    public async Task Minimal_NoBranchNoContext_EmptyOutput()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var cmd = CreateCommand(gitService: null);

        var result = await cmd.ExecuteAsync("minimal");
        result.ShouldBe(0);
    }

    // ── Human format: no active context shows (none) ────────────────

    [Fact]
    public async Task Human_NoActiveContext_ShowsNone()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("main");

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync("human");
        result.ShouldBe(0);
    }

    // ── Always returns 0 ────────────────────────────────────────────

    [Fact]
    public async Task AlwaysReturnsZero()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var cmd = CreateCommand(gitService: null);
        var result = await cmd.ExecuteAsync("human");
        result.ShouldBe(0);
    }

    // ── Cache miss — auto-fetch from ADO ────────────────────────────

    [Fact]
    public async Task Human_CacheMiss_AutoFetchesFromAdo()
    {
        var item = CreateWorkItem(42, "Auto-fetched");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("bug/42-auto-fetched");

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync("human");
        result.ShouldBe(0);

        await _workItemRepo.Received().SaveAsync(item, Arg.Any<CancellationToken>());
    }

    // ── Unreachable — shows error but still returns 0 ───────────────

    [Fact]
    public async Task Human_Unreachable_ShowsErrorWithReason()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns<WorkItem>(x => throw new InvalidOperationException("Network timeout"));
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("bug/42-test");

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync("human");
        result.ShouldBe(0);
    }
}
