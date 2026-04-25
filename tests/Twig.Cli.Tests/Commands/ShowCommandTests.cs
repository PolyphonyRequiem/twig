using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ShowCommandTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IWorkItemLinkRepository _linkRepo;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly ITelemetryClient _telemetryClient;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly TwigConfiguration _config;
    private readonly string _tempDir;
    private readonly TwigPaths _paths;
    private readonly ShowCommand _cmd;

    public ShowCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _linkRepo = Substitute.For<IWorkItemLinkRepository>();
        _telemetryClient = Substitute.For<ITelemetryClient>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();

        var adoService = Substitute.For<IAdoWorkItemService>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, adoService, protectedCacheWriter, pendingChangeStore, null, 30, 30);
        _config = new TwigConfiguration();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        _tempDir = Path.Combine(Path.GetTempPath(), "twig-show-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db"));

        _cmd = new ShowCommand(
            _workItemRepo,
            _linkRepo,
            _formatterFactory,
            _syncCoordinatorFactory,
            _config,
            paths: _paths,
            fieldDefinitionStore: _fieldDefinitionStore,
            processConfigProvider: _processConfigProvider,
            telemetryClient: _telemetryClient);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── Factory helpers ─────────────────────────────────────────────

    private ShowCommand CreateCommandWithPipeline(RenderingPipelineFactory pipelineFactory, TextWriter? stderr = null) =>
        new(_workItemRepo, _linkRepo, _formatterFactory,
            _syncCoordinatorFactory, _config,
            pipelineFactory: pipelineFactory, paths: _paths,
            fieldDefinitionStore: _fieldDefinitionStore,
            processConfigProvider: _processConfigProvider,
            telemetryClient: _telemetryClient,
            stderr: stderr);

    // ═══════════════════════════════════════════════════════════════
    //  Cache miss: item not found
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_ItemNotInCache_ReturnsExitCode1WithMessageAndTelemetry()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var stderrWriter = new StringWriter();
        var cmd = new ShowCommand(_workItemRepo, _linkRepo, _formatterFactory,
            _syncCoordinatorFactory, _config,
            telemetryClient: _telemetryClient, stderr: stderrWriter);

        var result = await cmd.ExecuteAsync(999);

        result.ShouldBe(1);
        var stderr = stderrWriter.ToString();
        stderr.ShouldContain("Work item #999 not found in local cache");
        stderr.ShouldContain("twig set 999");
        _telemetryClient.Received().TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(d =>
                d["command"] == "show" &&
                d["exit_code"] == "1"),
            Arg.Any<Dictionary<string, double>>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Enrichment: children, parent, links
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_ItemWithParent_LoadsParentFromCache()
    {
        var item = new WorkItemBuilder(42, "Child Task").WithParent(10).Build();
        SetupCachedItem(item);

        var result = await _cmd.ExecuteAsync(42);

        result.ShouldBe(0);
        await _workItemRepo.Received().GetByIdAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Show_ItemWithoutParent_SkipsParentLookup()
    {
        var item = new WorkItemBuilder(42, "Root Item").Build();
        SetupCachedItem(item);

        var result = await _cmd.ExecuteAsync(42);

        result.ShouldBe(0);
        // GetByIdAsync called once for the item itself, not for a parent
        await _workItemRepo.Received(1).GetByIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Show_LoadsChildrenFromCache()
    {
        var item = new WorkItemBuilder(42, "Parent Item").Build();
        var child = new WorkItemBuilder(43, "Child Task").WithParent(42).Build();
        SetupCachedItem(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { child });

        var result = await _cmd.ExecuteAsync(42);

        result.ShouldBe(0);
        await _workItemRepo.Received().GetChildrenAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Show_LoadsLinksFromCache()
    {
        var item = new WorkItemBuilder(42, "Linked Item").Build();
        SetupCachedItem(item);

        var result = await _cmd.ExecuteAsync(42);

        result.ShouldBe(0);
        await _linkRepo.Received().GetLinksAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Show_LinkLoadFailure_StillSucceeds()
    {
        var item = new WorkItemBuilder(42, "Item").Build();
        SetupCachedItem(item);
        _linkRepo.GetLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WorkItemLink>>(_ => throw new InvalidOperationException("DB error"));

        var result = await _cmd.ExecuteAsync(42);

        result.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Output format paths
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_JsonFormat_ReturnsJsonOutput()
    {
        var item = new WorkItemBuilder(42, "JSON Item").Build();
        SetupCachedItem(item);

        var output = await CaptureStdout(() => _cmd.ExecuteAsync(42, "json"));

        output.ShouldContain("\"id\": 42");
        output.ShouldContain("\"title\": \"JSON Item\"");
    }

    [Fact]
    public async Task Show_MinimalFormat_ReturnsOutput()
    {
        var item = new WorkItemBuilder(42, "Minimal Item").Build();
        SetupCachedItem(item);

        var output = await CaptureStdout(() => _cmd.ExecuteAsync(42, "minimal"));

        output.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Show_JsonCompactFormat_ReturnsCompactOutput()
    {
        var item = new WorkItemBuilder(42, "Compact Item").Build();
        SetupCachedItem(item);

        var output = await CaptureStdout(() => _cmd.ExecuteAsync(42, "json-compact"));

        output.ShouldContain("\"id\": 42");
        output.ShouldContain("\"title\": \"Compact Item\"");
        output.ShouldContain("\"type\":");
        output.ShouldContain("\"state\":");
        // Compact format should NOT include verbose fields like iterationPath
        output.ShouldNotContain("\"iterationPath\"");
    }

    [Fact]
    public async Task Show_HumanFormat_Redirected_FormatsWithoutRenderer()
    {
        var item = new WorkItemBuilder(42, "Human Item").Build();
        SetupCachedItem(item);
        var spectreRenderer = Substitute.For<IAsyncRenderer>();
        var cmd = CreateCommandWithPipeline(new RenderingPipelineFactory(_formatterFactory, spectreRenderer, isOutputRedirected: () => true));

        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "human"));

        output.ShouldContain("42");
        output.ShouldContain("Human Item");
    }

    [Fact]
    public async Task Show_HumanFormat_Tty_UsesAsyncRenderer()
    {
        var item = new WorkItemBuilder(42, "TTY Item").Build();
        SetupCachedItem(item);
        var mockRenderer = Substitute.For<IAsyncRenderer>();
        var ttyPipeline = new RenderingPipelineFactory(
            _formatterFactory, mockRenderer, isOutputRedirected: () => false);
        var cmd = CreateCommandWithPipeline(ttyPipeline);

        var result = await cmd.ExecuteAsync(42, "human");

        result.ShouldBe(0);
        await mockRenderer.Received().RenderStatusAsync(
            Arg.Any<Func<Task<WorkItem?>>>(),
            Arg.Any<Func<Task<IReadOnlyList<PendingChangeRecord>>>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<FieldDefinition>?>(),
            Arg.Any<IReadOnlyList<StatusFieldEntry>?>(),
            Arg.Any<(int Done, int Total)?>(),
            Arg.Any<IReadOnlyList<WorkItemLink>?>(),
            Arg.Any<WorkItem?>(),
            Arg.Any<IReadOnlyList<WorkItem>?>());
    }

    [Fact]
    public async Task Show_TtyPath_PassesEmptyPendingChangesFactory()
    {
        var item = new WorkItemBuilder(42, "TTY Empty Pending").Build();
        SetupCachedItem(item);
        Func<Task<IReadOnlyList<PendingChangeRecord>>>? capturedFactory = null;
        var mockRenderer = Substitute.For<IAsyncRenderer>();
        mockRenderer.RenderStatusAsync(
            Arg.Any<Func<Task<WorkItem?>>>(),
            Arg.Do<Func<Task<IReadOnlyList<PendingChangeRecord>>>>(f => capturedFactory = f),
            Arg.Any<CancellationToken>(),
            Arg.Any<IReadOnlyList<FieldDefinition>?>(),
            Arg.Any<IReadOnlyList<StatusFieldEntry>?>(),
            Arg.Any<(int Done, int Total)?>(),
            Arg.Any<IReadOnlyList<WorkItemLink>?>(),
            Arg.Any<WorkItem?>(),
            Arg.Any<IReadOnlyList<WorkItem>?>()).Returns(Task.CompletedTask);
        var ttyPipeline = new RenderingPipelineFactory(
            _formatterFactory, mockRenderer, isOutputRedirected: () => false);
        var cmd = CreateCommandWithPipeline(ttyPipeline);

        await cmd.ExecuteAsync(42, "human");

        capturedFactory.ShouldNotBeNull();
        var pendingChanges = await capturedFactory!();
        pendingChanges.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_Success_EmitsTelemetry()
    {
        var item = new WorkItemBuilder(42, "Telemetry Item").Build();
        SetupCachedItem(item);

        await _cmd.ExecuteAsync(42, "json");

        _telemetryClient.Received().TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(d =>
                d["command"] == "show" &&
                d["exit_code"] == "0" &&
                d["output_format"] == "json"),
            Arg.Is<Dictionary<string, double>>(m =>
                m.ContainsKey("duration_ms") && m["duration_ms"] >= 0));
    }

    // ═══════════════════════════════════════════════════════════════
    //  No side effects: no ADO fetch, no context mutation, no sync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_NeverWritesToRepository()
    {
        var item = new WorkItemBuilder(42, "Read Only").Build();
        SetupCachedItem(item);

        await _cmd.ExecuteAsync(42);

        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().SaveBatchAsync(Arg.Any<IEnumerable<WorkItem>>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Minimal dependencies: all optional params null
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Show_MinimalDependencies_StillWorks()
    {
        var item = new WorkItemBuilder(42, "Minimal Deps").Build();
        SetupCachedItem(item);
        var cmd = new ShowCommand(_workItemRepo, _linkRepo, _formatterFactory, _syncCoordinatorFactory, _config);

        var output = await CaptureStdout(() => cmd.ExecuteAsync(42, "json"));

        output.ShouldContain("\"id\": 42");
    }

    // ── Private helpers ─────────────────────────────────────────────

    private void SetupCachedItem(WorkItem item)
    {
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _linkRepo.GetLinksAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());
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
}
