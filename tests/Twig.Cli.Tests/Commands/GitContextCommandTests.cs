using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
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
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly IGitService _gitService;
    private readonly IAdoGitService _adoGitService;

    public GitContextCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _gitService = Substitute.For<IGitService>();
        _adoGitService = Substitute.For<IAdoGitService>();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration();
    }

    private GitContextCommand CreateCommand(
        IGitService? gitService = null, IAdoGitService? adoGitService = null) =>
        new(_contextStore, _workItemRepo, _formatterFactory, _hintEngine, _config,
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

        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("human");
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(original);
        }

        sw.ToString().ShouldContain("not in a git repository");
        sw.ToString().ShouldContain("Context: (none)");
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

        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("human");
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(original);
        }

        var output = sw.ToString();
        output.ShouldContain("Branch: bug/42-fix-login");
        output.ShouldContain("#42");
        output.ShouldContain("Fix login");
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

        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("human");
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(original);
        }

        var output = sw.ToString();
        output.ShouldContain("Context: #100");
        output.ShouldContain("Detected from branch: #200");
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

        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("human");
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(original);
        }

        var output = sw.ToString();
        output.ShouldContain("Linked PRs:");
        output.ShouldContain("PR !101");
        output.ShouldContain("PR: Fix login");
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

        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("json");
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(original);
        }

        var json = sw.ToString().Trim();
        json.ShouldContain("\"command\":\"context\"");
        json.ShouldContain("\"branch\":\"bug/42-fix-login\"");
        json.ShouldContain("\"activeWorkItem\"");
        json.ShouldContain("\"id\":42");
        json.ShouldContain("\"pullRequests\"");
        json.ShouldContain("\"exitCode\":0");
    }

    // ── JSON format: null git service ───────────────────────────────

    [Fact]
    public async Task Json_NoGitService_ReturnsNullBranch()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var cmd = CreateCommand(gitService: null);

        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("json");
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(original);
        }

        var json = sw.ToString().Trim();
        json.ShouldContain("\"branch\":null");
        json.ShouldContain("\"activeWorkItem\":null");
        json.ShouldContain("\"detectedWorkItemId\":null");
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

        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("minimal");
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(original);
        }

        var lines = sw.ToString().Trim().Split(Environment.NewLine);
        lines.Length.ShouldBe(2);
        lines[0].ShouldBe("bug/42-fix-login");
        lines[1].ShouldBe("42");
    }

    // ── Minimal format: no branch, no context ───────────────────────

    [Fact]
    public async Task Minimal_NoBranchNoContext_EmptyOutput()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var cmd = CreateCommand(gitService: null);

        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("minimal");
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(original);
        }

        sw.ToString().Trim().ShouldBeEmpty();
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

        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("human");
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(original);
        }

        var output = sw.ToString();
        output.ShouldContain("Branch: main");
        output.ShouldContain("Context: (none)");
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
}
