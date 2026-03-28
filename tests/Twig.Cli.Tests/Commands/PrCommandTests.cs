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
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class PrCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly IGitService _gitService;
    private readonly IAdoGitService _adoGitService;

    public PrCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _gitService = Substitute.For<IGitService>();
        _adoGitService = Substitute.For<IAdoGitService>();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration
        {
            Git = new GitConfig
            {
                DefaultTarget = "main",
                AutoLink = true,
            },
        };
    }

    private PrCommand CreateCommand(IGitService? gitService = null, IAdoGitService? adoGitService = null) =>
        new(new ActiveItemResolver(_contextStore, _workItemRepo, _adoService),
            _adoService,
            _formatterFactory, _hintEngine, _config,
            gitService: gitService, adoGitService: adoGitService);

    private static WorkItem CreateWorkItem(int id, string title, string type = "User Story") => new()
    {
        Id = id,
        Type = WorkItemType.Parse(type).Value,
        Title = title,
        State = "Active",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

    private static PullRequestInfo CreatePrInfo(int prId = 1, string url = "https://dev.azure.com/pr/1") =>
        new(prId, "Test PR", "active", "refs/heads/feature/123", "refs/heads/main", url);

    // ── Happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_CreatesPr_LinksToWorkItem()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-add-login");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(CreatePrInfo(42, "https://dev.azure.com/pr/42"));
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("project-id");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);

        // PR created with correct parameters
        await _adoGitService.Received().CreatePullRequestAsync(
            Arg.Is<PullRequestCreate>(r =>
                r.SourceBranch == "refs/heads/feature/12345-add-login" &&
                r.TargetBranch == "refs/heads/main" &&
                r.Title.Contains("12345") &&
                r.WorkItemId == 12345 &&
                !r.IsDraft),
            Arg.Any<CancellationToken>());

        // Artifact link added
        await _adoGitService.Received().AddArtifactLinkAsync(
            12345,
            Arg.Is<string>(s => s.Contains("42") && s.StartsWith("vstfs:///Git/PullRequestId/")),
            "ArtifactLink",
            Arg.Any<int>(),
            "Pull Request",
            Arg.Any<CancellationToken>());
    }

    // ── --draft flag ────────────────────────────────────────────────

    [Fact]
    public async Task DraftFlag_CreatesDraftPr()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-add-login");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(CreatePrInfo());
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(draft: true);

        result.ShouldBe(0);

        await _adoGitService.Received().CreatePullRequestAsync(
            Arg.Is<PullRequestCreate>(r => r.IsDraft),
            Arg.Any<CancellationToken>());
    }

    // ── --target override ───────────────────────────────────────────

    [Fact]
    public async Task TargetOverride_UsesSpecifiedBranch()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-add-login");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(CreatePrInfo());
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(target: "develop");

        result.ShouldBe(0);

        await _adoGitService.Received().CreatePullRequestAsync(
            Arg.Is<PullRequestCreate>(r => r.TargetBranch == "refs/heads/develop"),
            Arg.Any<CancellationToken>());
    }

    // ── --title override ────────────────────────────────────────────

    [Fact]
    public async Task TitleOverride_UsesSpecifiedTitle()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-add-login");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(CreatePrInfo());
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(title: "Custom PR title");

        result.ShouldBe(0);

        await _adoGitService.Received().CreatePullRequestAsync(
            Arg.Is<PullRequestCreate>(r => r.Title == "Custom PR title"),
            Arg.Any<CancellationToken>());
    }

    // ── No active work item ─────────────────────────────────────────

    [Fact]
    public async Task NoActiveWorkItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        await _adoGitService.DidNotReceive().CreatePullRequestAsync(
            Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>());
    }

    // ── Work item not in cache — auto-fetch from ADO ──────────────

    [Fact]
    public async Task WorkItemNotInCache_AutoFetchesFromAdo()
    {
        var item = CreateWorkItem(999, "Auto-fetched");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(999);
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/999-test");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(CreatePrInfo());
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(item, Arg.Any<CancellationToken>());
    }

    // ── Work item unreachable — ADO fetch fails ─────────────────────

    [Fact]
    public async Task WorkItemUnreachable_ReturnsErrorWithReason()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(999);
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .Returns<WorkItem>(x => throw new InvalidOperationException("Network timeout"));

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        await _adoGitService.DidNotReceive().CreatePullRequestAsync(
            Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>());
    }

    // ── No git service ──────────────────────────────────────────────

    [Fact]
    public async Task NoGitService_ReturnsError()
    {
        var item = CreateWorkItem(100, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(100);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = CreateCommand(gitService: null, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    // ── No ADO Git service ──────────────────────────────────────────

    [Fact]
    public async Task NoAdoGitService_ReturnsError()
    {
        var item = CreateWorkItem(100, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(100);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService, adoGitService: null);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    // ── Not inside work tree ────────────────────────────────────────

    [Fact]
    public async Task NotInsideWorkTree_ReturnsError()
    {
        var item = CreateWorkItem(100, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(100);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    // ── PR creation failure ─────────────────────────────────────────

    [Fact]
    public async Task PrCreationFailure_ReturnsError()
    {
        var item = CreateWorkItem(12345, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/test");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("API error"));

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    // ── Artifact link failure is best-effort ────────────────────────

    [Fact]
    public async Task ArtifactLinkFailure_DoesNotFailCommand()
    {
        var item = CreateWorkItem(12345, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/test");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(CreatePrInfo());
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    // ── PR description format ───────────────────────────────────────

    [Fact]
    public async Task DefaultDescription_IncludesResolvesAbHashTypeAndState()
    {
        var item = CreateWorkItem(12345, "Add login", "Bug");
        // Manually set state to Active via object initializer (done in CreateWorkItem)
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("bugfix/12345-add-login");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(CreatePrInfo());
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);

        await _adoGitService.Received().CreatePullRequestAsync(
            Arg.Is<PullRequestCreate>(r =>
                r.Description.StartsWith("Resolves AB#12345.") &&
                r.Description.Contains("**Type:**") &&
                r.Description.Contains("**State:**") &&
                r.Description.Contains("Active")),
            Arg.Any<CancellationToken>());
    }

    // ── PR title from work item ─────────────────────────────────────

    [Fact]
    public async Task DefaultTitle_IncludesWorkItemIdAndTitle()
    {
        var item = CreateWorkItem(12345, "Implement OAuth2");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(CreatePrInfo());
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);

        await _adoGitService.Received().CreatePullRequestAsync(
            Arg.Is<PullRequestCreate>(r =>
                r.Title.Contains("#12345") &&
                r.Title.Contains("Implement OAuth2") &&
                r.Description.Contains("AB#12345")),
            Arg.Any<CancellationToken>());
    }

    // ── JSON output format ──────────────────────────────────────────

    [Fact]
    public async Task JsonOutput_ContainsStructuredFields()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/test");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(CreatePrInfo(42, "https://dev.azure.com/pr/42"));
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        var cmd = CreateCommand(_gitService, _adoGitService);

        var result = await cmd.ExecuteAsync(outputFormat: "json");
        result.ShouldBe(0);
    }

    // ── Minimal output format ───────────────────────────────────────

    [Fact]
    public async Task MinimalOutput_PrintsPrUrlOnly()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/test");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(CreatePrInfo(42, "https://dev.azure.com/pr/42"));
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        var cmd = CreateCommand(_gitService, _adoGitService);

        var result = await cmd.ExecuteAsync(outputFormat: "minimal");
        result.ShouldBe(0);
    }

    // ── AutoLink disabled → no artifact link ────────────────────────

    [Fact]
    public async Task AutoLinkDisabled_SkipsArtifactLink()
    {
        var item = CreateWorkItem(12345, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/test");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(CreatePrInfo());

        _config.Git.AutoLink = false;

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoGitService.DidNotReceive().AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── HTTP 409 Conflict from ADO ──────────────────────────────────

    [Fact]
    public async Task PrCreationFails_WithHttpConflict_ReturnsError()
    {
        var item = CreateWorkItem(12345, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-test");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException(
                "An active pull request for the source and target branch already exists.",
                null,
                System.Net.HttpStatusCode.Conflict));

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        await _adoGitService.DidNotReceive().AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── HTTP 429 Rate Limit from ADO ────────────────────────────────

    [Fact]
    public async Task PrCreationFails_WithHttpRateLimit_ReturnsError()
    {
        var item = CreateWorkItem(12345, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-test");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException(
                "Rate limit exceeded.",
                null,
                System.Net.HttpStatusCode.TooManyRequests));

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    // ── ConfigDefaultTarget used when no --target specified ─────────

    [Fact]
    public async Task ConfigDefaultTarget_UsedWhenTargetNotSpecified()
    {
        _config.Git.DefaultTarget = "release/v2";

        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-add-login");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(CreatePrInfo());
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(); // no target override

        result.ShouldBe(0);

        await _adoGitService.Received().CreatePullRequestAsync(
            Arg.Is<PullRequestCreate>(r => r.TargetBranch == "refs/heads/release/v2"),
            Arg.Any<CancellationToken>());
    }

    // ── EPIC-003: Error resilience — auth failure and merge policy blocked ──

    [Fact]
    public async Task PrCreation_AuthFailure_PrintsActionableMessage()
    {
        // EPIC-003 Task 9: AdoAuthenticationException should produce an actionable
        // message about checking credentials, not a generic "Failed to create pull request".
        var item = CreateWorkItem(12345, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-test");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoAuthenticationException());

        var cmd = CreateCommand(_gitService, _adoGitService);

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task PrCreation_MergePolicyBlocked_IncludesPolicyReason()
    {
        // EPIC-003 Task 10: AdoBadRequestException with merge policy details
        // should surface the policy name/reason in the command output.
        var item = CreateWorkItem(12345, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-test");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoBadRequestException(
                "TF401027: You need at least one reviewer to approve before the pull request can be completed. Required policy: Minimum number of reviewers (2)."));

        var cmd = CreateCommand(_gitService, _adoGitService);

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }
}
