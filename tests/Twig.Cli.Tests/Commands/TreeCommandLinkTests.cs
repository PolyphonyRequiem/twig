using NSubstitute;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class TreeCommandLinkTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;

    public TreeCommandLinkTests()
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
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, linkRepo, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);
        _processTypeStore = Substitute.For<IProcessTypeStore>();

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
    }

    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    private TreeCommand CreateCommand(RenderingPipelineFactory? pipelineFactory = null) =>
        new(_contextStore, _workItemRepo, _config, _formatterFactory, _activeItemResolver,
            _workingSetService, _syncCoordinator, _processTypeStore, pipelineFactory);

    // ── HumanOutputFormatter Links section tests ────────────────────

    [Fact]
    public void FormatTree_WithLinks_ShowsLinksSection()
    {
        var focus = CreateWorkItem(1, "Focus Item");
        var links = new List<WorkItemLink>
        {
            new(1, 456, LinkTypes.Related),
            new(1, 123, LinkTypes.Predecessor),
            new(1, 789, LinkTypes.Successor),
        };

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(),
            siblingCounts: null, focusedItemLinks: links);

        var fmt = new HumanOutputFormatter();
        var output = fmt.FormatTree(tree, 10, 1);

        output.ShouldContain("Links");
        output.ShouldContain("Related: #456");
        output.ShouldContain("Predecessor: #123");
        output.ShouldContain("Successor: #789");
    }

    [Fact]
    public void FormatTree_WithoutLinks_OmitsLinksSection()
    {
        var focus = CreateWorkItem(1, "Focus Item");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var fmt = new HumanOutputFormatter();
        var output = fmt.FormatTree(tree, 10, 1);

        output.ShouldNotContain("Links");
        output.ShouldNotContain("╰──");
    }

    [Fact]
    public void FormatTree_LinksUsesBoxDrawing()
    {
        var focus = CreateWorkItem(1, "Focus Item");
        var links = new List<WorkItemLink>
        {
            new(1, 100, LinkTypes.Related),
            new(1, 200, LinkTypes.Successor),
        };

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(),
            siblingCounts: null, focusedItemLinks: links);

        var fmt = new HumanOutputFormatter();
        var output = fmt.FormatTree(tree, 10, 1);

        output.ShouldContain("┊");
        output.ShouldContain("╰── Links");
        output.ShouldContain("├──");
        output.ShouldContain("└──");
    }

    // ── JSON output with links ──────────────────────────────────────

    [Fact]
    public void JsonFormatTree_WithLinks_IncludesLinksArray()
    {
        var focus = CreateWorkItem(1, "Focus Item");
        var links = new List<WorkItemLink>
        {
            new(1, 456, LinkTypes.Related),
        };

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(),
            siblingCounts: null, focusedItemLinks: links);

        var fmt = new JsonOutputFormatter();
        var json = fmt.FormatTree(tree, 10, 1);

        json.ShouldContain("\"links\"");
        json.ShouldContain("\"targetId\": 456");
        json.ShouldContain("\"linkType\": \"Related\"");
        json.ShouldContain("\"sourceId\": 1");
    }

    [Fact]
    public void JsonFormatTree_WithoutLinks_IncludesEmptyLinksArray()
    {
        var focus = CreateWorkItem(1, "Focus Item");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var fmt = new JsonOutputFormatter();
        var json = fmt.FormatTree(tree, 10, 1);

        json.ShouldContain("\"links\": []");
    }

    // ── End-to-end: TreeCommand sync path with links ────────────────

    [Fact]
    public async Task SyncPath_SyncLinksAsync_CalledAndLinksPassedToTree()
    {
        var focus = CreateWorkItem(1, "Focus Item");
        var links = new List<WorkItemLink>
        {
            new(1, 456, LinkTypes.Related),
        };

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _adoService.FetchWithLinksAsync(1, Arg.Any<CancellationToken>())
            .Returns((focus, (IReadOnlyList<WorkItemLink>)links));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("json");

        result.ShouldBe(0);
        await _adoService.Received(1).FetchWithLinksAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncPath_SyncLinksAsync_Failure_TreeStillRenders()
    {
        var focus = CreateWorkItem(1, "Focus Item");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _adoService.FetchWithLinksAsync(1, Arg.Any<CancellationToken>())
            .Returns<(WorkItem, IReadOnlyList<WorkItemLink>)>(
                _ => throw new HttpRequestException("Network error"));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("json");

        result.ShouldBe(0); // Command succeeds despite link sync failure
    }

    // ── End-to-end: TreeCommand async path with links ───────────────

    [Fact]
    public async Task AsyncPath_LinksRenderedInSpectreTree()
    {
        var focus = CreateWorkItem(1, "Focus Item");
        var links = new List<WorkItemLink>
        {
            new(1, 456, LinkTypes.Related),
            new(1, 789, LinkTypes.Successor),
        };

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _adoService.FetchWithLinksAsync(1, Arg.Any<CancellationToken>())
            .Returns((focus, (IReadOnlyList<WorkItemLink>)links));

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Links");
        output.ShouldContain("Related: #456");
        output.ShouldContain("Successor: #789");
    }

    [Fact]
    public async Task AsyncPath_NoLinks_OmitsLinksSection()
    {
        var focus = CreateWorkItem(1, "Focus Item");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _adoService.FetchWithLinksAsync(1, Arg.Any<CancellationToken>())
            .Returns((focus, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldNotContain("Links");
    }

    [Fact]
    public async Task AsyncPath_LinkSyncFailure_TreeStillRenders()
    {
        var focus = CreateWorkItem(1, "Focus Item");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _adoService.FetchWithLinksAsync(1, Arg.Any<CancellationToken>())
            .Returns<(WorkItem, IReadOnlyList<WorkItemLink>)>(
                _ => throw new HttpRequestException("Network error"));

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        _testConsole.Output.ShouldContain("Focus Item");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, int? parentId = null, string type = "Task")
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Parse(type).Value,
            Title = title,
            State = "New",
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
