using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class PatchCommandTelemetryTests : IDisposable
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IConsoleInput _consoleInput;
    private readonly IFieldDefinitionStore _fieldDefStore;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly ITelemetryClient _telemetryClient;
    private readonly StringWriter _stderr;
    private readonly StringWriter _stdout;

    public PatchCommandTelemetryTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _telemetryClient = Substitute.For<ITelemetryClient>();
        _fieldDefStore = Substitute.For<IFieldDefinitionStore>();
        _fieldDefStore.GetByReferenceNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((FieldDefinition?)null);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(),
            new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()),
            new MinimalOutputFormatter(), new IdsOutputFormatter());

        _stderr = new StringWriter();
        _stdout = new StringWriter();
    }

    private PatchCommand CreateCommand(ITelemetryClient? telemetry = null, TextReader? stdin = null)
    {
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var workflow = new Twig.Infrastructure.Services.Mutation.PatchWorkflow(
            _workItemRepo, _adoService, _pendingChangeStore);
        return new PatchCommand(
            resolver,
            _adoService,
            _consoleInput,
            _workItemRepo,
            _fieldDefStore,
            workflow,
            _formatterFactory,
            telemetryClient: telemetry ?? _telemetryClient,
            stdinReader: stdin,
            stderr: _stderr,
            stdout: _stdout);
    }

    private void SetActiveItem(int id, string title = "Test Item")
    {
        var item = new WorkItemBuilder(id, title).InState("Active").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(id);
        _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(item);
        // Return same item as remote (no conflict)
        _adoService.FetchAsync(id, Arg.Any<CancellationToken>()).Returns(item);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry — success with field_count
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_Success_EmitsTelemetryWithPatchCommandAndFieldCount()
    {
        SetActiveItem(42);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"New Title","System.Description":"Desc"}""");

        result.ShouldBe(0);
        _telemetryClient.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(p =>
                p["command"] == "patch" &&
                p["exit_code"] == "0"),
            Arg.Is<Dictionary<string, double>>(m =>
                m.ContainsKey("duration_ms") &&
                m["field_count"] == 2));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry — single field
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_SingleField_EmitsFieldCountOne()
    {
        SetActiveItem(42);

        await CreateCommand().ExecuteAsync(json: """{"System.Title":"Updated"}""");

        _telemetryClient.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Any<Dictionary<string, string>>(),
            Arg.Is<Dictionary<string, double>>(m =>
                m["field_count"] == 1));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry — validation error (no input) emits exit_code 2
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NoInput_EmitsTelemetryWithExitCodeTwo()
    {
        var result = await CreateCommand().ExecuteAsync();

        result.ShouldBe(2);
        _telemetryClient.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(p =>
                p["command"] == "patch" &&
                p["exit_code"] == "2"),
            Arg.Is<Dictionary<string, double>>(m =>
                m.ContainsKey("duration_ms") &&
                m["field_count"] == 0));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry — invalid JSON emits exit_code 2
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_InvalidJson_EmitsTelemetryWithExitCodeTwo()
    {
        var result = await CreateCommand().ExecuteAsync(json: "not-json");

        result.ShouldBe(2);
        _telemetryClient.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(p =>
                p["command"] == "patch" &&
                p["exit_code"] == "2"),
            Arg.Any<Dictionary<string, double>>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry — no active item emits exit_code 1 with field_count
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NoActiveItem_EmitsTelemetryWithExitCodeOne()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"New Title"}""");

        result.ShouldBe(1);
        _telemetryClient.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(p =>
                p["command"] == "patch" &&
                p["exit_code"] == "1"),
            Arg.Is<Dictionary<string, double>>(m =>
                m["field_count"] == 1));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry — concurrency conflict emits exit_code 1
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_ConcurrencyConflict_EmitsTelemetryWithExitCodeOne()
    {
        SetActiveItem(42);
        _adoService.PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<Domain.ValueObjects.FieldChange>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(2));

        var result = await CreateCommand().ExecuteAsync(
            json: """{"System.Title":"Conflict"}""");

        result.ShouldBe(1);
        _telemetryClient.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(p =>
                p["command"] == "patch" &&
                p["exit_code"] == "1"),
            Arg.Is<Dictionary<string, double>>(m =>
                m["field_count"] == 1));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry — null client does not throw
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NullTelemetryClient_DoesNotThrow()
    {
        SetActiveItem(42);

        var cmd = CreateCommand(telemetry: null!);
        // Re-create without telemetry client
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var workflow2 = new Twig.Infrastructure.Services.Mutation.PatchWorkflow(
            _workItemRepo, _adoService, _pendingChangeStore);
        cmd = new PatchCommand(
            resolver,
            _adoService,
            _consoleInput,
            _workItemRepo,
            _fieldDefStore,
            workflow2,
            _formatterFactory,
            telemetryClient: null,
            stderr: _stderr,
            stdout: _stdout);

        var result = await cmd.ExecuteAsync(json: """{"System.Title":"No Crash"}""");

        result.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry — stdin input reports correct field_count
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_Stdin_EmitsCorrectFieldCount()
    {
        SetActiveItem(42);
        var stdinContent = """{"System.Title":"FromStdin","System.State":"Active","Custom.Field":"Value"}""";
        var stdin = new StringReader(stdinContent);

        await CreateCommand(stdin: stdin).ExecuteAsync(readStdin: true);

        _telemetryClient.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(p =>
                p["command"] == "patch"),
            Arg.Is<Dictionary<string, double>>(m =>
                m["field_count"] == 3));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry — duration_ms is always emitted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_AlwaysEmitsDurationMs()
    {
        SetActiveItem(42);

        await CreateCommand().ExecuteAsync(json: """{"System.Title":"Test"}""");

        _telemetryClient.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Any<Dictionary<string, string>>(),
            Arg.Is<Dictionary<string, double>>(m =>
                m.ContainsKey("duration_ms") &&
                m["duration_ms"] >= 0));
    }

    public void Dispose()
    {
        _stderr.Dispose();
        _stdout.Dispose();
    }
}
