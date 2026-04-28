using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ShowCommand_GitContextTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IWorkItemLinkRepository _linkRepo;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly ITelemetryClient _telemetryClient;
    private readonly IAdoGitService _adoGitService;
    private readonly StatusFieldConfigReader _statusFieldReader;
    private readonly CommandContext _ctx;
    private readonly string _tempDir;

    public ShowCommand_GitContextTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _linkRepo = Substitute.For<IWorkItemLinkRepository>();
        _telemetryClient = Substitute.For<ITelemetryClient>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _adoGitService = Substitute.For<IAdoGitService>();

        var adoService = Substitute.For<IAdoWorkItemService>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, adoService, protectedCacheWriter, pendingChangeStore, null, 30, 30);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        _tempDir = Path.Combine(Path.GetTempPath(), "twig-show-git-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db"));

        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var pipelineFactory = new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true);
        _ctx = new CommandContext(pipelineFactory, _formatterFactory, hintEngine, new TwigConfiguration(), TelemetryClient: _telemetryClient);
        _statusFieldReader = new StatusFieldConfigReader(paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── Factory helpers ─────────────────────────────────────────────

    private ShowCommand CreateCommand(TwigPaths? paths = null, IAdoGitService? gitService = null)
    {
        return new ShowCommand(
            _ctx, _workItemRepo, _linkRepo, _syncCoordinatorFactory, _statusFieldReader,
            fieldDefinitionStore: _fieldDefinitionStore,
            processConfigProvider: _processConfigProvider,
            twigPaths: paths,
            adoGitService: gitService);
    }

    private void SetupCachedItem(WorkItem item)
    {
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _linkRepo.GetLinksAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());
    }

    /// <summary>Creates a temp directory with a .git/HEAD pointing to a branch.</summary>
    private string CreateGitRepo(string branchName)
    {
        var repoDir = Path.Combine(_tempDir, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        var gitDir = Path.Combine(repoDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), $"ref: refs/heads/{branchName}\n");
        return repoDir;
    }

    private static async Task<string> CaptureStdout(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            await action();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Branch detection via GitBranchReader
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_WithGitRepo_IncludesBranchInJsonOutput()
    {
        var repoDir = CreateGitRepo("feature/123-test");
        var twigDir = Path.Combine(repoDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var item = new WorkItemBuilder(42, "Git Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(paths: paths, gitService: null);
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json"));

        output.ShouldContain("\"gitContext\"");
        output.ShouldContain("\"currentBranch\": \"feature/123-test\"");
        output.ShouldContain("\"linkedPullRequests\": []");
    }

    [Fact]
    public async Task Show_WithGitRepo_IncludesBranchInHumanOutput()
    {
        var repoDir = CreateGitRepo("main");
        var twigDir = Path.Combine(repoDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var item = new WorkItemBuilder(42, "Git Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(paths: paths, gitService: null);
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "human"));

        output.ShouldContain("Git");
        output.ShouldContain("Branch:");
        output.ShouldContain("main");
    }

    // ═══════════════════════════════════════════════════════════════
    //  PR lookup via IAdoGitService
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_WithLinkedPRs_IncludesPRsInJsonOutput()
    {
        var repoDir = CreateGitRepo("feature/456-pr");
        var twigDir = Path.Combine(repoDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var prs = new List<PullRequestInfo>
        {
            new(101, "Add feature X", "active", "refs/heads/feature/456-pr", "refs/heads/main", "https://dev.azure.com/pr/101")
        };
        _adoGitService.GetPullRequestsForBranchAsync("feature/456-pr", Arg.Any<CancellationToken>())
            .Returns(prs);

        var item = new WorkItemBuilder(42, "PR Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(paths: paths, gitService: _adoGitService);
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json"));

        output.ShouldContain("\"gitContext\"");
        output.ShouldContain("\"currentBranch\": \"feature/456-pr\"");
        output.ShouldContain("\"pullRequestId\": 101");
        output.ShouldContain("\"title\": \"Add feature X\"");
        output.ShouldContain("\"status\": \"active\"");
    }

    [Fact]
    public async Task Show_WithLinkedPRs_IncludesPRsInHumanOutput()
    {
        var repoDir = CreateGitRepo("feature/456-pr");
        var twigDir = Path.Combine(repoDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var prs = new List<PullRequestInfo>
        {
            new(101, "Add feature X", "active", "refs/heads/feature/456-pr", "refs/heads/main", "https://dev.azure.com/pr/101")
        };
        _adoGitService.GetPullRequestsForBranchAsync("feature/456-pr", Arg.Any<CancellationToken>())
            .Returns(prs);

        var item = new WorkItemBuilder(42, "PR Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(paths: paths, gitService: _adoGitService);
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "human"));

        output.ShouldContain("PR:");
        output.ShouldContain("!101");
        output.ShouldContain("Add feature X");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Best-effort: PR lookup failures are non-fatal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_PRLookupFails_StillSucceedsWithBranch()
    {
        var repoDir = CreateGitRepo("feature/error-branch");
        var twigDir = Path.Combine(repoDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        _adoGitService.GetPullRequestsForBranchAsync("feature/error-branch", Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Network error"));

        var item = new WorkItemBuilder(42, "Error Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(paths: paths, gitService: _adoGitService);
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json"));

        output.ShouldContain("\"currentBranch\": \"feature/error-branch\"");
        output.ShouldContain("\"linkedPullRequests\": []");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No TwigPaths / No .git directory — graceful degradation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_NoTwigPaths_OmitsGitContext()
    {
        var item = new WorkItemBuilder(42, "No Paths Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(paths: null, gitService: _adoGitService);
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json"));

        output.ShouldNotContain("\"gitContext\"");
    }

    [Fact]
    public async Task Show_NoGitDirectory_OmitsGitContext()
    {
        // TwigPaths points to a dir without .git
        var noGitDir = Path.Combine(_tempDir, "no-git-" + Guid.NewGuid().ToString("N")[..8]);
        var twigDir = Path.Combine(noGitDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var item = new WorkItemBuilder(42, "No Git Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(paths: paths, gitService: _adoGitService);
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json"));

        output.ShouldNotContain("\"gitContext\"");
    }

    [Fact]
    public async Task Show_DetachedHead_OmitsGitContext()
    {
        var repoDir = Path.Combine(_tempDir, "detached-" + Guid.NewGuid().ToString("N")[..8]);
        var gitDir = Path.Combine(repoDir, ".git");
        Directory.CreateDirectory(gitDir);
        // Detached HEAD — raw SHA, not a ref
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "abc123def456\n");
        var twigDir = Path.Combine(repoDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var item = new WorkItemBuilder(42, "Detached Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(paths: paths, gitService: _adoGitService);
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json"));

        output.ShouldNotContain("\"gitContext\"");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No IAdoGitService — branch only, no PR lookup
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_NoGitService_ReturnsEmptyPRList()
    {
        var repoDir = CreateGitRepo("feature/no-git-svc");
        var twigDir = Path.Combine(repoDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var item = new WorkItemBuilder(42, "No Svc Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(paths: paths, gitService: null);
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json"));

        output.ShouldContain("\"currentBranch\": \"feature/no-git-svc\"");
        output.ShouldContain("\"linkedPullRequests\": []");
        await _adoGitService.DidNotReceive().GetPullRequestsForBranchAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Multiple PRs
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_MultiplePRs_AllAppearInOutput()
    {
        var repoDir = CreateGitRepo("feature/multi-pr");
        var twigDir = Path.Combine(repoDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var prs = new List<PullRequestInfo>
        {
            new(201, "First PR", "active", "refs/heads/feature/multi-pr", "refs/heads/main", "https://ado/pr/201"),
            new(202, "Second PR", "active", "refs/heads/feature/multi-pr", "refs/heads/develop", "https://ado/pr/202")
        };
        _adoGitService.GetPullRequestsForBranchAsync("feature/multi-pr", Arg.Any<CancellationToken>())
            .Returns(prs);

        var item = new WorkItemBuilder(42, "Multi PR Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(paths: paths, gitService: _adoGitService);
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json"));

        output.ShouldContain("\"pullRequestId\": 201");
        output.ShouldContain("\"pullRequestId\": 202");
        output.ShouldContain("\"title\": \"First PR\"");
        output.ShouldContain("\"title\": \"Second PR\"");
    }
}
