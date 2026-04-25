using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class DiscardCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IConsoleInput _consoleInput;
    private readonly IPromptStateWriter _promptStateWriter;
    private readonly ITelemetryClient _telemetryClient;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly DiscardCommand _cmd;

    public DiscardCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _promptStateWriter = Substitute.For<IPromptStateWriter>();
        _telemetryClient = Substitute.For<ITelemetryClient>();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        _cmd = new DiscardCommand(
            _workItemRepo,
            _pendingChangeStore,
            _consoleInput,
            _formatterFactory,
            _promptStateWriter,
            _telemetryClient);
    }

    // ═══════════════════════════════════════════════════════════════
    //  DD-10: Parameter exclusivity guard
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_BothIdAndAll_ReturnsError()
    {
        var (result, stderr) = await StderrCapture.RunAsync(
            () => _cmd.ExecuteAsync(id: 42, all: true));

        result.ShouldBe(1);
        stderr.ShouldContain("Specify either <id> or --all, not both.");
    }

    [Fact]
    public async Task Execute_NeitherIdNorAll_ReturnsError()
    {
        var (result, stderr) = await StderrCapture.RunAsync(
            () => _cmd.ExecuteAsync(id: null, all: false));

        result.ShouldBe(1);
        stderr.ShouldContain("Specify <id> or --all");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-item flow: item not found
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_Single_ItemNotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => _cmd.ExecuteAsync(id: 999));

        result.ShouldBe(1);
        stderr.ShouldContain("Work item #999 not found.");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-item flow: seed guard
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_Single_SeedItem_ReturnsError()
    {
        var seed = new WorkItemBuilder(-1, "My Seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => _cmd.ExecuteAsync(id: -1));

        result.ShouldBe(1);
        stderr.ShouldContain("seed");
        stderr.ShouldContain("twig seed discard");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-item flow: no-op (no pending changes, not dirty)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_Single_NoPendingChanges_NotDirty_ReturnsNoOp()
    {
        var item = new WorkItemBuilder(42, "Clean Item").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>()).Returns((0, 0));

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(id: 42);

        result.ShouldBe(0);
        writer.ToString().ShouldContain("no pending changes");
        await _pendingChangeStore.DidNotReceive().ClearChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-item flow: phantom-dirty (dirty flag, no changes)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_Single_PhantomDirty_ClearsDirtyFlag()
    {
        var item = new WorkItemBuilder(42, "Phantom Dirty").Dirty().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>()).Returns((0, 0));

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(id: 42);

        result.ShouldBe(0);
        writer.ToString().ShouldContain("stale dirty flag");
        await _workItemRepo.Received().ClearDirtyFlagAsync(42, Arg.Any<CancellationToken>());
        await _promptStateWriter.Received().WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-item flow: confirmation accepted
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    public async Task Execute_Single_ConfirmAccepted_DiscardsChanges(string response)
    {
        var item = new WorkItemBuilder(42, "Dirty Item").Dirty().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>()).Returns((1, 0));
        _consoleInput.ReadLine().Returns(response);

        Console.SetOut(new StringWriter());

        var result = await _cmd.ExecuteAsync(id: 42);

        result.ShouldBe(0);
        await _pendingChangeStore.Received().ClearChangesAsync(42, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().ClearDirtyFlagAsync(42, Arg.Any<CancellationToken>());
        await _promptStateWriter.Received().WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-item flow: confirmation rejected
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("n")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Execute_Single_ConfirmRejected_DoesNotDiscard(string? response)
    {
        var item = new WorkItemBuilder(42, "Keep Me").Dirty().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>()).Returns((1, 0));
        _consoleInput.ReadLine().Returns(response);

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(id: 42);

        result.ShouldBe(0);
        writer.ToString().ShouldContain("cancelled");
        await _pendingChangeStore.DidNotReceive().ClearChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().ClearDirtyFlagAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _promptStateWriter.DidNotReceive().WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-item flow: --yes bypasses prompt
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_Single_YesFlag_SkipsPrompt()
    {
        var item = new WorkItemBuilder(42, "Skip Prompt").Dirty().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>()).Returns((1, 2));

        var result = await _cmd.ExecuteAsync(id: 42, yes: true);

        result.ShouldBe(0);
        _consoleInput.DidNotReceive().ReadLine();
        await _pendingChangeStore.Received().ClearChangesAsync(42, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().ClearDirtyFlagAsync(42, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-item flow: JSON output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_Single_JsonOutput_WritesStructuredJson()
    {
        var item = new WorkItemBuilder(42, "JSON Test").Dirty().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>()).Returns((3, 5));

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(id: 42, yes: true, outputFormat: "json");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("\"items\"");
        output.ShouldContain("\"id\": 42");
        output.ShouldContain("\"notes\": 3");
        output.ShouldContain("\"fieldEdits\": 5");
        output.ShouldContain("\"totalNotes\": 3");
        output.ShouldContain("\"totalFieldEdits\": 5");
        output.ShouldContain("\"totalItems\": 1");
    }

    // ═══════════════════════════════════════════════════════════════
    //  All-items flow: no dirty items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_All_NoDirtyItems_ReturnsNoOp()
    {
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem>());

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        writer.ToString().ShouldContain("No pending changes");
        await _workItemRepo.Received().ClearPhantomDirtyFlagsAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  All-items flow: seeds excluded
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_All_SeedsExcluded_OnlySeedsPresent_ReturnsNoOp()
    {
        var seed = new WorkItemBuilder(-1, "Only Seed").AsSeed().Dirty().Build();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem> { seed });

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        writer.ToString().ShouldContain("No pending changes");
        await _workItemRepo.Received().ClearPhantomDirtyFlagsAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  All-items flow: confirmation accepted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_All_ConfirmAccepted_DiscardsAll()
    {
        var item1 = new WorkItemBuilder(10, "Item A").Dirty().Build();
        var item2 = new WorkItemBuilder(20, "Item B").Dirty().Build();
        var seed = new WorkItemBuilder(-1, "Seed").AsSeed().Dirty().Build();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { item1, item2, seed });
        _pendingChangeStore.GetChangeSummaryAsync(10, Arg.Any<CancellationToken>()).Returns((1, 2));
        _pendingChangeStore.GetChangeSummaryAsync(20, Arg.Any<CancellationToken>()).Returns((0, 1));
        _consoleInput.ReadLine().Returns("y");

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        await _pendingChangeStore.Received().ClearAllChangesAsync(Arg.Any<CancellationToken>());
        // No per-item ClearDirtyFlagAsync — ClearPhantomDirtyFlagsAsync handles all non-seed dirty flags atomically
        await _workItemRepo.DidNotReceive().ClearDirtyFlagAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.Received().ClearPhantomDirtyFlagsAsync(Arg.Any<CancellationToken>());
        await _promptStateWriter.Received().WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  All-items flow: confirmation rejected
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_All_ConfirmRejected_DoesNotDiscard()
    {
        var item = new WorkItemBuilder(10, "Item A").Dirty().Build();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { item });
        _pendingChangeStore.GetChangeSummaryAsync(10, Arg.Any<CancellationToken>()).Returns((1, 0));
        _consoleInput.ReadLine().Returns("n");

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        writer.ToString().ShouldContain("cancelled");
        await _pendingChangeStore.DidNotReceive().ClearAllChangesAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  All-items flow: --yes bypasses prompt
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_All_YesFlag_SkipsPrompt()
    {
        var item = new WorkItemBuilder(10, "Item A").Dirty().Build();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { item });
        _pendingChangeStore.GetChangeSummaryAsync(10, Arg.Any<CancellationToken>()).Returns((2, 3));

        var result = await _cmd.ExecuteAsync(all: true, yes: true);

        result.ShouldBe(0);
        _consoleInput.DidNotReceive().ReadLine();
        await _pendingChangeStore.Received().ClearAllChangesAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  All-items flow: JSON output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_All_JsonOutput_WritesStructuredJson()
    {
        var item1 = new WorkItemBuilder(10, "Item A").Dirty().Build();
        var item2 = new WorkItemBuilder(20, "Item B").Dirty().Build();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { item1, item2 });
        _pendingChangeStore.GetChangeSummaryAsync(10, Arg.Any<CancellationToken>()).Returns((1, 2));
        _pendingChangeStore.GetChangeSummaryAsync(20, Arg.Any<CancellationToken>()).Returns((3, 0));

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(all: true, yes: true, outputFormat: "json");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("\"items\"");
        output.ShouldContain("\"id\": 10");
        output.ShouldContain("\"id\": 20");
        output.ShouldContain("\"totalNotes\": 4");
        output.ShouldContain("\"totalFieldEdits\": 2");
        output.ShouldContain("\"totalItems\": 2");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Telemetry
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_EmitsTelemetry_SingleItem()
    {
        var item = new WorkItemBuilder(42, "Tele Item").Dirty().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>()).Returns((1, 0));

        await _cmd.ExecuteAsync(id: 42, yes: true);

        _telemetryClient.Received().TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(d =>
                d["command"] == "discard" &&
                d["exit_code"] == "0" &&
                d["item_count"] == "1" &&
                d["used_all"] == "False"),
            Arg.Any<Dictionary<string, double>>());
    }

    [Fact]
    public async Task Execute_EmitsTelemetry_AllItems()
    {
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem>());

        await _cmd.ExecuteAsync(all: true);

        _telemetryClient.Received().TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(d =>
                d["command"] == "discard" &&
                d["item_count"] == "0" &&
                d["used_all"] == "True"),
            Arg.Any<Dictionary<string, double>>());
    }

    [Fact]
    public async Task Execute_EmitsTelemetry_AllItems_NonZeroCount()
    {
        var item1 = new WorkItemBuilder(10, "A").Dirty().Build();
        var item2 = new WorkItemBuilder(20, "B").Dirty().Build();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { item1, item2 });
        _pendingChangeStore.GetChangeSummaryAsync(10, Arg.Any<CancellationToken>()).Returns((1, 0));
        _pendingChangeStore.GetChangeSummaryAsync(20, Arg.Any<CancellationToken>()).Returns((0, 2));

        await _cmd.ExecuteAsync(all: true, yes: true);

        _telemetryClient.Received().TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(d =>
                d["command"] == "discard" &&
                d["exit_code"] == "0" &&
                d["item_count"] == "2" &&
                d["used_all"] == "True"),
            Arg.Any<Dictionary<string, double>>());
    }

    [Fact]
    public async Task Execute_EmitsTelemetry_OnError()
    {
        var (_, _) = await StderrCapture.RunAsync(
            () => _cmd.ExecuteAsync(id: null, all: false));

        _telemetryClient.Received().TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(d => d["exit_code"] == "1"),
            Arg.Any<Dictionary<string, double>>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  DD-9: Prompt state writer NOT called on error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_Error_NoPromptStateWrite()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var (_, _) = await StderrCapture.RunAsync(
            () => _cmd.ExecuteAsync(id: 999));

        await _promptStateWriter.DidNotReceive().WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single-item flow: not dirty but has pending changes (edge case)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_Single_NotDirtyButHasChanges_StillDiscards()
    {
        // Item not marked dirty but has pending changes (shouldn't normally happen, but handle gracefully)
        var item = new WorkItemBuilder(42, "Orphan Changes").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>()).Returns((0, 2));

        var result = await _cmd.ExecuteAsync(id: 42, yes: true);

        result.ShouldBe(0);
        await _pendingChangeStore.Received().ClearChangesAsync(42, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().ClearDirtyFlagAsync(42, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Human output formatting
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_Single_HumanOutput_ShowsSuccessMessage()
    {
        var item = new WorkItemBuilder(42, "Test Item").Dirty().Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangeSummaryAsync(42, Arg.Any<CancellationToken>()).Returns((2, 1));

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(id: 42, yes: true, outputFormat: "human");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("Discarded");
        output.ShouldContain("2 notes");
        output.ShouldContain("1 field edit");
        output.ShouldContain("#42");
    }

    [Fact]
    public async Task Execute_All_HumanOutput_ShowsAggregateSummary()
    {
        var item1 = new WorkItemBuilder(10, "A").Dirty().Build();
        var item2 = new WorkItemBuilder(20, "B").Dirty().Build();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItem> { item1, item2 });
        _pendingChangeStore.GetChangeSummaryAsync(10, Arg.Any<CancellationToken>()).Returns((1, 0));
        _pendingChangeStore.GetChangeSummaryAsync(20, Arg.Any<CancellationToken>()).Returns((0, 2));

        var writer = new StringWriter();
        Console.SetOut(writer);

        var result = await _cmd.ExecuteAsync(all: true, yes: true, outputFormat: "human");

        result.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("2 items");
        output.ShouldContain("1 note");
        output.ShouldContain("2 field edits");
    }
}
