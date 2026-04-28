using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class CommitCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly IGitService _gitService;
    private readonly IAdoGitService _adoGitService;

    public CommitCommandTests()
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
                CommitTemplate = "{type}(#{id}): {message}",
                AutoLink = true,
            },
        };
    }

    private CommitCommand CreateCommand(IGitService? gitService = null, IAdoGitService? adoGitService = null) =>
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

    // ── Happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_FormatsMessage_Commits_LinksArtifact()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("abc123def456");
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("project-id");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(message: "implement auth");

        result.ShouldBe(0);

        // Commit was called with formatted message
        await _gitService.Received().CommitAsync(
            "feat(#12345): implement auth",
            Arg.Any<bool>(), Arg.Any<CancellationToken>());

        // Artifact link added
        await _adoGitService.Received().AddArtifactLinkAsync(
            12345,
            Arg.Is<string>(s => s.Contains("abc123def456") && s.StartsWith("vstfs:///Git/Commit/")),
            "ArtifactLink",
            Arg.Any<int>(),
            "Fixed in Commit",
            Arg.Any<CancellationToken>());
    }

    // ── Bug type maps to fix ────────────────────────────────────────

    [Fact]
    public async Task BugType_MapsToFix()
    {
        var item = CreateWorkItem(42, "Login crash", "Bug");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("abc123");

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync(message: "fix null ref", noLink: true);

        result.ShouldBe(0);
        await _gitService.Received().CommitAsync(
            "fix(#42): fix null ref",
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── No active work item ─────────────────────────────────────────

    [Fact]
    public async Task NoActiveWorkItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(message: "test");

        result.ShouldBe(1);
        await _gitService.DidNotReceive().CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
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
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("abc123");

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync(message: "test", noLink: true);

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
        var result = await cmd.ExecuteAsync(message: "test");

        result.ShouldBe(1);
        await _gitService.DidNotReceive().CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── No git service ──────────────────────────────────────────────

    [Fact]
    public async Task NoGitService_ReturnsError()
    {
        var item = CreateWorkItem(100, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(100);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = CreateCommand(gitService: null);
        var result = await cmd.ExecuteAsync(message: "test");

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

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync(message: "test");

        result.ShouldBe(1);
    }

    // ── --no-link flag ──────────────────────────────────────────────

    [Fact]
    public async Task NoLinkFlag_SkipsArtifactLink()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("abc123");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(message: "test", noLink: true);

        result.ShouldBe(0);
        await _adoGitService.DidNotReceive().AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── AutoLink disabled ───────────────────────────────────────────

    [Fact]
    public async Task AutoLinkDisabled_SkipsArtifactLink()
    {
        var item = CreateWorkItem(12345, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("abc123");

        _config.Git.AutoLink = false;

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(message: "test");

        result.ShouldBe(0);
        await _adoGitService.DidNotReceive().AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Artifact link failure is best-effort ────────────────────────

    [Fact]
    public async Task ArtifactLinkFailure_DoesNotFailCommand()
    {
        var item = CreateWorkItem(12345, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("abc123");
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(message: "test");

        result.ShouldBe(0);
    }

    // ── No AdoGitService → skips linking ────────────────────────────

    [Fact]
    public async Task NoAdoGitService_SkipsLinking()
    {
        var item = CreateWorkItem(12345, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("abc123");

        var cmd = CreateCommand(_gitService, adoGitService: null);
        var result = await cmd.ExecuteAsync(message: "test");

        result.ShouldBe(0);
        await _gitService.Received().CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── Empty message ───────────────────────────────────────────────

    [Fact]
    public async Task EmptyMessage_StillFormatsTemplate()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("abc123");

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync(message: null, noLink: true);

        result.ShouldBe(0);
        await _gitService.Received().CommitAsync(
            "feat(#12345): ",
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── JSON output format ──────────────────────────────────────────

    [Fact]
    public async Task JsonOutput_ContainsStructuredFields()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("abc123def");

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync(message: "test", noLink: true, outputFormat: "json");
        result.ShouldBe(0);
    }

    // ── Minimal output format ───────────────────────────────────────

    [Fact]
    public async Task MinimalOutput_PrintsCommitHashOnly()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("abc123def456");

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync(message: "test", noLink: true, outputFormat: "minimal");
        result.ShouldBe(0);
    }

    // ── Git commit failure ──────────────────────────────────────────

    [Fact]
    public async Task GitCommitFailure_ReturnsError()
    {
        var item = CreateWorkItem(12345, "Test");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("nothing to commit"));

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync(message: "test");

        result.ShouldBe(1);
    }

    // ── Passthrough args forwarded ──────────────────────────────────

    [Fact]
    public async Task PassthroughArgs_ForwardedViaCommitWithArgs()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitWithArgsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns("abc123");

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync(message: "test", noLink: true, passthrough: new[] { "--amend" });

        result.ShouldBe(0);
        await _gitService.Received().CommitWithArgsAsync(
            "feat(#12345): test",
            Arg.Is<IReadOnlyList<string>>(args => args.Count == 1 && args[0] == "--amend"),
            Arg.Any<CancellationToken>());
        // Should NOT call the simple CommitAsync
        await _gitService.DidNotReceive().CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoPassthroughArgs_UsesSimpleCommit()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("abc123");

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync(message: "test", noLink: true, passthrough: null);

        result.ShouldBe(0);
        await _gitService.Received().CommitAsync(
            "feat(#12345): test",
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().CommitWithArgsAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    // ── Empty passthrough array (params produces empty array, not null) ──

    [Fact]
    public async Task EmptyPassthroughArray_UsesSimpleCommit()
    {
        var item = CreateWorkItem(12345, "Add login");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.CommitAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns("abc123");

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync(message: "test", noLink: true, passthrough: Array.Empty<string>());

        result.ShouldBe(0);
        await _gitService.Received().CommitAsync(
            "feat(#12345): test",
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().CommitWithArgsAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
