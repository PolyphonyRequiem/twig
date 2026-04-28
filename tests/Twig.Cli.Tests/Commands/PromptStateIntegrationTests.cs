using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
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
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly IConsoleInput _consoleInput;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;

    private string PromptJsonPath => Path.Combine(_twigDir, "prompt.json");

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
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration();
        _paths = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"));

        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.AgileUserStoryOnly());
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
        var syncCoordFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedWriter, _pendingChangeStore, null, 30, 30);
        var iterService = Substitute.For<IIterationService>();
        iterService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var wsService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterService, null);
        var pipelineFactory = new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true);
        var ctx = new CommandContext(pipelineFactory, _formatterFactory, _hintEngine, _config);
        var statusFieldReader = new StatusFieldConfigReader(_paths);
        var cmd = new SetCommand(ctx, _workItemRepo, _contextStore, resolver, syncCoordFactory,
            wsService, statusFieldReader, promptStateWriter: writer);

        var result = await cmd.ExecuteAsync("12345");

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var root = ReadPromptJson();
        root.GetProperty("id").GetInt32().ShouldBe(12345);
        root.GetProperty("title").GetString().ShouldBe("Implement login");
        root.GetProperty("state").GetString().ShouldBe("Active");
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
        var cmd = new StateCommand(
            new CommandContext(new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true), _formatterFactory, _hintEngine, _config),
            resolver, _workItemRepo, _adoService,
            _pendingChangeStore, _processConfigProvider, _consoleInput,
            promptStateWriter: writer);

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
        var flusher = new PendingChangeFlusher(_workItemRepo, _adoService, _pendingChangeStore, _consoleInput, _formatterFactory);
        var cmd = new SaveCommand(_workItemRepo, _pendingChangeStore, flusher,
            saveResolver, _formatterFactory, writer);

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
        var cmd = new ConfigCommand(_config, _paths, _formatterFactory, writer);

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
        var cmd = new ConfigCommand(_config, _paths, _formatterFactory, writer);

        var result = await cmd.ExecuteAsync("git.branchpattern", @"^feature/(?<id>\d+)");

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
        var syncCoordFactory2 = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedWriter2, _pendingChangeStore, null, 30, 30);
        var iterService2 = Substitute.For<IIterationService>();
        iterService2.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var wsService2 = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterService2, null);
        var pipelineFactory2 = new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true);
        var ctx2 = new CommandContext(pipelineFactory2, _formatterFactory, _hintEngine, _config);
        var statusFieldReader2 = new StatusFieldConfigReader(badPaths);
        var cmd = new SetCommand(ctx2, _workItemRepo, _contextStore, resolver2, syncCoordFactory2,
            wsService2, statusFieldReader2, promptStateWriter: failWriter);

        var result = await cmd.ExecuteAsync("42");

        // Command succeeds despite writer failing to write (directory doesn't exist)
        result.ShouldBe(0);
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
            _adoService, _consoleInput, editorLauncher, _formatterFactory, _hintEngine, writer);

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
        var cmd = new NoteCommand(noteResolver, _workItemRepo, _pendingChangeStore, _adoService,
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
            _pendingChangeStore, _consoleInput, _formatterFactory, writer);

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
        var refreshSyncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, refreshProtectedWriter, _pendingChangeStore, null, 30, 30);
        var refreshWorkingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);
        var refreshOrchestrator = new RefreshOrchestrator(_contextStore, _workItemRepo, _adoService,
            _pendingChangeStore, refreshProtectedWriter, refreshWorkingSetService, refreshSyncCoordinatorFactory,
            iterationService,
            Substitute.For<ITrackingService>());
        var cmd = new RefreshCommand(
            new CommandContext(new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true), _formatterFactory, _hintEngine, _config),
            _contextStore, iterationService, _paths, _processTypeStore, _fieldDefinitionStore,
            refreshOrchestrator, promptStateWriter: writer);

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        File.Exists(PromptJsonPath).ShouldBeTrue();
        var root = ReadPromptJson();
        root.GetProperty("id").GetInt32().ShouldBe(42);
    }
}
