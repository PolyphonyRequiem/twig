using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class FlowStartCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly ProtectedCacheWriter _protectedCacheWriter;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IConsoleInput _consoleInput;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly IGitService _gitService;
    private readonly IIterationService _iterationService;

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

    public FlowStartCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _gitService = Substitute.For<IGitService>();
        _iterationService = Substitute.For<IIterationService>();

        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);

        // Default: no dirty items (for ProtectedCacheWriter)
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration
        {
            User = new UserConfig { DisplayName = "Test User" },
            Git = new GitConfig { BranchTemplate = "feature/{id}-{title}", DefaultTarget = "main" },
        };

        _processConfigProvider.GetConfiguration().Returns(BuildAgileConfig());
    }

    private FlowStartCommand CreateCommand(IGitService? gitService = null, IIterationService? iterationService = null) =>
        new(_workItemRepo, _adoService, _contextStore, _activeItemResolver, _protectedCacheWriter,
            _processConfigProvider, _consoleInput, _formatterFactory, _hintEngine, _config,
            pipelineFactory: null, gitService: gitService, iterationService: iterationService);

    private FlowStartCommand CreateCommandWithRenderer(
        IAsyncRenderer renderer, IGitService? gitService = null, IIterationService? iterationService = null)
    {
        var pipelineFactory = new RenderingPipelineFactory(
            _formatterFactory, renderer, isOutputRedirected: () => false);
        return new(_workItemRepo, _adoService, _contextStore, _activeItemResolver, _protectedCacheWriter,
            _processConfigProvider, _consoleInput, _formatterFactory, _hintEngine, _config,
            pipelineFactory: pipelineFactory, gitService: gitService, iterationService: iterationService);
    }

    private static WorkItem CreateWorkItem(int id, string title, string state, string? assignedTo = null) => new()
    {
        Id = id,
        Type = WorkItemType.UserStory,
        Title = title,
        State = state,
        AssignedTo = assignedTo,
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

    [Fact]
    public async Task HappyPath_SetsContext_TransitionsState_Assigns_CreatesBranch()
    {
        var item = CreateWorkItem(12345, "Add login", "New");
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(12345, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.HasUncommittedChangesAsync(Arg.Any<CancellationToken>()).Returns(false);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync("12345");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(12345, Arg.Any<CancellationToken>());
        // State transition (New → Active)
        await _adoService.Received().PatchAsync(12345,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Assignment
        await _adoService.Received().PatchAsync(12345,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.AssignedTo")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Re-fetch after assignment to refresh cache (Bug 2: once for conflict check, once for post-assignment refresh)
        await _adoService.Received(2).FetchAsync(12345, Arg.Any<CancellationToken>());
        // Branch creation
        await _gitService.Received().CreateBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _gitService.Received().CheckoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoBranch_SkipsGitOperations()
    {
        var item = CreateWorkItem(1, "Test", "New");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync("1", noBranch: true);

        result.ShouldBe(0);
        await _gitService.DidNotReceive().IsInsideWorkTreeAsync(Arg.Any<CancellationToken>());
        await _gitService.DidNotReceive().CreateBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoState_SkipsStateTransition()
    {
        var item = CreateWorkItem(1, "Test", "New");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        // Only assignment patch expected, not state patch
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("1", noState: true);

        result.ShouldBe(0);
        // Should have received exactly one patch (assignment), not state transition
        await _adoService.Received(1).PatchAsync(1,
            Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoAssign_SkipsAssignment()
    {
        var item = CreateWorkItem(1, "Test", "New");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("1", noAssign: true);

        result.ShouldBe(0);
        // Only state transition patch, no assignment
        await _adoService.Received(1).PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyInProgress_SkipsStateTransition()
    {
        var item = CreateWorkItem(1, "Test", "Active");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("1");

        result.ShouldBe(0);
        // Only assignment patch, no state transition
        await _adoService.DidNotReceive().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AlreadyAssigned_SkipsAssignment()
    {
        var item = CreateWorkItem(1, "Test", "New", assignedTo: "Existing User");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("1");

        result.ShouldBe(0);
        // State patch but no assignment patch
        await _adoService.DidNotReceive().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.AssignedTo")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Take_AssignsEvenIfAlreadyAssigned()
    {
        var item = CreateWorkItem(1, "Test", "Active", assignedTo: "Other User");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("1", take: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.AssignedTo")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Re-fetch after assignment to refresh cache (Bug 2)
        await _adoService.Received(2).FetchAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Force_ProceedsWithUncommittedChanges()
    {
        var item = CreateWorkItem(1, "Test", "New");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.HasUncommittedChangesAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync("1", force: true);

        result.ShouldBe(0);
        // Should still create branch despite uncommitted changes
        await _gitService.Received().CreateBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoGitRepo_SkipsBranch()
    {
        var item = CreateWorkItem(1, "Test", "New");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        // No git service injected
        var cmd = CreateCommand(gitService: null);
        var result = await cmd.ExecuteAsync("1");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatternDisambiguation_MultipleMatches_ReturnsError()
    {
        var items = new[]
        {
            CreateWorkItem(1, "Login feature", "New"),
            CreateWorkItem(2, "Login bug", "New"),
        };
        _workItemRepo.FindByPatternAsync("Login", Arg.Any<CancellationToken>()).Returns(items);

        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync("Login");

            result.ShouldBe(1);
        }
        finally
        {
            Console.SetError(savedErr);
        }
    }

    [Fact]
    public async Task ContextSwitchHint_NoError()
    {
        // Already have an active item, switching to new one
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(999);
        var item = CreateWorkItem(1, "New item", "New");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("1");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JsonOutputFormat_ProducesStructuredOutput()
    {
        var item = CreateWorkItem(1, "Test", "New");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync("1", outputFormat: "json");

            result.ShouldBe(0);
            var output = stdout.ToString();
            output.ShouldContain("\"command\": \"flow start\"");
            output.ShouldContain("\"itemId\": 1");
            output.ShouldContain("\"actions\": {");
            output.ShouldContain("\"contextSet\": true");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    [Fact]
    public async Task UncommittedChanges_NoForce_ReturnsError()
    {
        var item = CreateWorkItem(1, "Test", "New");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.HasUncommittedChangesAsync(Arg.Any<CancellationToken>()).Returns(true);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync("1");

        result.ShouldBe(1);
        await _gitService.DidNotReceive().CreateBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Guard must fire BEFORE any context/ADO mutations
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.AssignedTo")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchedFromAdo_CacheMiss_AutoFetchesAndProceeds()
    {
        // Item not in cache — ActiveItemResolver auto-fetches from ADO
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(1, "Auto-fetched item", "New");
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync("1");

            result.ShouldBe(0);
            await _contextStore.Received().SetActiveWorkItemIdAsync(1, Arg.Any<CancellationToken>());
            // Verify the info message about fetching from ADO was emitted
            stdout.ToString().ShouldContain("Fetching work item 1 from ADO");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    // ── Interactive Picker (ITEM-029/030) ──────────────────────────────

    [Fact]
    public async Task NoArg_WithIterationService_ProposedItems_NonTty_PrintsListAndErrors()
    {
        var iterPath = IterationPath.Parse("Project\\Sprint 1").Value;
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>()).Returns(iterPath);
        var items = new[]
        {
            CreateWorkItem(101, "Unstarted item A", "New"),
            CreateWorkItem(102, "Unstarted item B", "New"),
        };
        _workItemRepo.GetByIterationAndAssigneeAsync(iterPath, "Test User", Arg.Any<CancellationToken>()).Returns(items);

        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            // No pipeline factory → non-TTY path
            var cmd = CreateCommand(iterationService: _iterationService);
            var result = await cmd.ExecuteAsync(null);

            result.ShouldBe(1);
            var errOutput = stderr.ToString();
            errOutput.ShouldContain("Interactive picker not available");
            errOutput.ShouldContain("#101");
            errOutput.ShouldContain("#102");
        }
        finally
        {
            Console.SetError(savedErr);
        }
    }

    [Fact]
    public async Task NoArg_NoProposedItems_PrintsNoUnstartedItems()
    {
        var iterPath = IterationPath.Parse("Project\\Sprint 1").Value;
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>()).Returns(iterPath);
        // All items are Active (InProgress), none Proposed
        var items = new[]
        {
            CreateWorkItem(101, "Active item", "Active"),
        };
        _workItemRepo.GetByIterationAndAssigneeAsync(iterPath, "Test User", Arg.Any<CancellationToken>()).Returns(items);

        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var cmd = CreateCommand(iterationService: _iterationService);
            var result = await cmd.ExecuteAsync(null);

            result.ShouldBe(1);
            stderr.ToString().ShouldContain("No unstarted items");
        }
        finally
        {
            Console.SetError(savedErr);
        }
    }

    [Fact]
    public async Task NoArg_NoIterationService_ReturnsUsageError()
    {
        var savedErr = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            // No iteration service injected
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync(null);

            result.ShouldBe(2);
        }
        finally
        {
            Console.SetError(savedErr);
        }
    }

    [Fact]
    public async Task NoArg_Tty_ProposedItems_ShowsPickerAndProceedsWithFullFlow()
    {
        var iterPath = IterationPath.Parse("Project\\Sprint 1").Value;
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>()).Returns(iterPath);
        var proposed = new[]
        {
            CreateWorkItem(101, "Unstarted item A", "New"),
            CreateWorkItem(102, "Unstarted item B", "New"),
        };
        _workItemRepo.GetByIterationAndAssigneeAsync(iterPath, "Test User", Arg.Any<CancellationToken>()).Returns(proposed);

        // Mock renderer returns a selected item
        var renderer = Substitute.For<IAsyncRenderer>();
        renderer.PromptDisambiguationAsync(
            Arg.Any<IReadOnlyList<(int Id, string Title)>>(), Arg.Any<CancellationToken>())
            .Returns((101, "Unstarted item A"));

        // Set up the full flow for item 101
        _workItemRepo.GetByIdAsync(101, Arg.Any<CancellationToken>()).Returns(proposed[0]);
        _adoService.FetchAsync(101, Arg.Any<CancellationToken>()).Returns(proposed[0]);
        _adoService.PatchAsync(101, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);

        var cmd = CreateCommandWithRenderer(renderer, iterationService: _iterationService);
        var result = await cmd.ExecuteAsync(null);

        result.ShouldBe(0);
        // Verify the full start flow proceeded
        await _contextStore.Received().SetActiveWorkItemIdAsync(101, Arg.Any<CancellationToken>());
        // State transition occurred (New → Active)
        await _adoService.Received().PatchAsync(101,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.State")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Minimal output (ITEM-034) ──────────────────────────────────────

    [Fact]
    public async Task MinimalFormat_EmitsBranchNameOnly()
    {
        var item = CreateWorkItem(1, "Test", "New");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.HasUncommittedChangesAsync(Arg.Any<CancellationToken>()).Returns(false);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand(_gitService);
            var result = await cmd.ExecuteAsync("1", outputFormat: "minimal");

            result.ShouldBe(0);
            var output = stdout.ToString().Trim();
            output.ShouldStartWith("feature/1-test");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }

    // ── JSON contract (ITEM-033) ───────────────────────────────────────

    [Fact]
    public async Task JsonFormat_ContainsStructuredActionsObject()
    {
        var item = CreateWorkItem(1, "Test", "New");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.HasUncommittedChangesAsync(Arg.Any<CancellationToken>()).Returns(false);
        _gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var savedOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var cmd = CreateCommand(_gitService);
            var result = await cmd.ExecuteAsync("1", outputFormat: "json");

            result.ShouldBe(0);
            var output = stdout.ToString();
            // Verify structured actions object
            output.ShouldContain("\"command\": \"flow start\"");
            output.ShouldContain("\"itemId\": 1");
            output.ShouldContain("\"type\": \"User Story\"");
            output.ShouldContain("\"contextSet\": true");
            output.ShouldContain("\"from\": \"New\"");
            output.ShouldContain("\"to\": \"Active\"");
            output.ShouldContain("\"to\": \"Test User\"");
            output.ShouldContain("\"name\":");
            output.ShouldContain("\"created\": true");
            output.ShouldContain("\"exitCode\": 0");
        }
        finally
        {
            Console.SetOut(savedOut);
        }
    }
}
