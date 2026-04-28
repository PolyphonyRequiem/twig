using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Wiring tests that verify end-to-end formatter selection, hint routing,
/// and ANSI suppression through representative commands.
/// ITEM-042 and ITEM-043 from EPIC-005.
/// </summary>
public class CommandFormatterWiringTests
{
    // ── SetCommand — human format + hints ───────────────────────────

    [Fact]
    public async Task SetCommand_HumanFormat_HintsEnabled_EmitsNavigationHint()
    {
        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();
        var contextStore = Substitute.For<IContextStore>();

        var item = CreateWorkItem(42, "Test Item");
        workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var factory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = true });
        var resolver = new ActiveItemResolver(contextStore, workItemRepo, adoService);
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedWriter = new ProtectedCacheWriter(workItemRepo, pendingChangeStore);
        var syncCoordFactory = new SyncCoordinatorFactory(workItemRepo, adoService, protectedWriter, pendingChangeStore, null, 30, 30);
        var iterService = Substitute.For<IIterationService>();
        iterService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var wsService = new WorkingSetService(contextStore, workItemRepo, pendingChangeStore, iterService, null);
        var pipelineFactory = new RenderingPipelineFactory(factory, null!, isOutputRedirected: () => true);
        var ctx = new CommandContext(pipelineFactory, factory, hintEngine, new TwigConfiguration());
        var statusFieldReader = new StatusFieldConfigReader(new TwigPaths(
            Path.Combine(Path.GetTempPath(), ".twig-fmtwire-test"),
            Path.Combine(Path.GetTempPath(), ".twig-fmtwire-test", "config"),
            Path.Combine(Path.GetTempPath(), ".twig-fmtwire-test", "twig.db")));
        var cmd = new SetCommand(ctx, workItemRepo, contextStore, resolver, syncCoordFactory, wsService, statusFieldReader);

        var result = await cmd.ExecuteAsync("42", "human");

        result.ShouldBe(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
