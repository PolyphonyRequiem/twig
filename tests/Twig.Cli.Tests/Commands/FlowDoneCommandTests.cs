using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class FlowDoneCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly SaveCommand _saveCommand;
    private readonly IConsoleInput _consoleInput;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly IGitService _gitService;
    private readonly IAdoGitService _adoGitService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly ProtectedCacheWriter _protectedCacheWriter;

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

    private static StateEntry[] S(params (string Name, StateCategory Cat)[] entries) =>
        entries.Select(e => new StateEntry(e.Name, e.Cat, null)).ToArray();

    private static ProcessConfiguration BuildAgileConfig() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("User Story", AgileUserStoryStates, new[] { "Task" }),
            MakeRecord("Task", S(("New", StateCategory.Proposed), ("Active", StateCategory.InProgress), ("Closed", StateCategory.Completed), ("Removed", StateCategory.Removed)), Array.Empty<string>()),
        });

    public FlowDoneCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _gitService = Substitute.For<IGitService>();
        _adoGitService = Substitute.For<IAdoGitService>();

        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration
        {
            User = new UserConfig { DisplayName = "Test User" },
            Git = new GitConfig { DefaultTarget = "main" },
            Flow = new FlowConfig { OfferPrOnDone = true },
        };

        _processConfigProvider.GetConfiguration().Returns(BuildAgileConfig());

        // Default: no pending changes, no dirty items
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        _saveCommand = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            _activeItemResolver, _consoleInput, _formatterFactory, _hintEngine);
    }

    private FlowDoneCommand CreateCommand(IGitService? gitService = null, IAdoGitService? adoGitService = null) =>
        new(_workItemRepo, _adoService, _pendingChangeStore,
            _processConfigProvider, _saveCommand, _consoleInput, _formatterFactory, _config,
            _activeItemResolver, _protectedCacheWriter,
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
    public async Task HappyPath_SavesWorkTree_TransitionsState_OffersPr()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Add login", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-add-login");
        _gitService.IsAheadOfAsync("main", Arg.Any<CancellationToken>()).Returns(true);
        _consoleInput.ReadLine().Returns("y");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(new PullRequestInfo(42, "PR", "active", "feature/1-add-login", "main", "https://pr"));

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        // State transition
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State" && f.NewValue == "Resolved")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        // PR creation
        await _adoGitService.Received().CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoSave_SkipsSaveStep()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Test", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(noSave: true);

        result.ShouldBe(0);
        // SaveCommand should not have been invoked — GetChangesAsync is SaveCommand-specific
        await _pendingChangeStore.DidNotReceive().GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoPr_SkipsPrOffer()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Test", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(noPr: true);

        result.ShouldBe(0);
        await _gitService.DidNotReceive().IsInsideWorkTreeAsync(Arg.Any<CancellationToken>());
        await _adoGitService.DidNotReceive().CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExplicitId_SavesSingleItem()
    {
        // Do NOT set active context — explicit id should work independently
        var item = CreateWorkItem(42, "Specific item", "Active");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(id: 42);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task ExplicitId_DoesNotChangeContext()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(99);
        var item = CreateWorkItem(42, "Specific item", "Active");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(id: 42);

        result.ShouldBe(0);
        // Context should NOT have been changed
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
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
    public async Task TypeWithNoResolvedCategory_FallsBackToCompleted()
    {
        // Task type has no Resolved category — should fall back to Completed ('d' → "Closed")
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Task,
            Title = "A task",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(noSave: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State" && f.NewValue == "Closed")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyResolved_SkipsTransition()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Test", "Resolved");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(noSave: true);

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(),
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JsonOutputFormat_ProducesStructuredOutput()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Test", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync(noSave: true, outputFormat: "json");

            result.ShouldBe(0);
            var output = stdout.ToString();
            output.ShouldContain("\"command\": \"flow done\"");
            output.ShouldContain("\"itemId\": 1");
            output.ShouldContain("\"actions\": {");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    [Fact]
    public async Task WorkTreeSaved_WhenDirtyItemsExist_IncludesActionInOutput()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Add login", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        // Active item IS dirty
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 1 });
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync(outputFormat: "json");

            result.ShouldBe(0);
            var output = stdout.ToString();
            output.ShouldContain("\"saved\": true");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    [Fact]
    public async Task WorkTreeSaved_WhenNoDirtyItems_ExcludesActionFromOutput()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Add login", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        // No dirty items at all
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync(outputFormat: "json");

            result.ShouldBe(0);
            var output = stdout.ToString();
            output.ShouldNotContain("\"saved\": true");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    [Fact]
    public async Task ExplicitId_WorkTreeSaved_ExcludedWhenTargetIsCleanButOthersAreDirty()
    {
        // Item 42 is clean, but item 999 is dirty — "Work tree saved" should NOT appear for item 42
        var item = CreateWorkItem(42, "Specific item", "Active");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 999 });
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync(id: 42, outputFormat: "json");

            result.ShouldBe(0);
            var output = stdout.ToString();
            output.ShouldNotContain("\"saved\": true");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    [Fact]
    public async Task NonExplicitId_WorkTreeSaved_IncludedWhenChildIsDirtyButActiveItemIsClean()
    {
        // Active item 1 is clean, but child item 2 is dirty — "Work tree saved" SHOULD appear
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Active");
        var child = CreateWorkItem(2, "Child task", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(child);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { child });
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        // Only child item 2 is dirty, not active item 1
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 2 });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync(outputFormat: "json");

            result.ShouldBe(0);
            var output = stdout.ToString();
            output.ShouldContain("\"saved\": true");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    [Fact]
    public async Task NonExplicitId_WorkTreeSaved_ExcludedWhenOnlyUnrelatedItemIsDirty()
    {
        // Active item 1 is clean, child items are clean, but unrelated item 999 is dirty
        // "Work tree saved" should NOT appear because the active work tree has nothing dirty
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Feature", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        // Unrelated item 999 is dirty, but active item 1 is clean
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 999 });

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync(outputFormat: "json");

            result.ShouldBe(0);
            var output = stdout.ToString();
            output.ShouldNotContain("\"saved\": true");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    [Fact]
    public async Task PrDescription_ContainsABHashLinkingFormat()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        var item = CreateWorkItem(12345, "Login timeout", "Active");
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(12345, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-login-timeout");
        _gitService.IsAheadOfAsync("main", Arg.Any<CancellationToken>()).Returns(true);
        _consoleInput.ReadLine().Returns("y");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(new PullRequestInfo(891, "PR", "active", "feature/12345-login-timeout", "main", "https://pr"));

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(noSave: true);

        result.ShouldBe(0);
        await _adoGitService.Received().CreatePullRequestAsync(
            Arg.Is<PullRequestCreate>(r => r.Description.Contains("AB#12345")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BranchNotAhead_NoPrOffered()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Test", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-test");
        _gitService.IsAheadOfAsync("main", Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(noSave: true);

        result.ShouldBe(0);
        await _adoGitService.DidNotReceive().CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UserDeclinesPr_NoPrCreated()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Test", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-test");
        _gitService.IsAheadOfAsync("main", Arg.Any<CancellationToken>()).Returns(true);
        _consoleInput.ReadLine().Returns("n");

        var cmd = CreateCommand(_gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(noSave: true);

        result.ShouldBe(0);
        await _adoGitService.DidNotReceive().CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OfferPrOnDone_Disabled_NoPrOffered()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Test", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var configNoPr = new TwigConfiguration
        {
            User = new UserConfig { DisplayName = "Test User" },
            Git = new GitConfig { DefaultTarget = "main" },
            Flow = new FlowConfig { OfferPrOnDone = false },
        };

        var cmd = new FlowDoneCommand(
            _workItemRepo, _adoService, _pendingChangeStore,
            _processConfigProvider, _saveCommand, _consoleInput, _formatterFactory, configNoPr,
            _activeItemResolver, _protectedCacheWriter,
            _gitService, _adoGitService);
        var result = await cmd.ExecuteAsync(noSave: true);

        result.ShouldBe(0);
        await _gitService.DidNotReceive().IsInsideWorkTreeAsync(Arg.Any<CancellationToken>());
        await _adoGitService.DidNotReceive().CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>());
    }

    // ── Minimal output (ITEM-034) ──────────────────────────────────────

    [Fact]
    public async Task MinimalFormat_EmitsPrUrlWhenPrCreated()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Test", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-test");
        _gitService.IsAheadOfAsync("main", Arg.Any<CancellationToken>()).Returns(true);
        _consoleInput.ReadLine().Returns("y");
        _adoGitService.CreatePullRequestAsync(Arg.Any<PullRequestCreate>(), Arg.Any<CancellationToken>())
            .Returns(new PullRequestInfo(42, "PR", "active", "feature/1-test", "main", "https://dev.azure.com/pr/42"));

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand(_gitService, _adoGitService);
            var result = await cmd.ExecuteAsync(noSave: true, outputFormat: "minimal");

            result.ShouldBe(0);
            var output = stdout.ToString().Trim();
            output.ShouldContain("https://dev.azure.com/pr/42");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    [Fact]
    public async Task MinimalFormat_EmitsEmptyWhenNoPr()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Test", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync(noSave: true, noPr: true, outputFormat: "minimal");

            result.ShouldBe(0);
            var output = stdout.ToString().Trim();
            output.ShouldBeEmpty();
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    // ── Null IAdoGitService (unresolved git project/repository) ───────

    [Fact]
    public async Task NullAdoGitService_SkipsPrOperationsGracefully()
    {
        // Simulates the case where git project/repository cannot be resolved at startup,
        // so IAdoGitService is not registered and resolves to null.
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        var item = CreateWorkItem(1, "Test", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/1-test");
        _gitService.IsAheadOfAsync("main", Arg.Any<CancellationToken>()).Returns(true);

        // No adoGitService injected → null path
        var cmd = CreateCommand(gitService: _gitService, adoGitService: null);
        var result = await cmd.ExecuteAsync(noSave: true);

        // Should succeed and not attempt PR creation
        result.ShouldBe(0);
    }
}
