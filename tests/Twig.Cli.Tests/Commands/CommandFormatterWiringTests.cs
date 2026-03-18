using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
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

        var output = await CaptureStdout(() => cmd.ExecuteAsync("json"));

        output.ShouldNotContain("\x1b[");
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

        var output = await CaptureStdout(() => cmd.ExecuteAsync("human"));

        output.ShouldContain("hint:");
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

        var output = await CaptureStdout(() => cmd.ExecuteAsync("minimal"));

        output.ShouldNotContain("hint:");
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
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = true });
        var resolver = new ActiveItemResolver(contextStore, workItemRepo, adoService);
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedWriter = new ProtectedCacheWriter(workItemRepo, pendingChangeStore);
        var syncCoord = new SyncCoordinator(workItemRepo, adoService, protectedWriter, 30);
        var iterService = Substitute.For<IIterationService>();
        iterService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var wsService = new WorkingSetService(contextStore, workItemRepo, pendingChangeStore, iterService, null);
        var cmd = new SetCommand(workItemRepo, contextStore, resolver, syncCoord, wsService, factory, hintEngine);

        var output = await CaptureStdout(() => cmd.ExecuteAsync("42", "human"));

        output.ShouldContain("hint:");
        output.ShouldContain("twig status");
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
        services.AddSingleton(new TwigConfiguration());
        services.AddSingleton(new HumanOutputFormatter());
        services.AddSingleton(new JsonOutputFormatter());
        services.AddSingleton(new MinimalOutputFormatter());
        services.AddSingleton<OutputFormatterFactory>();
        services.AddSingleton(new HintEngine(new DisplayConfig { Hints = false }));
        var adoService = Substitute.For<IAdoWorkItemService>();
        services.AddSingleton(new ActiveItemResolver(contextStore, workItemRepo, adoService));
        var pcw = new ProtectedCacheWriter(workItemRepo, pendingChangeStore);
        services.AddSingleton(new SyncCoordinator(workItemRepo, adoService, pcw, 30));
        var iterSvc = Substitute.For<IIterationService>();
        iterSvc.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        services.AddSingleton(new WorkingSetService(contextStore, workItemRepo, pendingChangeStore, iterSvc, null));
        services.AddSingleton<StatusCommand>();

        var provider = services.BuildServiceProvider();
        var twigCmds = new TwigCommands(provider);

        var output = await CaptureStdout(() => twigCmds.Status("json"));

        output.ShouldNotContain("\x1b[");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Captures stdout for the duration of the action, then restores the original writer.
    /// </summary>
    private static async Task<string> CaptureStdout(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return sw.ToString();
    }

    private static (IContextStore ContextStore, IWorkItemRepository WorkItemRepo, IPendingChangeStore PendingChangeStore)
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
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = hintsEnabled });
        var adoService = Substitute.For<IAdoWorkItemService>();
        var activeItemResolver = new ActiveItemResolver(contextStore, workItemRepo, adoService);
        var pcs = Substitute.For<IPendingChangeStore>();
        var pcw = new ProtectedCacheWriter(workItemRepo, pcs);
        var sc = new SyncCoordinator(workItemRepo, adoService, pcw, 30);
        var iterSvc = Substitute.For<IIterationService>();
        iterSvc.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var wss = new WorkingSetService(contextStore, workItemRepo, pcs, iterSvc, null);
        return new StatusCommand(contextStore, workItemRepo, pendingChangeStore, config, factory, hintEngine,
            activeItemResolver, wss, sc);
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
