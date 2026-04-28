using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests verifying SetCommand's slimmed context-switch-only behavior.
/// After the slim-down, SetCommand no longer extends the working set or syncs —
/// these tests confirm the command remains a fast context mutation.
/// </summary>
public sealed class SetCommand_ContextChangeTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly CommandContext _ctx;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalErr;

    public SetCommand_ContextChangeTests()
    {
        _originalOut = Console.Out;
        _originalErr = Console.Error;
        Console.SetOut(new StringWriter());
        Console.SetError(new StringWriter());

        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();

        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var pipelineFactory = new RenderingPipelineFactory(formatterFactory, null!, isOutputRedirected: () => true);
        _ctx = new CommandContext(pipelineFactory, formatterFactory, hintEngine, new TwigConfiguration());
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalErr);
    }

    [Fact]
    public async Task Set_SetsContextWithoutSyncing()
    {
        var item = new WorkItemBuilder(100, "Parent Story")
            .AsUserStory().WithIterationPath("Project\\Sprint 1").Build();

        ArrangeItemInCache(item);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("100");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(100, Arg.Any<CancellationToken>());
        // No child fetching — slimmed set doesn't extend working set
        await _adoService.DidNotReceive().FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_EmitsConfirmationLine()
    {
        var item = new WorkItemBuilder(100, "Test Item")
            .AsTask().WithIterationPath("Project\\Sprint 1").Build();

        ArrangeItemInCache(item);

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var cmd = CreateCommand();
            var result = await cmd.ExecuteAsync("100");
            result.ShouldBe(0);

            var output = sw.ToString();
            output.ShouldContain("#100");
            output.ShouldContain("Test Item");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Set_DoesNotFetchChildrenOrSync()
    {
        var item = new WorkItemBuilder(100, "Test Item")
            .AsTask().WithIterationPath("Project\\Sprint 1").Build();

        ArrangeItemInCache(item);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("100");

        result.ShouldBe(0);
        // No child fetching or sync operations
        await _adoService.DidNotReceive().FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private SetCommand CreateCommand()
    {
        return new SetCommand(_ctx, _workItemRepo, _contextStore, _activeItemResolver);
    }

    private void ArrangeItemInCache(WorkItem item)
    {
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
    }
}
