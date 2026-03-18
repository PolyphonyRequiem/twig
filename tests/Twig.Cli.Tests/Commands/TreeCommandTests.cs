using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class TreeCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinator _syncCoordinator;

    public TreeCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _config = new TwigConfiguration();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);
    }

    // ── Depth flag behavior ─────────────────────────────────────────

    [Fact]
    public void Tree_DefaultDepth_Uses10()
    {
        // Verify default DisplayConfig.TreeDepth is 10
        new TwigConfiguration().Display.TreeDepth.ShouldBe(10);
    }

    [Fact]
    public async Task Tree_DepthFlag_LimitsChildren()
    {
        var active = CreateWorkItem(1, "Focus", parentId: null);
        var children = Enumerable.Range(2, 5)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = new TreeCommand(_contextStore, _workItemRepo, _config, _formatterFactory, _activeItemResolver, _workingSetService, _syncCoordinator);

        var captured = CaptureConsoleOut(() => cmd.ExecuteAsync("minimal", depth: 2).GetAwaiter().GetResult());

        captured.ShouldContain("... +3 more");
    }

    [Fact]
    public async Task Tree_AllFlag_ShowsAllChildren()
    {
        var active = CreateWorkItem(1, "Focus", parentId: null);
        var children = Enumerable.Range(2, 21)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = new TreeCommand(_contextStore, _workItemRepo, _config, _formatterFactory, _activeItemResolver, _workingSetService, _syncCoordinator);

        var captured = CaptureConsoleOut(() => cmd.ExecuteAsync("minimal", all: true).GetAwaiter().GetResult());

        // All 20 children should appear — no truncation
        for (var i = 2; i <= 21; i++)
            captured.ShouldContain($"#{i}");
        captured.ShouldNotContain("more");
    }

    [Fact]
    public async Task Tree_DepthFlagOverridesConfig()
    {
        _config.Display.TreeDepth = 100;

        var active = CreateWorkItem(1, "Focus", parentId: null);
        var children = Enumerable.Range(2, 5)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = new TreeCommand(_contextStore, _workItemRepo, _config, _formatterFactory, _activeItemResolver, _workingSetService, _syncCoordinator);

        var captured = CaptureConsoleOut(() => cmd.ExecuteAsync("minimal", depth: 2).GetAwaiter().GetResult());

        // --depth 2 overrides config TreeDepth=100, so only 2 shown + "... +3 more"
        captured.ShouldContain("... +3 more");
    }

    [Fact]
    public async Task Tree_AllFlagOverridesDepth()
    {
        var active = CreateWorkItem(1, "Focus", parentId: null);
        var children = Enumerable.Range(2, 6)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = new TreeCommand(_contextStore, _workItemRepo, _config, _formatterFactory, _activeItemResolver, _workingSetService, _syncCoordinator);

        var captured = CaptureConsoleOut(() => cmd.ExecuteAsync("minimal", depth: 1, all: true).GetAwaiter().GetResult());

        // --all overrides --depth 1, so all 5 children should appear
        for (var i = 2; i <= 6; i++)
            captured.ShouldContain($"#{i}");
        captured.ShouldNotContain("more");
    }

    // ── WS-021: JSON output parity ──────────────────────────────────

    [Fact]
    public async Task Tree_JsonOutput_NoSyncIndicators()
    {
        var active = CreateWorkItem(1, "JSON Tree Item", parentId: null);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = new TreeCommand(_contextStore, _workItemRepo, _config, _formatterFactory, _activeItemResolver, _workingSetService, _syncCoordinator);

        var captured = CaptureConsoleOut(() => cmd.ExecuteAsync("json").GetAwaiter().GetResult());

        // JSON output must not contain sync indicators
        captured.ShouldNotContain("syncing");
        captured.ShouldNotContain("Syncing");
        captured.ShouldNotContain("↻");
        // Must contain valid JSON
        captured.ShouldContain("\"id\":");
    }

    private static WorkItem CreateWorkItem(int id, string title, int? parentId)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    private static string CaptureConsoleOut(Action action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }
}
