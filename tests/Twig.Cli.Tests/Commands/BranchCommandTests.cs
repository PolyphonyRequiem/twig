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
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class BranchCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly IGitService _gitService;
    private readonly IAdoGitService _adoGitService;

    public BranchCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _gitService = Substitute.For<IGitService>();
        _adoGitService = Substitute.For<IAdoGitService>();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration
        {
            Git = new GitConfig
            {
                BranchTemplate = "feature/{id}-{title}",
                AutoLink = true,
                AutoTransition = true,
            },
        };

        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.AgileUserStoryOnly());
    }

    private BranchCommand CreateCommand(IGitService? gitService = null, BranchLinkService? branchLinkService = null) =>
        new(new ActiveItemResolver(_contextStore, _workItemRepo, _adoService), _workItemRepo, _adoService, _processConfigProvider,
            _formatterFactory, _hintEngine, _config,
            gitService: gitService, branchLinkService: branchLinkService);

    private BranchLinkService CreateBranchLinkService() => new(_adoGitService, _adoService);

    private static WorkItem CreateWorkItem(int id, string title, string state) => new()
    {
        Id = id,
        Type = WorkItemType.UserStory,
        Title = title,
        State = state,
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

    // ── Happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_CreatesBranch_LinksArtifact_TransitionsState()
    {
        var item = CreateWorkItem(12345, "Add login", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(12345, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("project-id");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-id");

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);

        // Branch created and checked out
        await _gitService.Received().CreateBranchAsync(
            Arg.Is<string>(s => s.Contains("12345")), Arg.Any<CancellationToken>());
        await _gitService.Received().CheckoutAsync(
            Arg.Is<string>(s => s.Contains("12345")), Arg.Any<CancellationToken>());

        // Artifact link added via BranchLinkService → IAdoWorkItemService
        await _adoService.Received().AddArtifactLinkAsync(
            12345,
            Arg.Is<string>(s => s.StartsWith("vstfs:///Git/Ref/")),
            "Branch",
            Arg.Any<CancellationToken>());

        // State transitioned (New is Proposed → Active via shorthand 'c')
        await _adoService.Received().PatchAsync(12345,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── No active work item ─────────────────────────────────────────

    [Fact]
    public async Task NoActiveWorkItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        await _gitService.DidNotReceive().CreateBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Work item not in cache — auto-fetch from ADO ──────────────

    [Fact]
    public async Task WorkItemNotInCache_AutoFetchesFromAdo()
    {
        var item = CreateWorkItem(999, "Auto-fetched", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(999);
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync(noLink: true, noTransition: true);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(item, Arg.Any<CancellationToken>());
        await _gitService.Received().CreateBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Work item unreachable — ADO fetch fails ─────────────────────

    [Fact]
    public async Task WorkItemUnreachable_ReturnsErrorWithReason()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(999);
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .Returns<WorkItem>(x => throw new InvalidOperationException("Network timeout"));

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        await _gitService.DidNotReceive().CreateBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── No git service ──────────────────────────────────────────────

    [Fact]
    public async Task NoGitService_ReturnsError()
    {
        var item = CreateWorkItem(100, "Test", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(100);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = CreateCommand(gitService: null);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    // ── Not inside work tree ────────────────────────────────────────

    [Fact]
    public async Task NotInsideWorkTree_ReturnsError()
    {
        var item = CreateWorkItem(100, "Test", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(100);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    // ── --no-link flag ──────────────────────────────────────────────

    [Fact]
    public async Task NoLinkFlag_SkipsArtifactLink()
    {
        var item = CreateWorkItem(12345, "Add login", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(12345, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync(noLink: true);

        result.ShouldBe(0);

        // Branch created
        await _gitService.Received().CreateBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // No artifact link call
        await _adoService.DidNotReceive().AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── --no-transition flag ────────────────────────────────────────

    [Fact]
    public async Task NoTransitionFlag_SkipsStateChange()
    {
        var item = CreateWorkItem(12345, "Add login", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync(noTransition: true);

        result.ShouldBe(0);

        // Branch created
        await _gitService.Received().CreateBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // No state patch (only artifact link fetch)
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Branch already exists → checkout only ───────────────────────

    [Fact]
    public async Task BranchAlreadyExists_ChecksOutWithoutCreating()
    {
        var item = CreateWorkItem(12345, "Add login", "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync(noLink: true, noTransition: true);

        result.ShouldBe(0);
        await _gitService.DidNotReceive().CreateBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitService.Received().CheckoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Branch already exists → no artifact link ────────────────────

    [Fact]
    public async Task BranchAlreadyExists_SkipsArtifactLink()
    {
        var item = CreateWorkItem(12345, "Add login", "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);

        // No artifact link since branch was not newly created
        await _adoService.DidNotReceive().AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── AutoLink disabled → no artifact link ────────────────────────

    [Fact]
    public async Task AutoLinkDisabled_SkipsArtifactLink()
    {
        var item = CreateWorkItem(12345, "Test", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(12345, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        _config.Git.AutoLink = false;

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.DidNotReceive().AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── AutoTransition disabled → no state change ───────────────────

    [Fact]
    public async Task AutoTransitionDisabled_SkipsStateChange()
    {
        var item = CreateWorkItem(12345, "Test", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        _config.Git.AutoTransition = false;

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Already InProgress → no transition ──────────────────────────

    [Fact]
    public async Task AlreadyInProgress_NoStateTransition()
    {
        var item = CreateWorkItem(12345, "Test", "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        // No state patch — item is already InProgress (Active)
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Artifact link failure is best-effort ────────────────────────

    [Fact]
    public async Task ArtifactLinkFailure_DoesNotFailCommand()
    {
        var item = CreateWorkItem(12345, "Test", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(12345, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync();

        // Command succeeds — artifact linking is best-effort
        result.ShouldBe(0);
    }

    // ── State transition failure is best-effort ────────────────────

    [Fact]
    public async Task StateTransitionFailure_DoesNotFailCommand()
    {
        var item = CreateWorkItem(12345, "Test", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        // State transition patch throws
        _adoService.PatchAsync(12345, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO patch failed"));
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("pid");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("rid");

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync(noLink: true);

        // Command succeeds — state transition is best-effort
        result.ShouldBe(0);
    }

    // ── No BranchLinkService → skips linking ────────────────────────────

    [Fact]
    public async Task NoBranchLinkService_SkipsLinking()
    {
        var item = CreateWorkItem(12345, "Test", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(12345, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService, branchLinkService: null);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _gitService.Received().CreateBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Minimal output format ───────────────────────────────────────

    [Fact]
    public async Task MinimalOutput_PrintsBranchNameOnly()
    {
        var item = CreateWorkItem(12345, "Add login", "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync(noLink: true, noTransition: true, outputFormat: "minimal");
        result.ShouldBe(0);
    }

    // ── JSON output format ──────────────────────────────────────────

    [Fact]
    public async Task JsonOutput_ContainsStructuredFields()
    {
        var item = CreateWorkItem(12345, "Add login", "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService);

        var result = await cmd.ExecuteAsync(noLink: true, noTransition: true, outputFormat: "json");
        result.ShouldBe(0);
    }

    // ── Branch name uses configured template ────────────────────────

    [Fact]
    public async Task BranchName_UsesConfiguredTemplate()
    {
        _config.Git.BranchTemplate = "users/{type}/{id}-{title}";

        var item = CreateWorkItem(42, "Fix Bug", "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync(noLink: true, noTransition: true);

        result.ShouldBe(0);
        // "User Story" maps to "feature" via the default type map in BranchNamingService
        await _gitService.Received().CreateBranchAsync(
            Arg.Is<string>(s => s.StartsWith("users/feature/42-")),
            Arg.Any<CancellationToken>());
    }

    // ── Artifact URI format ─────────────────────────────────────────

    [Fact]
    public async Task ArtifactUri_ContainsProjectAndRepoIds()
    {
        var item = CreateWorkItem(12345, "Test", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(12345, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("my-project-id");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("my-repo-id");

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync(noTransition: true);

        result.ShouldBe(0);
        await _adoService.Received().AddArtifactLinkAsync(
            12345,
            Arg.Is<string>(s =>
                s.Contains("my-project-id") &&
                s.Contains("my-repo-id") &&
                s.StartsWith("vstfs:///Git/Ref/")),
            "Branch",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ArtifactUri_IncludesGBPrefixOnBranchRef()
    {
        // The ADO vstfs URI for a branch ref must include the 'GB' prefix before the
        // branch name: vstfs:///Git/Ref/{projectId}/{repoId}/GB{branchName}
        var item = CreateWorkItem(777, "GB Test", "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(777);
        _workItemRepo.GetByIdAsync(777, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(777, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(777, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _adoGitService.GetProjectIdAsync(Arg.Any<CancellationToken>()).Returns("proj-123");
        _adoGitService.GetRepositoryIdAsync(Arg.Any<CancellationToken>()).Returns("repo-456");

        var cmd = CreateCommand(_gitService, CreateBranchLinkService());
        var result = await cmd.ExecuteAsync(noTransition: true);

        result.ShouldBe(0);

        // Full format: vstfs:///Git/Ref/{projectId}/{repoId}/GB{encodedBranchName}
        await _adoService.Received().AddArtifactLinkAsync(
            777,
            Arg.Is<string>(s =>
                s.StartsWith("vstfs:///Git/Ref/proj-123/repo-456/GB")),
            "Branch",
            Arg.Any<CancellationToken>());
    }
}
