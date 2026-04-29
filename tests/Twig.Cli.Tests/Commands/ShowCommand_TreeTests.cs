using NSubstitute;
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

public sealed class ShowCommand_TreeTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IWorkItemLinkRepository _linkRepo;
    private readonly IContextStore _contextStore;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly ITelemetryClient _telemetryClient;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly WorkingSetService _workingSetService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly StatusFieldConfigReader _statusFieldReader;
    private readonly string _tempDir;

    public ShowCommand_TreeTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _linkRepo = Substitute.For<IWorkItemLinkRepository>();
        _contextStore = Substitute.For<IContextStore>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _telemetryClient = Substitute.For<ITelemetryClient>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _linkRepo.GetLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);

        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);

        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedCacheWriter, _pendingChangeStore, null, 30, 30);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter(), new IdsOutputFormatter());

        _tempDir = Path.Combine(Path.GetTempPath(), "twig-show-tree-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _statusFieldReader = new StatusFieldConfigReader(
            new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── Helpers ────────────────────────────────────────────────────

    private CommandContext CreateCtx(RenderingPipelineFactory? pipelineFactory = null) =>
        new(pipelineFactory ?? new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory,
            new HintEngine(new DisplayConfig { Hints = false }),
            new TwigConfiguration(),
            TelemetryClient: _telemetryClient);

    private TreeRenderingService CreateTreeService(CommandContext? ctx = null) =>
        new(ctx ?? CreateCtx(), _contextStore, _workItemRepo, _activeItemResolver,
            _workingSetService, _syncCoordinatorFactory, _processTypeStore);

    private ShowCommand CreateShowCommand(CommandContext? ctx = null, TreeRenderingService? treeSvc = null)
    {
        var c = ctx ?? CreateCtx();
        return new ShowCommand(c, _workItemRepo, _linkRepo,
            _syncCoordinatorFactory, _statusFieldReader,
            fieldDefinitionStore: _fieldDefinitionStore,
            processConfigProvider: _processConfigProvider,
            contextStore: _contextStore,
            activeItemResolver: _activeItemResolver,
            pendingChangeStore: _pendingChangeStore,
            workingSetService: _workingSetService,
            treeRenderingService: treeSvc ?? CreateTreeService(c));
    }

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
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
    //  --tree flag delegates to TreeRenderingService
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_TreeFlag_ById_ReturnsSuccess()
    {
        var item = new WorkItemBuilder(42, "Tree Item").Build();
        SetupActiveItem(item);

        var cmd = CreateShowCommand();
        var result = await cmd.ExecuteAsync(42, tree: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Show_TreeFlag_NoId_UsesActiveItem()
    {
        var item = new WorkItemBuilder(42, "Active Tree").Build();
        SetupActiveItem(item);

        var cmd = CreateShowCommand();
        var result = await cmd.ExecuteAsync(tree: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Show_TreeFlag_JsonOutput_ReturnsSuccess()
    {
        var item = new WorkItemBuilder(42, "JSON Tree").Build();
        SetupActiveItem(item);

        var cmd = CreateShowCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json", tree: true));

        output.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Show_TreeFlag_MinimalOutput_ReturnsSuccess()
    {
        var item = new WorkItemBuilder(42, "Minimal Tree").Build();
        SetupActiveItem(item);

        var cmd = CreateShowCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "minimal", tree: true));

        output.ShouldNotBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  --tree off — normal show behaviour is unchanged
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_WithoutTreeFlag_RenderNormalCard()
    {
        var item = new WorkItemBuilder(42, "Normal Show").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());

        var cmd = CreateShowCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json", tree: false));

        // Normal show returns single item JSON, not tree format
        output.ShouldContain("\"id\": 42");
        output.ShouldContain("\"title\": \"Normal Show\"");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry: tree=true is tracked
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_TreeFlag_TracksTelemetryWithTreeProperty()
    {
        var item = new WorkItemBuilder(42, "Telemetry Tree").Build();
        SetupActiveItem(item);

        var cmd = CreateShowCommand();
        await cmd.ExecuteAsync(42, tree: true);

        _telemetryClient.Received().TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(d =>
                d["command"] == "show" &&
                d["tree"] == "true" &&
                d["exit_code"] == "0"),
            Arg.Any<Dictionary<string, double>>());
    }

    [Fact]
    public async Task Show_WithoutTreeFlag_TelemetryDoesNotIncludeTreeProperty()
    {
        var item = new WorkItemBuilder(42, "Normal Telemetry").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());

        var cmd = CreateShowCommand();
        await cmd.ExecuteAsync(42, tree: false);

        _telemetryClient.Received().TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(d =>
                d["command"] == "show" &&
                !d.ContainsKey("tree")),
            Arg.Any<Dictionary<string, double>>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Explicit --id parameter with --tree
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_TreeFlag_ExplicitId_RendersTreeForSpecificItem()
    {
        var item = new WorkItemBuilder(42, "Specific Tree Item").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateShowCommand();
        var result = await cmd.ExecuteAsync(42, tree: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Show_TreeFlag_ExplicitId_NotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Not found")));

        var cmd = CreateShowCommand();
        var result = await cmd.ExecuteAsync(99, tree: true);

        result.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Sibling counts in --tree output (migrated from TreeCommandAsyncTests)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_TreeFlag_JsonOutput_IncludesParentChainAndChildren()
    {
        var parent = new WorkItemBuilder(100, "Epic Parent").AsEpic().Build();
        var focus = new WorkItemBuilder(42, "Focus Task").WithParent(100).Build();
        var child = new WorkItemBuilder(50, "Child Item").WithParent(42).Build();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _workItemRepo.GetChildrenAsync(50, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { focus });

        var cmd = CreateShowCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json", tree: true));

        output.ShouldContain("\"focus\"");
        output.ShouldContain("\"parentChain\"");
        output.ShouldContain("\"children\"");
        output.ShouldContain("\"totalChildren\": 1");
        output.ShouldContain("Epic Parent");
        output.ShouldContain("Child Item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Links rendering in --tree output (migrated from TreeCommandLinkTests)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_TreeFlag_JsonOutput_IncludesLinks()
    {
        var focus = new WorkItemBuilder(42, "Linked Tree Item").Build();
        SetupActiveItem(focus);

        // Provide link data via the sync coordinator
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(focus);

        var cmd = CreateShowCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json", tree: true));

        // JSON tree output should include links array (even if empty)
        output.ShouldContain("\"links\"");
    }

    [Fact]
    public async Task Show_TreeFlag_JsonOutput_GrandchildrenIncluded()
    {
        var focus = new WorkItemBuilder(1, "Epic").AsEpic().Build();
        var child = new WorkItemBuilder(10, "Issue").WithParent(1).Build();
        var grandchild = new WorkItemBuilder(100, "Task").WithParent(10).Build();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { grandchild });
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateShowCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(1, "json", tree: true));

        output.ShouldNotBeEmpty();
        // Grandchildren should be fetched
        await _workItemRepo.Received().GetChildrenAsync(10, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  --tree off — normal show behaviour is unchanged
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_TreeFlag_NoRefresh_SkipsSyncPass()
    {
        var item = new WorkItemBuilder(42, "NoRefresh Tree").Build();
        SetupActiveItem(item);

        var cmd = CreateShowCommand();
        var result = await cmd.ExecuteAsync(42, tree: true, noRefresh: true);

        result.ShouldBe(0);
        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  TreeRenderingService not injected — graceful error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_TreeFlag_NoTreeService_ReturnsError()
    {
        var stderrWriter = new StringWriter();
        var ctx = new CommandContext(
            new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory, new HintEngine(new DisplayConfig { Hints = false }),
            new TwigConfiguration(), TelemetryClient: _telemetryClient, Stderr: stderrWriter);

        var cmd = new ShowCommand(ctx, _workItemRepo, _linkRepo,
            _syncCoordinatorFactory, _statusFieldReader,
            treeRenderingService: null);

        var result = await cmd.ExecuteAsync(42, tree: true);

        result.ShouldBe(1);
        stderrWriter.ToString().ShouldContain("Tree rendering is not available");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Tree with parent chain
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_TreeFlag_ItemWithParent_RendersHierarchy()
    {
        var parent = new WorkItemBuilder(10, "Parent Epic").Build();
        var item = new WorkItemBuilder(42, "Child Task").WithParent(10).Build();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetParentChainAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateShowCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "minimal", tree: true));

        output.ShouldNotBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  No active item when --tree and no id
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_TreeFlag_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateShowCommand();
        var result = await cmd.ExecuteAsync(tree: true);

        result.ShouldBe(1);
    }
}
