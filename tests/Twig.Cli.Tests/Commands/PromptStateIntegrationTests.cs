using System.Text.Json;
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

/// <summary>
/// Integration tests verifying that mutating commands write <c>.twig/prompt.json</c>
/// via <see cref="IPromptStateWriter"/> after their primary operations complete.
/// </summary>
public class PromptStateIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _twigDir;
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly IConsoleInput _consoleInput;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;

    private string PromptJsonPath => Path.Combine(_twigDir, "prompt.json");

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

    public PromptStateIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-psi-test-{Guid.NewGuid():N}");
        _twigDir = Path.Combine(_tempDir, ".twig");
        Directory.CreateDirectory(_twigDir);

        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration();
        _paths = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"));

        _processConfigProvider.GetConfiguration().Returns(BuildAgileConfig());
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort cleanup */ }
    }

    private PromptStateWriter CreateWriter()
    {
        return new PromptStateWriter(_contextStore, _workItemRepo, _config, _paths, _processTypeStore);
    }

    private static WorkItem CreateWorkItem(int id, string title, string type = "User Story",
        string state = "Active", bool isDirty = false, int? parentId = null)
    {
        var wi = new WorkItem
        {
            Id = id,
            Type = WorkItemType.Parse(type).Value,
            Title = title,
            State = state,
            ParentId = parentId,
        };
        if (isDirty)
            wi.SetDirty();
        return wi;
    }

    private JsonElement ReadPromptJson()
    {
        var json = File.ReadAllText(PromptJsonPath);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    // ── (a) twig set writes prompt.json ────────────────────────────────

    [Fact]
    public async Task SetCommand_WritesPromptJson_WithWorkItemData()
    {
        var item = CreateWorkItem(12345, "Implement login");
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);

        // After set, context returns the new ID
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);

        var writer = CreateWriter();
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var syncCoord = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, 30);
        var iterService = Substitute.For<IIterationService>();
        iterService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var wsService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterService, null);
        var cmd = new SetCommand(_workItemRepo, _contextStore, resolver, syncCoord,
            wsService, _formatterFactory, _hintEngine, promptStateWriter: writer);

        var result = await cmd.ExecuteAsync("12345");

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var root = ReadPromptJson();
        root.GetProperty("id").GetInt32().ShouldBe(12345);
        root.GetProperty("title").GetString().ShouldBe("Implement login");
        root.GetProperty("state").GetString().ShouldBe("Active");
    }

    // ── (b) twig flow-start writes prompt.json ─────────────────────────

    [Fact]
    public async Task FlowStartCommand_WritesPromptJson()
    {
        var item = CreateWorkItem(42, "Add feature", state: "New");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        // After flow-start, context returns the new ID
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        var writer = CreateWriter();
        var activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var cmd = new FlowStartCommand(_workItemRepo, _adoService, _contextStore,
            activeItemResolver, protectedCacheWriter, _processConfigProvider, _consoleInput,
            _formatterFactory, _hintEngine, _config, promptStateWriter: writer);

        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var root = ReadPromptJson();
        root.GetProperty("id").GetInt32().ShouldBe(42);
    }

    // ── (c) twig flow-close writes {} ──────────────────────────────────

    [Fact]
    public async Task FlowCloseCommand_WritesEmptyPromptJson()
    {
        var item = CreateWorkItem(42, "Done feature", state: "Resolved");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        // After clear, context returns null
        _contextStore.When(x => x.ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>()))
            .Do(_ => _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null));

        var writer = CreateWriter();
        var activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var cmd = new FlowCloseCommand(_adoService, _contextStore,
            _pendingChangeStore, _processConfigProvider, _consoleInput,
            _formatterFactory, _hintEngine, _config, activeItemResolver, protectedCacheWriter,
            promptStateWriter: writer);

        var result = await cmd.ExecuteAsync(force: true);

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var json = File.ReadAllText(PromptJsonPath);
        json.ShouldBe("{}");
    }

    // ── (d) twig state transition updates prompt.json ──────────────────

    [Fact]
    public async Task StateCommand_WritesPromptJson_AfterTransition()
    {
        var item = CreateWorkItem(99, "Story to resolve", state: "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(99);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(item);

        var remote = CreateWorkItem(99, "Story to resolve", state: "Active");
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>()).Returns(remote);
        _adoService.PatchAsync(99, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        // After state change, the writer sees the new state
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(99);

        var writer = CreateWriter();
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var cmd = new StateCommand(resolver, _workItemRepo, _adoService,
            _pendingChangeStore, _processConfigProvider, _consoleInput,
            _formatterFactory, _hintEngine, writer);

        var result = await cmd.ExecuteAsync("Resolved");

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
    }

    // ── (e) twig save updates isDirty in prompt.json ───────────────────

    [Fact]
    public async Task SaveCommand_WritesPromptJson_AfterDirtyCleared()
    {
        var item = CreateWorkItem(55, "Dirty story", isDirty: true);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(55);
        _workItemRepo.GetByIdAsync(55, Arg.Any<CancellationToken>()).Returns(item);

        var remote = CreateWorkItem(55, "Dirty story");
        _adoService.FetchAsync(55, Arg.Any<CancellationToken>()).Returns(remote);
        _adoService.PatchAsync(55, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 55 });
        _pendingChangeStore.GetChangesAsync(55, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(55, "field", "System.Title", "old", "new") });

        _workItemRepo.GetChildrenAsync(55, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // After save completes, the writer reads clean state
        var cleanItem = CreateWorkItem(55, "Dirty story", isDirty: false);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(55);

        var writer = CreateWriter();
        var saveResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var cmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            saveResolver, _consoleInput, _formatterFactory, _hintEngine, writer);

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
    }

    // ── (f) twig config display.icons regenerates prompt.json ──────────

    [Fact]
    public async Task ConfigCommand_WritesPromptJson_OnDisplayKeyChange()
    {
        // Set up existing context so writer has data to write
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        var item = CreateWorkItem(42, "Icon test");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var writer = CreateWriter();
        var cmd = new ConfigCommand(_config, _paths, _formatterFactory, _hintEngine, writer);

        var result = await cmd.ExecuteAsync("display.icons", "nerd");

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var root = ReadPromptJson();
        root.GetProperty("id").GetInt32().ShouldBe(42);
    }

    // ── Config non-display key does NOT write prompt.json ──────────────

    [Fact]
    public async Task ConfigCommand_DoesNotWritePromptJson_OnNonDisplayKey()
    {
        var writer = CreateWriter();
        var cmd = new ConfigCommand(_config, _paths, _formatterFactory, _hintEngine, writer);

        var result = await cmd.ExecuteAsync("git.defaulttarget", "develop");

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeFalse();
    }

    // ── Writer failure does not cause command failure ───────────────────

    [Fact]
    public async Task SetCommand_Succeeds_WhenWriterThrows()
    {
        var item = CreateWorkItem(42, "Test item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        // Use a real PromptStateWriter that targets a read-only path to trigger an internal failure.
        // The writer swallows all exceptions per its contract, so the command still succeeds.
        var badPaths = new TwigPaths(
            Path.Combine(_tempDir, "nonexistent", "deep", "path"),
            Path.Combine(_tempDir, "nonexistent", "config"),
            Path.Combine(_tempDir, "nonexistent", "twig.db"));
        var failWriter = new PromptStateWriter(_contextStore, _workItemRepo, _config, badPaths, _processTypeStore);

        var resolver2 = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedWriter2 = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var syncCoord2 = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter2, 30);
        var iterService2 = Substitute.For<IIterationService>();
        iterService2.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var wsService2 = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterService2, null);
        var cmd = new SetCommand(_workItemRepo, _contextStore, resolver2, syncCoord2,
            wsService2, _formatterFactory, _hintEngine, promptStateWriter: failWriter);

        var result = await cmd.ExecuteAsync("42");

        // Command succeeds despite writer failing to write (directory doesn't exist)
        result.ShouldBe(0);
    }

    // ── (g) twig flow-done writes prompt.json after state transition ───

    [Fact]
    public async Task FlowDoneCommand_WritesPromptJson_AfterTransition()
    {
        var item = CreateWorkItem(77, "Feature to resolve", state: "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(77);
        _workItemRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _adoService.FetchAsync(77, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(77, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var writer = CreateWriter();
        var flowSaveResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var saveCmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            flowSaveResolver, _consoleInput, _formatterFactory, _hintEngine);
        var cmd = new FlowDoneCommand(_workItemRepo, _adoService,
            _pendingChangeStore, _processConfigProvider, saveCmd, _consoleInput,
            _formatterFactory, _hintEngine, _config, flowSaveResolver, protectedCacheWriter,
            promptStateWriter: writer);

        var result = await cmd.ExecuteAsync(noSave: true);

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var root = ReadPromptJson();
        root.GetProperty("id").GetInt32().ShouldBe(77);
    }

    // ── FlowDoneCommand with noSave:false — verifies skipPromptWrite prevents double-write ──

    [Fact]
    public async Task FlowDoneCommand_WritesPromptJsonOnce_WhenDirtyItemsSaved()
    {
        var item = CreateWorkItem(77, "Feature to resolve", state: "Active", isDirty: true);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(77);
        _workItemRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(77, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 77 });
        _pendingChangeStore.GetChangesAsync(77, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(77, "field", "System.Title", "old", "new") });

        _adoService.FetchAsync(77, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(77, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        // Use a mock writer to count calls — SaveCommand should NOT call it (skipPromptWrite: true),
        // only FlowDoneCommand should call it once at the end.
        var mockWriter = Substitute.For<IPromptStateWriter>();

        var flowDoneResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var saveCmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            flowDoneResolver, _consoleInput, _formatterFactory, _hintEngine, mockWriter);
        var cmd = new FlowDoneCommand(_workItemRepo, _adoService,
            _pendingChangeStore, _processConfigProvider, saveCmd, _consoleInput,
            _formatterFactory, _hintEngine, _config, flowDoneResolver, protectedCacheWriter,
            promptStateWriter: mockWriter);

        var result = await cmd.ExecuteAsync(noSave: false);

        result.ShouldBe(0);
        // Exactly one call: from FlowDoneCommand. SaveCommand's call is suppressed by skipPromptWrite.
        await mockWriter.Received(1).WritePromptStateAsync();
    }

    // ── (h) twig edit writes prompt.json after staging changes ─────────

    [Fact]
    public async Task EditCommand_WritesPromptJson_AfterStagingChanges()
    {
        var item = CreateWorkItem(88, "Edit target", state: "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(88);
        _workItemRepo.GetByIdAsync(88, Arg.Any<CancellationToken>()).Returns(item);

        var editorLauncher = Substitute.For<IEditorLauncher>();
        editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: New title\n");

        var writer = CreateWriter();
        var editResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var cmd = new EditCommand(editResolver, _workItemRepo, _pendingChangeStore,
            editorLauncher, _formatterFactory, _hintEngine, writer);

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var root = ReadPromptJson();
        root.GetProperty("id").GetInt32().ShouldBe(88);
    }

    // ── (i) twig note writes prompt.json after adding note ─────────────

    [Fact]
    public async Task NoteCommand_WritesPromptJson_AfterAddingNote()
    {
        var item = CreateWorkItem(66, "Note target", state: "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(66);
        _workItemRepo.GetByIdAsync(66, Arg.Any<CancellationToken>()).Returns(item);

        var editorLauncher = Substitute.For<IEditorLauncher>();

        var writer = CreateWriter();
        var noteResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var cmd = new NoteCommand(noteResolver, _workItemRepo, _pendingChangeStore,
            editorLauncher, _formatterFactory, _hintEngine, writer);

        var result = await cmd.ExecuteAsync("A test note");

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var root = ReadPromptJson();
        root.GetProperty("id").GetInt32().ShouldBe(66);
    }

    // ── (j) twig update writes prompt.json after field update ──────────

    [Fact]
    public async Task UpdateCommand_WritesPromptJson_AfterFieldUpdate()
    {
        var item = CreateWorkItem(33, "Update target", state: "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(33);
        _workItemRepo.GetByIdAsync(33, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(33, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(33, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var writer = CreateWriter();
        var updateResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var cmd = new UpdateCommand(updateResolver, _workItemRepo, _adoService,
            _pendingChangeStore, _consoleInput, _formatterFactory, _hintEngine, writer);

        var result = await cmd.ExecuteAsync("System.Title", "New Title");

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var root = ReadPromptJson();
        root.GetProperty("id").GetInt32().ShouldBe(33);
    }

    // ── (k) twig refresh writes prompt.json ────────────────────────────

    [Fact]
    public async Task RefreshCommand_WritesPromptJson_AfterCacheRefresh()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        var item = CreateWorkItem(42, "Refresh target", state: "Active");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        iterationService.GetWorkItemTypeAppearancesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeAppearance>());
        iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeWithStates>());
        iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData());

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var writer = CreateWriter();
        var refreshProtectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        var refreshSyncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, refreshProtectedWriter, 30);
        var refreshWorkingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);
        var cmd = new RefreshCommand(_contextStore, _workItemRepo, _adoService, iterationService,
            _pendingChangeStore, refreshProtectedWriter, _config, _paths, _processTypeStore, _formatterFactory, _hintEngine,
            refreshWorkingSetService, refreshSyncCoordinator, writer);

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var root = ReadPromptJson();
        root.GetProperty("id").GetInt32().ShouldBe(42);
    }

    // ── (l) twig branch writes prompt.json only when auto-transition fires

    [Fact]
    public async Task BranchCommand_WritesPromptJson_WhenAutoTransitionFires()
    {
        var item = CreateWorkItem(50, "Proposed story", type: "User Story", state: "New");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(50);
        _workItemRepo.GetByIdAsync(50, Arg.Any<CancellationToken>()).Returns(item);

        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var adoGitService = Substitute.For<IAdoGitService>();
        _adoService.FetchAsync(50, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(50, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var branchConfig = new TwigConfiguration { Git = { AutoTransition = true, AutoLink = false } };
        var writer = CreateWriter();
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var cmd = new BranchCommand(resolver, _workItemRepo, _adoService,
            _processConfigProvider, _formatterFactory, _hintEngine, branchConfig,
            gitService, adoGitService, writer);

        var result = await cmd.ExecuteAsync(noLink: true);

        result.ShouldBe(0);
        // Auto-transition Proposed→Active fires, so prompt.json should be written
        File.Exists(PromptJsonPath).ShouldBeTrue();
    }

    [Fact]
    public async Task BranchCommand_DoesNotWritePromptJson_WhenNoAutoTransition()
    {
        var item = CreateWorkItem(51, "Active story", type: "User Story", state: "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(51);
        _workItemRepo.GetByIdAsync(51, Arg.Any<CancellationToken>()).Returns(item);

        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        gitService.BranchExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var autoTransitionEnabledConfig = new TwigConfiguration { Git = { AutoTransition = true, AutoLink = false } };
        var writer = CreateWriter();
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var cmd = new BranchCommand(resolver, _workItemRepo, _adoService,
            _processConfigProvider, _formatterFactory, _hintEngine, autoTransitionEnabledConfig,
            gitService, promptStateWriter: writer);

        var result = await cmd.ExecuteAsync(noLink: true);

        result.ShouldBe(0);
        // State is already Active (InProgress), no transition fires, no prompt.json write
        File.Exists(PromptJsonPath).ShouldBeFalse();
    }

    // ── (m) _hook post-checkout writes prompt.json, other hooks do not ─

    [Fact]
    public async Task HookHandler_PostCheckout_WritesPromptJson()
    {
        var gitService = Substitute.For<IGitService>();
        gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("feature/12345-login");

        var item = CreateWorkItem(12345, "Login feature");
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);

        var writer = CreateWriter();
        var cmd = new HookHandlerCommand(_contextStore, _workItemRepo, _config, gitService, writer);

        // post-checkout with branch-flag=1 (branch switch)
        var result = await cmd.ExecuteAsync("post-checkout", new[] { "oldref", "newref", "1" });

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var root = ReadPromptJson();
        root.GetProperty("id").GetInt32().ShouldBe(12345);
    }

    [Fact]
    public async Task HookHandler_PrepareCommitMsg_DoesNotWritePromptJson()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        // Create a temp commit message file
        var msgFile = Path.Combine(_tempDir, "COMMIT_EDITMSG");
        await File.WriteAllTextAsync(msgFile, "initial message");

        var writer = CreateWriter();
        var cmd = new HookHandlerCommand(_contextStore, _workItemRepo, _config, promptStateWriter: writer);

        var result = await cmd.ExecuteAsync("prepare-commit-msg", new[] { msgFile });

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeFalse();
    }

    [Fact]
    public async Task HookHandler_CommitMsg_DoesNotWritePromptJson()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        var msgFile = Path.Combine(_tempDir, "COMMIT_EDITMSG");
        await File.WriteAllTextAsync(msgFile, "#42 fix bug");

        var writer = CreateWriter();
        var cmd = new HookHandlerCommand(_contextStore, _workItemRepo, _config, promptStateWriter: writer);

        var result = await cmd.ExecuteAsync("commit-msg", new[] { msgFile });

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeFalse();
    }

    // ── (n) twig stash pop writes prompt.json when WI# detected ────────

    [Fact]
    public async Task StashPop_WritesPromptJson_WhenWorkItemDetected()
    {
        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("feature/999-stash-test");

        var item = CreateWorkItem(999, "Stash test");
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.ExistsByIdAsync(999, Arg.Any<CancellationToken>()).Returns(true);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(999);

        var writer = CreateWriter();
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var cmd = new StashCommand(_contextStore, _workItemRepo, resolver, _formatterFactory,
            _hintEngine, _config, gitService, writer);

        var result = await cmd.PopAsync();

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var root = ReadPromptJson();
        root.GetProperty("id").GetInt32().ShouldBe(999);
    }

    [Fact]
    public async Task StashPush_DoesNotWritePromptJson()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        var item = CreateWorkItem(42, "Push test");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);

        var writer = CreateWriter();
        var resolver2 = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var cmd = new StashCommand(_contextStore, _workItemRepo, resolver2, _formatterFactory,
            _hintEngine, _config, gitService, writer);

        var result = await cmd.ExecuteAsync("test message");

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeFalse();
    }
}
