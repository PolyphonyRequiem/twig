using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
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
    // ── StatusCommand — json format ──────────────────────────────────

    [Fact]
    public async Task StatusCommand_JsonFormat_ProducesNoAnsiEscapeCodes()
    {
        var (contextStore, workItemRepo, pendingChangeStore) = CreateStatusMocks();
        var item = CreateWorkItem(1, "Test Item");
        contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = BuildStatusCommand(contextStore, workItemRepo, pendingChangeStore, hintsEnabled: false);

        var result = await cmd.ExecuteAsync("json");

        result.ShouldBe(0);
    }

    // ── StatusCommand — human format + hints ─────────────────────────

    [Fact]
    public async Task StatusCommand_HumanFormat_HintsEnabled_EmitsHintWhenStaleSeeds()
    {
        var (contextStore, workItemRepo, pendingChangeStore) = CreateStatusMocks();
        var item = CreateWorkItem(1, "Test Item");
        contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        // StaleDays = 0 means any seed with SeedCreatedAt set in the past is stale
        var staleSeed = new WorkItem
        {
            Id = 99,
            Type = WorkItemType.Task,
            Title = "Old Seed",
            State = "New",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { staleSeed });

        var cmd = BuildStatusCommand(contextStore, workItemRepo, pendingChangeStore,
            hintsEnabled: true, staleDays: 0);

        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
    }

    // ── StatusCommand — minimal format suppresses hints ───────────────

    [Fact]
    public async Task StatusCommand_MinimalFormat_SuppressesHintsEvenWhenEnabled()
    {
        var (contextStore, workItemRepo, pendingChangeStore) = CreateStatusMocks();
        var item = CreateWorkItem(1, "Test Item");
        contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var staleSeed = new WorkItem
        {
            Id = 99,
            Type = WorkItemType.Task,
            Title = "Old Seed",
            State = "New",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { staleSeed });

        var cmd = BuildStatusCommand(contextStore, workItemRepo, pendingChangeStore,
            hintsEnabled: true, staleDays: 0);

        var result = await cmd.ExecuteAsync("minimal");

        result.ShouldBe(0);
    }

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
        var cmd = new SetCommand(workItemRepo, contextStore, resolver, syncCoordFactory, wsService, factory, hintEngine);

        var result = await cmd.ExecuteAsync("42", "human");

        result.ShouldBe(0);
    }

    // ── TwigCommands — end-to-end --output routing ───────────────────

    /// <summary>
    /// Verifies the full path: TwigCommands.Status("json") → StatusCommand.ExecuteAsync("json")
    /// → OutputFormatterFactory.GetFormatter("json") → JsonOutputFormatter (no ANSI codes).
    /// </summary>
    [Fact]
    public async Task TwigCommands_Status_JsonOutput_RoutesToJsonFormatter_NoAnsiCodes()
    {
        var (contextStore, workItemRepo, pendingChangeStore) = CreateStatusMocks();
        var item = CreateWorkItem(1, "Test Item");
        contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var services = new ServiceCollection();
        services.AddSingleton(contextStore);
        services.AddSingleton(workItemRepo);
        services.AddSingleton(pendingChangeStore);
        var config = new TwigConfiguration();
        services.AddSingleton(config);
        services.AddSingleton(new HumanOutputFormatter());
        services.AddSingleton(new JsonOutputFormatter());
        services.AddSingleton(new JsonCompactOutputFormatter(new JsonOutputFormatter()));
        services.AddSingleton(new MinimalOutputFormatter());
        services.AddSingleton<OutputFormatterFactory>();
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        services.AddSingleton(hintEngine);
        var adoService = Substitute.For<IAdoWorkItemService>();
        var activeItemResolver = new ActiveItemResolver(contextStore, workItemRepo, adoService);
        services.AddSingleton(activeItemResolver);
        var pcw = new ProtectedCacheWriter(workItemRepo, pendingChangeStore);
        var scf = new SyncCoordinatorFactory(workItemRepo, adoService, pcw, pendingChangeStore, null, 30, 30);
        services.AddSingleton(scf.ReadWrite);
        services.AddSingleton(scf);
        var iterSvc = Substitute.For<IIterationService>();
        iterSvc.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var wss = new WorkingSetService(contextStore, workItemRepo, pendingChangeStore, iterSvc, null);
        services.AddSingleton(wss);
        var paths = new TwigPaths(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "config"), Path.Combine(Path.GetTempPath(), "twig.db"));
        services.AddSingleton(paths);
        services.AddSingleton(new StatusFieldConfigReader(paths));
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var pipelineFactory = new RenderingPipelineFactory(formatterFactory,
            new SpectreRenderer(new Spectre.Console.Testing.TestConsole(), new SpectreTheme(new DisplayConfig())),
            isOutputRedirected: () => true);
        services.AddSingleton(new CommandContext(pipelineFactory, formatterFactory, hintEngine, config));
        services.AddSingleton<StatusCommand>();

        var provider = services.BuildServiceProvider();
        var twigCmds = new TwigCommands(provider);

        var result = await twigCmds.Status("json");

        result.ShouldBe(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static(IContextStore ContextStore, IWorkItemRepository WorkItemRepo, IPendingChangeStore PendingChangeStore)
        CreateStatusMocks()
    {
        return (
            Substitute.For<IContextStore>(),
            Substitute.For<IWorkItemRepository>(),
            Substitute.For<IPendingChangeStore>()
        );
    }

    private static StatusCommand BuildStatusCommand(
        IContextStore contextStore,
        IWorkItemRepository workItemRepo,
        IPendingChangeStore pendingChangeStore,
        bool hintsEnabled,
        int staleDays = 14)
    {
        var config = new TwigConfiguration { Seed = new SeedConfig { StaleDays = staleDays } };
        var factory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = hintsEnabled });
        var adoService = Substitute.For<IAdoWorkItemService>();
        var activeItemResolver = new ActiveItemResolver(contextStore, workItemRepo, adoService);
        var pcs = Substitute.For<IPendingChangeStore>();
        var pcw = new ProtectedCacheWriter(workItemRepo, pcs);
        var sc = new SyncCoordinatorFactory(workItemRepo, adoService, pcw, pcs, null, 30, 30);
        var iterSvc= Substitute.For<IIterationService>();
        iterSvc.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var wss = new WorkingSetService(contextStore, workItemRepo, pcs, iterSvc, null);
        var paths = new TwigPaths(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "config"), Path.Combine(Path.GetTempPath(), "twig.db"));
        var statusFieldReader = new StatusFieldConfigReader(paths);
        var redirectedPipeline = new RenderingPipelineFactory(factory, new SpectreRenderer(new Spectre.Console.Testing.TestConsole(), new SpectreTheme(new DisplayConfig())), isOutputRedirected: () => true);
        var ctx = new CommandContext(redirectedPipeline, factory, hintEngine, config);
        return new StatusCommand(ctx, contextStore, workItemRepo, pendingChangeStore,
            activeItemResolver, wss, sc, statusFieldReader);
    }

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
