using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class FlowCloseCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IConsoleInput _consoleInput;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly IGitService _gitService;
    private readonly IAdoGitService _adoGitService;

    private static StateEntry[] AgileUserStoryStates =>
    [
        new("New", StateCategory.Proposed, null),
        new("Active", StateCategory.InProgress, null),
        new("Resolved", StateCategory.Resolved, null),
        new("Closed", StateCategory.Completed, null),
        new("Removed", StateCategory.Removed, null),
    ];

    private static ProcessTypeRecord MakeRecord(string typeName, StateEntry[] states, string[] childTypes) =>
        new() { TypeName = typeName, States = states, ValidChildTypes = childTypes };

    private static ProcessConfiguration BuildAgileConfig() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("User Story", AgileUserStoryStates, new[] { "Task" }),
        });

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

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration
        {
            Git = new GitConfig { DefaultTarget = "main" },
        };

        _processConfigProvider.GetConfiguration().Returns(BuildAgileConfig());

        // Default: non-TTY (IsOutputRedirected = true) to match typical test/CI behavior
        _consoleInput.IsOutputRedirected.Returns(true);

        // Default: no dirty items
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
    }

    private FlowCloseCommand CreateCommand(IGitService? gitService = null, IAdoGitService? adoGitService = null) =>
        new(_workItemRepo, _adoService, _contextStore, _pendingChangeStore,
            _processConfigProvider, _consoleInput, _formatterFactory, _hintEngine, _config,
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

    [Fact]
    public async Task HappyPath_GuardsAndTransitionsAndDeletesBranchAndClearsContext()
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

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync(outputFormat: "json");

            result.ShouldBe(0);
            var output = stdout.ToString();
            output.ShouldContain("\"command\":\"flow close\"");
            output.ShouldContain("\"itemId\":1");
            output.ShouldContain("\"actions\":{");
            output.ShouldContain("\"contextCleared\":true");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    [Fact]
    public async Task NoActiveContext_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync();

            result.ShouldBe(1);
            stderr.ToString().ShouldContain("No active work item");
        }
        finally
        {
            Console.SetError(savedErr);
        }
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

    // ── Minimal output (ITEM-034) ──────────────────────────────────────

    [Fact]
    public async Task MinimalFormat_EmitsEmptyString()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync(outputFormat: "minimal");

            result.ShouldBe(0);
            var output = stdout.ToString().Trim();
            output.ShouldBeEmpty();
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }
}
