using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests verifying the slimmed SetCommand outputs the correct confirmation format
/// for each output mode (human, json, jsonc, minimal) and does NOT emit hints.
/// Covers acceptance criteria from plan task T-2.5.
/// </summary>
public sealed class SetCommand_SlimTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public SetCommand_SlimTests()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;

        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
    }

    // ── Human format confirmation ────────────────────────────────

    [Fact]
    public async Task HumanFormat_OutputsSetActiveItemLine()
    {
        var item = new WorkItemBuilder(42, "Fix login bug").AsTask()
            .InState("Active").WithIterationPath("Project\\Sprint 1").Build();
        ArrangeItemInCache(item);

        var output = await ExecuteCapturingOutput("42", "human");

        output.ShouldContain("Set active item:");
        output.ShouldContain("#42");
        output.ShouldContain("Fix login bug");
        output.ShouldContain("Active");
    }

    // ── JSON format confirmation ─────────────────────────────────

    [Fact]
    public async Task JsonFormat_OutputsStructuredJson()
    {
        var item = new WorkItemBuilder(42, "Fix login bug").AsTask()
            .InState("Active").WithIterationPath("Project\\Sprint 1").Build();
        ArrangeItemInCache(item);

        var output = await ExecuteCapturingOutput("42", "json");

        output.ShouldContain("\"id\"");
        output.ShouldContain("42");
        output.ShouldContain("\"title\"");
        output.ShouldContain("Fix login bug");
        output.ShouldContain("\"state\"");
        output.ShouldContain("Active");
        output.ShouldContain("\"type\"");
        output.ShouldContain("Task");
    }

    // ── JSON Compact format confirmation ─────────────────────────

    [Fact]
    public async Task JsonCompactFormat_OutputsStructuredJson()
    {
        var item = new WorkItemBuilder(42, "Fix login bug").AsTask()
            .InState("Active").WithIterationPath("Project\\Sprint 1").Build();
        ArrangeItemInCache(item);

        var output = await ExecuteCapturingOutput("42", "json-compact");

        output.ShouldContain("\"id\"");
        output.ShouldContain("42");
        output.ShouldContain("\"title\"");
        output.ShouldContain("Fix login bug");
    }

    // ── Minimal format confirmation ──────────────────────────────

    [Fact]
    public async Task MinimalFormat_OutputsOnlyIdHash()
    {
        var item = new WorkItemBuilder(42, "Fix login bug").AsTask()
            .InState("Active").WithIterationPath("Project\\Sprint 1").Build();
        ArrangeItemInCache(item);

        var output = await ExecuteCapturingOutput("42", "minimal");

        output.Trim().ShouldBe("#42");
    }

    // ── No hints emitted ─────────────────────────────────────────

    [Fact]
    public async Task HumanFormat_DoesNotEmitHints()
    {
        var item = new WorkItemBuilder(42, "Fix login bug").AsTask()
            .InState("Active").WithParent(10).WithIterationPath("Project\\Sprint 1").Build();
        ArrangeItemInCache(item);

        // Enable hints — they should still NOT appear because SetCommand no longer calls HintEngine
        var output = await ExecuteCapturingOutput("42", "human", hintsEnabled: true);

        output.ShouldNotContain("Try:");
        output.ShouldNotContain("twig show");
        output.ShouldNotContain("twig tree");
        output.ShouldNotContain("Siblings:");
    }

    // ── Prompt state and history recorded ────────────────────────

    [Fact]
    public async Task WritesPromptState_WhenProvided()
    {
        var item = new WorkItemBuilder(42, "Test Item").AsTask()
            .WithIterationPath("Project\\Sprint 1").Build();
        ArrangeItemInCache(item);

        var promptWriter = Substitute.For<IPromptStateWriter>();
        var cmd = CreateCommand(promptStateWriter: promptWriter);

        var result = await ExecuteCapturing(cmd, "42");

        result.exitCode.ShouldBe(0);
        await promptWriter.Received(1).WritePromptStateAsync();
    }

    [Fact]
    public async Task RecordsHistory_WhenHistoryStoreProvided()
    {
        var item = new WorkItemBuilder(42, "Test Item").AsTask()
            .WithIterationPath("Project\\Sprint 1").Build();
        ArrangeItemInCache(item);

        var historyStore = Substitute.For<INavigationHistoryStore>();
        var cmd = CreateCommand(historyStore: historyStore);

        var result = await ExecuteCapturing(cmd, "42");

        result.exitCode.ShouldBe(0);
        await historyStore.Received(1).RecordVisitAsync(42, Arg.Any<CancellationToken>());
    }

    // ── No sync or child loading ─────────────────────────────────

    [Fact]
    public async Task DoesNotLoadChildrenParentsOrSync()
    {
        var item = new WorkItemBuilder(42, "Test Item").AsTask()
            .WithParent(10).WithIterationPath("Project\\Sprint 1").Build();
        ArrangeItemInCache(item);

        var cmd = CreateCommand();
        var result = await ExecuteCapturing(cmd, "42");

        result.exitCode.ShouldBe(0);
        await _adoService.DidNotReceive().FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private SetCommand CreateCommand(
        bool hintsEnabled = false,
        IPromptStateWriter? promptStateWriter = null,
        INavigationHistoryStore? historyStore = null)
    {
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = hintsEnabled });
        var pipelineFactory = new RenderingPipelineFactory(formatterFactory, null!, isOutputRedirected: () => true);
        var ctx = new CommandContext(pipelineFactory, formatterFactory, hintEngine, new TwigConfiguration());
        return new SetCommand(ctx, _workItemRepo, _contextStore, _activeItemResolver,
            promptStateWriter: promptStateWriter, historyStore: historyStore);
    }

    private async Task<string> ExecuteCapturingOutput(string idOrPattern, string outputFormat, bool hintsEnabled = false)
    {
        var cmd = CreateCommand(hintsEnabled: hintsEnabled);
        var (_, output) = await ExecuteCapturing(cmd, idOrPattern, outputFormat);
        return output;
    }

    private async Task<(int exitCode, string output)> ExecuteCapturing(SetCommand cmd, string idOrPattern, string outputFormat = "human")
    {
        using var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetError(new StringWriter());
        try
        {
            var exitCode = await cmd.ExecuteAsync(idOrPattern, outputFormat);
            return (exitCode, sw.ToString());
        }
        finally
        {
            Console.SetOut(_originalOut);
            Console.SetError(_originalErr);
        }
    }

    private void ArrangeItemInCache(WorkItem item)
    {
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }
}
