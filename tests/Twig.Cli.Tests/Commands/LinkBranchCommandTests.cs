using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class LinkBranchCommandTests : IDisposable
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoWorkItemService;
    private readonly IAdoGitService _adoGitService;
    private readonly IGitService _gitService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly StringWriter _stderr;
    private readonly StringWriter _stdout;
    private readonly TextWriter _originalOut;

    public LinkBranchCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoWorkItemService = Substitute.For<IAdoWorkItemService>();
        _adoGitService = Substitute.For<IAdoGitService>();
        _gitService = Substitute.For<IGitService>();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(),
            new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()),
            new MinimalOutputFormatter());

        _stderr = new StringWriter();
        _originalOut = Console.Out;
        _stdout = new StringWriter();
        Console.SetOut(_stdout);

        // Default: git repo is valid
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
    }

    private LinkBranchCommand CreateCommand(IGitService? gitService = null)
    {
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoWorkItemService);
        var branchLinkService = new BranchLinkService(_adoGitService, _adoWorkItemService);
        return new LinkBranchCommand(resolver, branchLinkService, _formatterFactory, gitService ?? _gitService, _stderr);
    }

    private void SetActiveItem(int id, string title = "Test Item")
    {
        var item = new WorkItemBuilder(id, title).InState("Active").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(id);
        _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(item);
    }

    private void SetBranchExists(string branchName, bool exists = true)
    {
        _gitService.BranchExistsAsync(branchName, Arg.Any<CancellationToken>()).Returns(exists);
    }

    private void SetLinkSuccess(bool alreadyExisted = false)
    {
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("proj-id");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");
        _adoWorkItemService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(alreadyExisted);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — branch linked successfully
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_BranchExists_LinksAndOutputsSuccess()
    {
        SetActiveItem(42);
        SetBranchExists("feature/my-branch");
        SetLinkSuccess();

        var result = await CreateCommand().ExecuteAsync("feature/my-branch");

        result.ShouldBe(0);
        _stdout.ToString().ShouldContain("Linked");
        _stdout.ToString().ShouldContain("feature/my-branch");
        _stdout.ToString().ShouldContain("#42");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Already linked — idempotent success
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_AlreadyLinked_ReturnsZeroWithMessage()
    {
        SetActiveItem(42);
        SetBranchExists("feature/my-branch");
        SetLinkSuccess(alreadyExisted: true);

        var result = await CreateCommand().ExecuteAsync("feature/my-branch");

        result.ShouldBe(0);
        _stdout.ToString().ShouldContain("Already linked");
    }

    // ═══════════════════════════════════════════════════════════════
    //  --id override — bypasses active item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_WithIdOverride_ResolvesById()
    {
        var item = new WorkItemBuilder(99, "Target Item").InState("Active").Build();
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(item);
        SetBranchExists("feature/target-branch");
        SetLinkSuccess();

        var result = await CreateCommand().ExecuteAsync("feature/target-branch", id: 99);

        result.ShouldBe(0);
        await _contextStore.DidNotReceive().GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
        _stdout.ToString().ShouldContain("#99");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No active item — error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await CreateCommand().ExecuteAsync("feature/any-branch");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Item not found — error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_ItemNotFoundById_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((Domain.Aggregates.WorkItem?)null);
        _adoWorkItemService.FetchAsync(999, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Not found"));

        var result = await CreateCommand().ExecuteAsync("feature/any-branch", id: 999);

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("#999");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty branch name — error
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_EmptyBranchName_ReturnsError(string branchName)
    {
        SetActiveItem(42);

        var result = await CreateCommand().ExecuteAsync(branchName);

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("Branch name is required");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Git unavailable — error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NoGitService_ReturnsError()
    {
        SetActiveItem(42);

        var cmd = new LinkBranchCommand(
            new ActiveItemResolver(_contextStore, _workItemRepo, _adoWorkItemService),
            new BranchLinkService(_adoGitService, _adoWorkItemService),
            _formatterFactory,
            gitService: null,
            stderr: _stderr);

        var result = await cmd.ExecuteAsync("feature/my-branch");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("Git is not available");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Git repo available but ADO git project not configured — error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_BranchLinkServiceNull_ReturnsError()
    {
        SetActiveItem(42);

        var cmd = new LinkBranchCommand(
            new ActiveItemResolver(_contextStore, _workItemRepo, _adoWorkItemService),
            branchLinkService: null,
            _formatterFactory,
            gitService: _gitService,
            stderr: _stderr);

        var result = await cmd.ExecuteAsync("feature/my-branch");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("Git project is not configured");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Not in git repo — error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NotInGitRepo_ReturnsError()
    {
        SetActiveItem(42);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var result = await CreateCommand().ExecuteAsync("feature/my-branch");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("Not inside a git repository");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Branch not found — error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_BranchNotFound_ReturnsError()
    {
        SetActiveItem(42);
        SetBranchExists("feature/nonexistent", exists: false);

        var result = await CreateCommand().ExecuteAsync("feature/nonexistent");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("feature/nonexistent");
        _stderr.ToString().ShouldContain("not found");
    }

    // ═══════════════════════════════════════════════════════════════
    //  BranchExistsAsync throws — error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_BranchExistsCheckThrows_ReturnsError()
    {
        SetActiveItem(42);
        _gitService.BranchExistsAsync("feature/broken", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git error"));

        var result = await CreateCommand().ExecuteAsync("feature/broken");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("Failed to check branch existence");
        _stderr.ToString().ShouldContain("git error");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Link failure — git context unavailable
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_GitContextUnavailable_ReturnsError()
    {
        SetActiveItem(42);
        SetBranchExists("feature/my-branch");
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await CreateCommand().ExecuteAsync("feature/my-branch");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("Failed to link");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Link failure — API error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_LinkApiThrows_ReturnsError()
    {
        SetActiveItem(42);
        SetBranchExists("feature/my-branch");
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("proj-id");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");
        _adoWorkItemService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var result = await CreateCommand().ExecuteAsync("feature/my-branch");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("Failed to link");
        _stderr.ToString().ShouldContain("Network error");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Minimal output format
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_MinimalFormat_OutputsBranchNameOnly()
    {
        SetActiveItem(42);
        SetBranchExists("feature/my-branch");
        SetLinkSuccess();

        var result = await CreateCommand().ExecuteAsync("feature/my-branch", outputFormat: "minimal");

        result.ShouldBe(0);
        _stdout.ToString().Trim().ShouldBe("feature/my-branch");
    }

    // ═══════════════════════════════════════════════════════════════
    //  All output formats succeed
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    [InlineData("json-compact")]
    [InlineData("minimal")]
    public async Task ExecuteAsync_AllFormats_Succeed(string format)
    {
        SetActiveItem(42);
        SetBranchExists("feature/my-branch");
        SetLinkSuccess();

        var result = await CreateCommand().ExecuteAsync("feature/my-branch", outputFormat: format);

        result.ShouldBe(0);
        _stdout.ToString().Length.ShouldBeGreaterThan(0);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _stderr.Dispose();
        _stdout.Dispose();
    }
}
