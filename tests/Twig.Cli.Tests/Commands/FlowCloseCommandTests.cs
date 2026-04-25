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
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class FlowCloseCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IConsoleInput _consoleInput;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly TwigConfiguration _config;
    private readonly IGitService _gitService;
    private readonly IAdoGitService _adoGitService;
    private readonly FlowTransitionService _flowTransitionService;
    private readonly IProcessConfigurationProvider _processConfigProvider;

    public FlowCloseCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _gitService = Substitute.For<IGitService>();
        _adoGitService = Substitute.For<IAdoGitService>();

        var activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Agile());
        _flowTransitionService = new FlowTransitionService(
            activeItemResolver, _adoService, _processConfigProvider, protectedCacheWriter);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _config = new TwigConfiguration
        {
            Git = new GitConfig { DefaultTarget = "main" },
        };

        // Default: non-TTY (IsOutputRedirected = true) to match typical test/CI behavior
        _consoleInput.IsOutputRedirected.Returns(true);

        // Default: no dirty items
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        // Default: no pending changes (so AutoPushNotesHelper is a no-op by default)
        _pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        // Default: no children (child verification gate passes)
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
    }

    private FlowCloseCommand CreateCommand(IGitService? gitService = null, IAdoGitService? adoGitService = null) =>
        new(_contextStore, _pendingChangeStore, _consoleInput, _formatterFactory, _config,
            _flowTransitionService, _workItemRepo, _adoService, _processConfigProvider,
            gitService, adoGitService);

    private static WorkItem CreateWorkItem(int id, string title, string state) => new()
    {
        Id = id,
        Type = WorkItemType.UserStory,
        Title = title,
        State = state,
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

    private static WorkItem CreateTaskItem(int id, string title, string state) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = title,
        State = state,
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

    [Fact]
    public async Task HappyPath_GuardsAndTransitionsAndDeletesBranchAndClearsContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetWorktreeRootAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-feature");
        _adoGitService.GetPullRequestsForBranchAsync("feature/1-feature", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequestInfo>());
        _consoleInput.ReadLine().Returns("y");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        // State transition
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State" && f.NewValue == "Closed")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Branch deleted
        await _gitService.Received().DeleteBranchAsync("feature/1-feature", Arg.Any<CancellationToken>());
        await _gitService.Received().CheckoutAsync("main", Arg.Any<CancellationToken>());
        // Context cleared
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnsavedChanges_RefusesWithExit1()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 1 });

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        await _contextStore.DidNotReceive().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenPr_NonTty_ReturnsExit2()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-feature");
        _adoGitService.GetPullRequestsForBranchAsync("feature/1-feature", Arg.Any<CancellationToken>())
            .Returns(new[] { new PullRequestInfo(99, "PR", "active", "feature/1-feature", "main", "https://pr") });

        // In test env, Console.IsOutputRedirected is true (non-TTY), so should exit 2
        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(2);
        await _contextStore.DidNotReceive().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Force_BypassesGuards()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 1 });
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(force: true);

        result.ShouldBe(0);
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoBranchCleanup_SkipsBranchDeletion()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync(noBranchCleanup: true);

        result.ShouldBe(0);
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyCompleted_SkipsTransition()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Closed");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(),
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoGitRepo_SkipsBranchCleanup()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand(gitService: null);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JsonOutputFormat_ProducesStructuredOutput()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Test", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(outputFormat: "json");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task NoActiveContext_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Force_BypassesPrGuard_WhenActivePrExists()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-feature");
        // No GetPullRequestsForBranchAsync setup — force: true skips the PR guard entirely.
        _consoleInput.ReadLine().Returns("y");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(force: true);

        result.ShouldBe(0);
        // Force bypasses the PR guard — should not even query PRs
        await _adoGitService.DidNotReceive().GetPullRequestsForBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenPr_Tty_UserDeclines_ReturnsZero()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-feature");
        _adoGitService.GetPullRequestsForBranchAsync("feature/1-feature", Arg.Any<CancellationToken>())
            .Returns(new[] { new PullRequestInfo(99, "PR", "active", "feature/1-feature", "main", "https://pr") });

        // Simulate TTY (interactive terminal)
        _consoleInput.IsOutputRedirected.Returns(false);
        _consoleInput.ReadLine().Returns("n");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        // User declined → cancelled, returns 0
        result.ShouldBe(0);
        await _contextStore.DidNotReceive().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompletedPr_DoesNotTriggerGuard()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-feature");
        // PR exists but status is "completed" (merged) — should NOT trigger the guard
        _adoGitService.GetPullRequestsForBranchAsync("feature/1-feature", Arg.Any<CancellationToken>())
            .Returns(new[] { new PullRequestInfo(99, "PR", "completed", "feature/1-feature", "main", "https://pr") });
        _consoleInput.ReadLine().Returns("y");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoPrs_NoGuardTriggered()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-feature");
        _adoGitService.GetPullRequestsForBranchAsync("feature/1-feature", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequestInfo>());
        _consoleInput.ReadLine().Returns("y");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    // ── Linked worktree (task #1621) ──────────────────────────────────

    [Fact]
    public async Task LinkedWorktree_SkipsBranchCleanup_EmitsWarning()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetWorktreeRootAsync(Arg.Any<CancellationToken>()).Returns("/tmp/my-worktree");
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-feature");
        _adoGitService.GetPullRequestsForBranchAsync("feature/1-feature", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequestInfo>());

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        // Branch cleanup skipped — no checkout or delete
        await _gitService.DidNotReceive().CheckoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().DeleteBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Context still cleared
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MainWorkTree_ProceedsWithBranchCleanup()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetWorktreeRootAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-feature");
        _adoGitService.GetPullRequestsForBranchAsync("feature/1-feature", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PullRequestInfo>());
        _consoleInput.ReadLine().Returns("y");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        // Main working tree — normal cleanup proceeds
        await _gitService.Received().CheckoutAsync("main", Arg.Any<CancellationToken>());
        await _gitService.Received().DeleteBranchAsync("feature/1-feature", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkedWorktree_NoBranchCleanupFlag_SkipsWorktreeCheck()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync(noBranchCleanup: true);

        result.ShouldBe(0);
        // noBranchCleanup skips entire section — no worktree check either
        await _gitService.DidNotReceive().GetWorktreeRootAsync(Arg.Any<CancellationToken>());
    }

    // ── Minimal output (ITEM-034) ──────────────────────────────────────

    [Fact]
    public async Task MinimalFormat_EmitsEmptyString()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(outputFormat: "minimal");

        result.ShouldBe(0);
    }

    // ── Child-state verification gate (task #1622) ────────────────────

    [Fact]
    public async Task ChildVerification_AllChildrenTerminal_Succeeds()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var children = new[]
        {
            CreateTaskItem(10, "Task A", "Closed"),
            CreateTaskItem(11, "Task B", "Closed"),
        };
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChildVerification_IncompleteChild_ReturnsExit1()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var children = new[]
        {
            CreateTaskItem(10, "Task A", "Closed"),
            CreateTaskItem(11, "Task B", "Active"),
        };
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        await _contextStore.DidNotReceive().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChildVerification_NoChildren_Succeeds()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        // Default: no children from cache, no children from ADO
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChildVerification_Force_BypassesGate()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        // Incomplete child — would block without --force
        var children = new[] { CreateTaskItem(10, "Task A", "Active") };
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(force: true);

        result.ShouldBe(0);
        // Child gate bypassed — no GetChildrenAsync called
        await _workItemRepo.DidNotReceive().GetChildrenAsync(1, Arg.Any<CancellationToken>());
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChildVerification_CacheMiss_FallsBackToAdo()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        // Cache returns empty — trigger ADO fallback
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        var adoChildren = new[] { CreateTaskItem(10, "Task A", "Closed") };
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(adoChildren);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received().FetchChildrenAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChildVerification_CacheMissAndAdoFailure_ReturnsExit1()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        // Cache returns empty, ADO throws
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        await _contextStore.DidNotReceive().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChildVerification_UnmappedType_TreatedAsNonTerminal()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        // Child with an unmapped type (not in ProcessConfiguration)
        var children = new[]
        {
            new WorkItem
            {
                Id = 10,
                Type = WorkItemType.Parse("CustomType").Value,
                Title = "Custom Item",
                State = "Done",
                IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
                AreaPath = AreaPath.Parse("Project").Value,
            },
        };
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1); // Unmapped type is treated as non-terminal
        await _contextStore.DidNotReceive().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChildVerification_ResolvedAndRemovedChildrenPass()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        // Agile Task states: Closed = Completed, Removed = Removed
        // User Story has Resolved; Task doesn't have Resolved state in Agile.
        // Use User Story children to test Resolved category.
        var children = new[]
        {
            CreateWorkItem(10, "Story A", "Resolved"),
            CreateWorkItem(11, "Story B", "Removed"),
            CreateTaskItem(12, "Task C", "Closed"),
        };
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    // ── ADO-authoritative child fetch (task #1717) ──────────────────────

    [Fact]
    public async Task ChildVerification_SpawnedItemInAdo_BlocksClosure()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        // Cache only knows about one completed child
        var cachedChildren = new[] { CreateTaskItem(10, "Task A", "Closed") };
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(cachedChildren);

        // ADO has an additional spawned issue that is still active
        var adoChildren = new[]
        {
            CreateTaskItem(10, "Task A", "Closed"),
            CreateTaskItem(11, "Spawned Issue", "Active"),
        };
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(adoChildren);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        await _contextStore.DidNotReceive().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChildVerification_AdoFailure_WithCachedChildren_ProceedsWithCache()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        // Cache has terminal children
        var cachedChildren = new[]
        {
            CreateTaskItem(10, "Task A", "Closed"),
            CreateTaskItem(11, "Task B", "Closed"),
        };
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(cachedChildren);

        // ADO fails — should fall back to cached children
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        // Falls back to cached children, all terminal → succeeds
        result.ShouldBe(0);
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChildVerification_AdoFailure_NoCachedChildren_ReturnsExit1()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        // Cache empty (default), ADO fails
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        await _contextStore.DidNotReceive().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChildVerification_AdoFewerChildrenThanCache_AdoAuthoritativeSetWins()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        // Cache has 2 children: one closed, one active (reparented away in ADO)
        var cachedChildren = new[]
        {
            CreateTaskItem(10, "Task A", "Closed"),
            CreateTaskItem(11, "Task B - reparented", "Active"),
        };
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(cachedChildren);

        // ADO only has the closed child (Task B was reparented away)
        var adoChildren = new[] { CreateTaskItem(10, "Task A", "Closed") };
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(adoChildren);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        // ADO's authoritative set wins — only Task A (closed) → succeeds
        result.ShouldBe(0);
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    // ── Pre-close-out note flush (task #1620) ─────────────────────────

    [Fact]
    public async Task NoteFlush_Success_PushesNotesAndContinuesToCompletion()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var note = new PendingChangeRecord(1, "note", null, null, "my pending note");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { note });

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(1, "my pending note", Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesByTypeAsync(1, "note", Arg.Any<CancellationToken>());
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoteFlush_NetworkFailure_WarnsAndContinuesToCompletion()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var note = new PendingChangeRecord(1, "note", null, null, "my pending note");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { note });
        _adoService.AddCommentAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        // Flush failure is non-blocking — close-out completes successfully
        result.ShouldBe(0);
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoteFlush_RunsEvenWithForceFlag()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var note = new PendingChangeRecord(1, "note", null, null, "pending note");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { note });

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(force: true);

        result.ShouldBe(0);
        // Flush ran even with --force
        await _adoService.Received().AddCommentAsync(1, "pending note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoteFlush_ItemWithOnlyPendingNotes_SucceedsAfterFlush()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        // Item appears clean (notes already flushed before guard runs)
        var note = new PendingChangeRecord(1, "note", null, null, "pending note");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { note });
        // After flush, dirty check returns empty (notes were cleared)
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(1, "pending note", Arg.Any<CancellationToken>());
        await _contextStore.Received().ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }
}
