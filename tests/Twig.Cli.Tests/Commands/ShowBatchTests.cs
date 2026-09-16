using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;

using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ShowBatchTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IWorkItemLinkRepository _linkRepo;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly ITelemetryClient _telemetryClient;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly StringWriter _stderr;
    private readonly ShowCommand _cmd;

    public ShowBatchTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _linkRepo = Substitute.For<IWorkItemLinkRepository>();
        _telemetryClient = Substitute.For<ITelemetryClient>();

        var adoService = Substitute.For<IAdoWorkItemService>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, adoService, protectedCacheWriter, pendingChangeStore, _linkRepo, 30, 30);

        _formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());

        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var pipelineFactory = new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true);
        _stderr = new StringWriter();
        var ctx = new CommandContext(pipelineFactory, _formatterFactory, hintEngine, new TwigConfiguration(), TelemetryClient: _telemetryClient, Stderr: _stderr);

        var tempDir = Path.Combine(Path.GetTempPath(), "twig-showbatch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var statusFieldReader = new StatusFieldConfigReader(new TwigPaths(tempDir, Path.Combine(tempDir, "config"), Path.Combine(tempDir, "twig.db")));

        _cmd = new ShowCommand(
            ctx,
            _workItemRepo,
            _linkRepo,
            _syncCoordinatorFactory,
            statusFieldReader);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty batch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShowBatch_EmptyString_ReturnsExitCode0AndEmptyJsonArray()
    {
        var (result, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("", "json"));

        result.ShouldBe(0);
        output.Trim().ShouldBe("[]");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single ID
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShowBatch_SingleId_ReturnsJsonArrayWithOneItem()
    {
        var item = new WorkItemBuilder(42, "Single Item").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("42", "json"));

        output.ShouldContain("\"id\": 42");
        output.ShouldContain("\"title\": \"Single Item\"");
        // Should be an array, not an object
        output.Trim().ShouldStartWith("[");
        output.Trim().ShouldEndWith("]");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Multiple IDs
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShowBatch_MultipleIds_ReturnsAllFoundItems()
    {
        var item1 = new WorkItemBuilder(10, "First Item").Build();
        var item2 = new WorkItemBuilder(20, "Second Item").Build();
        var item3 = new WorkItemBuilder(30, "Third Item").Build();
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item2);
        _workItemRepo.GetByIdAsync(30, Arg.Any<CancellationToken>()).Returns(item3);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("10,20,30", "json"));

        output.ShouldContain("\"id\": 10");
        output.ShouldContain("\"id\": 20");
        output.ShouldContain("\"id\": 30");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Missing IDs — AB#880 structured disclosure + non-zero exit
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShowBatch_MissingIds_DisclosedOnStderrAndReturnsExit1()
    {
        // AB#880: a missing id used to be silently dropped; now the stdout
        // array keeps its found-items shape, the shortfall is disclosed on
        // stderr as `#N` tokens, and the exit code becomes 1.
        var item = new WorkItemBuilder(10, "Found Item").Build();
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var (result, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("10,99", "json"));

        result.ShouldBe(1);
        output.ShouldContain("\"id\": 10");
        output.ShouldNotContain("\"id\": 99");
        var stderr = _stderr.ToString();
        stderr.ShouldContain("#99");
        stderr.ShouldContain("not found in cache");
    }

    [Fact]
    public async Task ShowBatch_MissingIds_JsonFormat_StderrIsStructuredJson()
    {
        // JSON format routes stderr through CommandError.Write → structured
        // `{ "error": "…" }`. Every missing id MUST appear.
        _workItemRepo.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var (result, _) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("100,200,300", "json"));

        result.ShouldBe(1);
        var stderr = _stderr.ToString().Trim();
        stderr.ShouldStartWith("{");
        stderr.ShouldContain("\"error\"");
        stderr.ShouldContain("#100");
        stderr.ShouldContain("#200");
        stderr.ShouldContain("#300");
    }

    [Fact]
    public async Task ShowBatch_AllFound_HasNoStderr_AndReturnsExit0()
    {
        var a = new WorkItemBuilder(11, "Alpha").Build();
        var b = new WorkItemBuilder(22, "Beta").Build();
        _workItemRepo.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(a);
        _workItemRepo.GetByIdAsync(22, Arg.Any<CancellationToken>()).Returns(b);

        var (result, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("11,22", "json"));

        result.ShouldBe(0);
        output.ShouldContain("\"id\": 11");
        output.ShouldContain("\"id\": 22");
        _stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task ShowBatch_AllMissing_EmptyStdoutArray_Exit1_AllIdsDisclosed()
    {
        _workItemRepo.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var (result, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("1,2,3", "json"));

        // Empty JSON array on stdout preserves the found-items contract; the
        // shortfall lives on stderr and in the exit code.
        output.Trim().ShouldBe("[]");
        result.ShouldBe(1);
        var stderr = _stderr.ToString();
        stderr.ShouldContain("#1");
        stderr.ShouldContain("#2");
        stderr.ShouldContain("#3");
    }

    // ═══════════════════════════════════════════════════════════════
    //  JSON format validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShowBatch_JsonFormat_IncludesExpectedFields()
    {
        var item = new WorkItemBuilder(42, "Field Check")
            .InState("Active")
            .AssignedTo("Test User")
            .Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("42", "json"));

        output.ShouldContain("\"id\": 42");
        output.ShouldContain("\"title\": \"Field Check\"");
        output.ShouldContain("\"state\": \"Active\"");
        output.ShouldContain("\"assignedTo\":");
        output.ShouldContain("\"type\":");
        output.ShouldContain("\"areaPath\":");
        output.ShouldContain("\"iterationPath\":");
        output.ShouldContain("\"isSeed\":");
        output.ShouldContain("\"parentId\":");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Non-numeric segments ignored
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShowBatch_NonNumericSegments_AreIgnored()
    {
        var item = new WorkItemBuilder(42, "Valid Item").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("abc,42,xyz", "json"));

        output.ShouldContain("\"id\": 42");
        await _workItemRepo.DidNotReceive().GetByIdAsync(0, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Whitespace and extra commas
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShowBatch_WhitespaceAroundIds_ParsedCorrectly()
    {
        var item = new WorkItemBuilder(42, "Trimmed").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync(" 42 ", "json"));

        output.ShouldContain("\"id\": 42");
    }

    [Fact]
    public async Task ShowBatch_TrailingComma_HandledGracefully()
    {
        var item = new WorkItemBuilder(42, "Trailing").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("42,", "json"));

        output.ShouldContain("\"id\": 42");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache-only: no writes to repository
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShowBatch_NeverWritesToRepository()
    {
        var item = new WorkItemBuilder(42, "Read Only").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        await _cmd.ExecuteBatchAsync("42", "json");

        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().SaveBatchAsync(Arg.Any<IEnumerable<WorkItem>>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShowBatch_EmitsTelemetry()
    {
        var item = new WorkItemBuilder(42, "Telemetry").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        await _cmd.ExecuteBatchAsync("42", "json");

        _telemetryClient.Received().TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(d =>
                d["command"] == "show-batch" &&
                d["exit_code"] == "0" &&
                d["output_format"] == "json"),
            Arg.Is<Dictionary<string, double>>(m =>
                m.ContainsKey("duration_ms") && m["duration_ms"] >= 0));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Human format fallback
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShowBatch_HumanFormat_OutputsEachItem()
    {
        var item1 = new WorkItemBuilder(10, "Human One").Build();
        var item2 = new WorkItemBuilder(20, "Human Two").Build();
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item2);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("10,20", "human"));

        output.ShouldContain("Human One");
        output.ShouldContain("Human Two");
    }

    // ═══════════════════════════════════════════════════════════════
    //  IDs format — bare numeric output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ShowBatch_IdsFormat_OutputsOneIdPerLine()
    {
        var item1 = new WorkItemBuilder(10, "First").Build();
        var item2 = new WorkItemBuilder(20, "Second").Build();
        var item3 = new WorkItemBuilder(30, "Third").Build();
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item2);
        _workItemRepo.GetByIdAsync(30, Arg.Any<CancellationToken>()).Returns(item3);

        var (result, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("10,20,30", "ids"));

        result.ShouldBe(0);
        var lines = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        lines.ShouldBe(new[] { "10", "20", "30" });
    }

    [Fact]
    public async Task ShowBatch_IdsFormat_Empty_ProducesNoOutput()
    {
        var (result, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("", "ids"));

        result.ShouldBe(0);
        output.Trim().ShouldBeEmpty();
    }

    [Fact]
    public async Task ShowBatch_IdsFormat_MissingItems_StdoutOnlyFound_ExitNonZero()
    {
        // The ids surface keeps its found-only shape — a walker reading the id
        // stream still gets one id per line — but AB#880 non-zero exit still
        // fires and the missing id is disclosed on stderr. This preserves the
        // ids consumer contract while making the miss legible to a caller who
        // wants completeness.
        var item = new WorkItemBuilder(10, "Found").Build();
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var (result, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("10,99", "ids"));

        result.ShouldBe(1);
        var lines = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        lines.ShouldBe(new[] { "10" });
        _stderr.ToString().ShouldContain("#99");
    }

    [Fact]
    public async Task ShowBatch_IdsFormat_PreservesInputOrder()
    {
        var item1 = new WorkItemBuilder(30, "Third").Build();
        var item2 = new WorkItemBuilder(10, "First").Build();
        var item3 = new WorkItemBuilder(20, "Second").Build();
        _workItemRepo.GetByIdAsync(30, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item2);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item3);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("30,10,20", "ids"));

        var lines = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).ToArray();
        lines.ShouldBe(new[] { "30", "10", "20" });
    }

    [Fact]
    public async Task ShowBatch_IdsFormat_SingleId_OutputsBareNumber()
    {
        var item = new WorkItemBuilder(42, "Solo").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var (_, output) = await StdoutCapture.RunAsync(() => _cmd.ExecuteBatchAsync("42", "ids"));

        output.Trim().ShouldBe("42");
    }

}
