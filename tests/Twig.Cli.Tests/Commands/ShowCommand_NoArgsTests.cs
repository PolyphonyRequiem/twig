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

public sealed class ShowCommand_NoArgsTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IWorkItemLinkRepository _linkRepo;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IContextStore _contextStore;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly StatusFieldConfigReader _statusFieldReader;
    private readonly string _tempDir;

    public ShowCommand_NoArgsTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _linkRepo = Substitute.For<IWorkItemLinkRepository>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _contextStore = Substitute.For<IContextStore>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var iterationService = Substitute.For<IIterationService>();
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);

        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedCacheWriter, _pendingChangeStore, null, 30, 30);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        _tempDir = Path.Combine(Path.GetTempPath(), "twig-show-noargs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _statusFieldReader = new StatusFieldConfigReader(
            new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── Factory helpers ─────────────────────────────────────────────

    private ShowCommand CreateCommand(TextWriter? stderr = null, TwigPaths? paths = null)
    {
        var pipelineFactory = new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true);
        var ctx = new CommandContext(pipelineFactory, _formatterFactory,
            new HintEngine(new DisplayConfig { Hints = false }), new TwigConfiguration(),
            TelemetryClient: Substitute.For<ITelemetryClient>(), Stderr: stderr);
        return new ShowCommand(ctx, _workItemRepo, _linkRepo,
            _syncCoordinatorFactory, _statusFieldReader,
            fieldDefinitionStore: _fieldDefinitionStore,
            processConfigProvider: _processConfigProvider,
            contextStore: _contextStore,
            activeItemResolver: _activeItemResolver,
            pendingChangeStore: _pendingChangeStore,
            workingSetService: _workingSetService,
            twigPaths: paths);
    }

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _linkRepo.GetLinksAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  No-args: active item found in cache
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoArgs_ActiveItemInCache_ReturnsExitCode0()
    {
        var item = new WorkItemBuilder(42, "Active Item").Build();
        SetupActiveItem(item);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task NoArgs_ActiveItemInCache_OutputsItemInfo()
    {
        var item = new WorkItemBuilder(42, "Active Item").Build();
        SetupActiveItem(item);

        var cmd = CreateCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(outputFormat: "json"));

        output.ShouldContain("\"id\": 42");
        output.ShouldContain("\"title\": \"Active Item\"");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No-args: active item fetched from ADO on cache miss
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoArgs_ActiveItemNotInCache_FetchesFromAdo()
    {
        var item = new WorkItemBuilder(42, "Fetched Item").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        // Cache miss first, then after save it's available
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(null as WorkItem, null as WorkItem, item, item, item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  No-args: no active item → branch detection hint
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoArgs_NoActiveItem_ReturnsExitCode1()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr: stderr);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        stderr.ToString().ShouldContain("No active work item");
        stderr.ToString().ShouldContain("twig set <id>");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No-args: active item unreachable
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoArgs_ActiveItemUnreachable_ReturnsExitCode1()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(null as WorkItem);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns<WorkItem>(_ => throw new InvalidOperationException("Network error"));

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr: stderr);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        stderr.ToString().ShouldContain("#42");
        stderr.ToString().ShouldContain("not reachable");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No-args: no context services available
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoArgs_NoContextServices_ReturnsExitCode1()
    {
        // Create command without context services
        var pipelineFactory = new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true);
        var stderr = new StringWriter();
        var cmdCtx = new CommandContext(pipelineFactory, _formatterFactory,
            new HintEngine(new DisplayConfig { Hints = false }), new TwigConfiguration(),
            TelemetryClient: Substitute.For<ITelemetryClient>(), Stderr: stderr);
        var cmd = new ShowCommand(cmdCtx, _workItemRepo, _linkRepo,
            _syncCoordinatorFactory, _statusFieldReader);

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
        stderr.ToString().ShouldContain("context services not available");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No-args: enrichment (children, parent, links)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoArgs_LoadsChildrenAndParent()
    {
        var parent = new WorkItemBuilder(10, "Parent").Build();
        var item = new WorkItemBuilder(42, "Child Task").WithParent(10).Build();
        SetupActiveItem(item);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(parent);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _workItemRepo.Received().GetChildrenAsync(42, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().GetByIdAsync(10, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  By-ID path: explicit ID still works (regression guard)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ByIdPath_ExplicitId_StillWorks()
    {
        var item = new WorkItemBuilder(99, "Explicit Item").Build();
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(99, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _linkRepo.GetLinksAsync(99, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(99);

        result.ShouldBe(0);
        // Should NOT touch context store when ID is explicit
        await _contextStore.DidNotReceive().GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Branch detection hint extraction
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("feature/1234-add-login", 1234)]
    [InlineData("feature/2149-pg-2", 2149)]
    [InlineData("users/name/42", 42)]
    [InlineData("bug/999", 999)]
    [InlineData("hotfix/55-fix-crash", 55)]
    public void ExtractWorkItemIdFromBranch_CommonPatterns_ExtractsId(string branchName, int expectedId)
    {
        ShowCommand.ExtractWorkItemIdFromBranch(branchName).ShouldBe(expectedId);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    [InlineData("feature/add-login")]
    [InlineData("feature/no-number-here")]
    public void ExtractWorkItemIdFromBranch_NoBranchId_ReturnsNull(string branchName)
    {
        ShowCommand.ExtractWorkItemIdFromBranch(branchName).ShouldBeNull();
    }

    [Fact]
    public void ExtractWorkItemIdFromBranch_ZeroId_ReturnsNull()
    {
        ShowCommand.ExtractWorkItemIdFromBranch("feature/0-something").ShouldBeNull();
    }

    [Fact]
    public void ExtractWorkItemIdFromBranch_NegativeId_ReturnsNull()
    {
        // "-1234" has dashIndex = 0, so candidate is empty → TryParse fails
        ShowCommand.ExtractWorkItemIdFromBranch("feature/-1234").ShouldBeNull();
    }

    // ── Private helpers ─────────────────────────────────────────────

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
}
