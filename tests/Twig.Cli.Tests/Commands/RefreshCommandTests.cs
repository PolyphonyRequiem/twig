using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class RefreshCommandTests : IDisposable
{
    private readonly string _testDir;
    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IIterationService _iterationService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly RefreshCommand _cmd;

    public RefreshCommandTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-refresh-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var twigDir = Path.Combine(_testDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var configPath = Path.Combine(twigDir, "config");
        var dbPath = Path.Combine(twigDir, "twig.db");

        _config = new TwigConfiguration { Organization = "https://dev.azure.com/org", Project = "MyProject" };
        _paths = new TwigPaths(twigDir, configPath, dbPath);
        _processTypeStore = Substitute.For<IProcessTypeStore>();

        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _iterationService = Substitute.For<IIterationService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();

        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _iterationService.GetWorkItemTypeAppearancesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeAppearance>
            {
                new("Bug", "CC293D", "icon_insect"),
                new("Task", "F2CB1D", "icon_clipboard"),
            });
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeWithStates>
            {
                new() { Name = "Bug", Color = "CC293D", IconId = "icon_insect", States = [] },
                new() { Name = "Task", Color = "F2CB1D", IconId = "icon_clipboard", States = [] },
            });
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData());

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        _cmd = new RefreshCommand(_contextStore, _workItemRepo, _adoService, _iterationService,
            _pendingChangeStore, _config, _paths, _processTypeStore, formatterFactory, hintEngine);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public async Task Refresh_NoItems_ReturnsSuccess()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Refresh_FetchesAndCachesItems()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });

        var item1 = CreateWorkItem(1, "Item 1");
        var item2 = CreateWorkItem(2, "Item 2");
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item1, item2 });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).FetchBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 2 && ids[0] == 1 && ids[1] == 2),
            Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveBatchAsync(Arg.Any<IReadOnlyList<WorkItem>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_SkipsNegativeIds()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1, -1 });

        var item = CreateWorkItem(1, "Real Item");
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).FetchBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 1 && ids[0] == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_RefreshesActiveItem()
    {
        var item1 = CreateWorkItem(1, "Sprint Item");
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item1 });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        var active = CreateWorkItem(42, "Active Item");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(active);
        _adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_UpdatesTypeAppearances()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);

        // Verify config in-memory update
        _config.TypeAppearances.ShouldNotBeNull();
        _config.TypeAppearances.Count.ShouldBe(2);
        _config.TypeAppearances.ShouldContain(a => a.Name == "Bug" && a.Color == "CC293D");
        _config.TypeAppearances.ShouldContain(a => a.Name == "Task" && a.Color == "F2CB1D");

        // Verify config was persisted to disk
        File.Exists(_paths.ConfigPath).ShouldBeTrue();
        var content = await File.ReadAllTextAsync(_paths.ConfigPath);
        content.ShouldContain("typeAppearances");
        content.ShouldContain("CC293D");

        // Verify SQLite process_types rows were saved via the mock
        await _processTypeStore.Received(2).SaveAsync(Arg.Any<ProcessTypeRecord>(), Arg.Any<CancellationToken>());
        await _processTypeStore.Received().SaveAsync(
            Arg.Is<ProcessTypeRecord>(r => r.TypeName == "Bug" && r.ColorHex == "CC293D" && r.IconId == "icon_insect"),
            Arg.Any<CancellationToken>());
        await _processTypeStore.Received().SaveAsync(
            Arg.Is<ProcessTypeRecord>(r => r.TypeName == "Task" && r.ColorHex == "F2CB1D" && r.IconId == "icon_clipboard"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithAreaPathEntries_UsesCorrectWiqlOperators()
    {
        _config.Defaults.AreaPathEntries = new List<AreaPathEntry>
        {
            new() { Path = "MyProject\\TeamA", IncludeChildren = true },
            new() { Path = "MyProject\\TeamB", IncludeChildren = false }
        };
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql =>
                wiql.Contains("[System.AreaPath] UNDER 'MyProject\\TeamA'") &&
                wiql.Contains("[System.AreaPath] = 'MyProject\\TeamB'") &&
                wiql.Contains(" OR ")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithSingleAreaPath_AddsAreaPathFilterToWiql()
    {
        _config.Defaults.AreaPath = "MyProject\\SingleTeam";
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => wiql.Contains("[System.AreaPath] UNDER 'MyProject\\SingleTeam'")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithoutAreaPaths_NoAreaPathFilterInWiql()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => !wiql.Contains("AreaPath")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_UsesBatchFetchInsteadOfSerial()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2, 3 });
        var items = new[] { CreateWorkItem(1, "A"), CreateWorkItem(2, "B"), CreateWorkItem(3, "C") };
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(items);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        // FetchBatchAsync should be called once with all IDs (not serial FetchAsync for each)
        await _adoService.Received(1).FetchBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 1, 2, 3 })),
            Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveBatchAsync(Arg.Any<IReadOnlyList<WorkItem>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithAreaPath_ContainingQuote_EscapesInWiql()
    {
        _config.Defaults.AreaPathEntries = new List<AreaPathEntry>
        {
            new() { Path = "My'Project\\Team", IncludeChildren = true }
        };
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => wiql.Contains("[System.AreaPath] UNDER 'My''Project\\Team'")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WithAreaPath_ContainingQuote_FallbackEscapesInWiql()
    {
        // Test escaping through the legacy AreaPaths fallback path
        _config.Defaults.AreaPaths = new List<string> { "My'Project\\Team" };
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => wiql.Contains("[System.AreaPath] UNDER 'My''Project\\Team'")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_IncludeChildrenFalse_UsesEqualsOperator()
    {
        _config.Defaults.AreaPathEntries = new List<AreaPathEntry>
        {
            new() { Path = "MyProject\\ExactTeam", IncludeChildren = false }
        };
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql =>
                wiql.Contains("[System.AreaPath] = 'MyProject\\ExactTeam'") &&
                !wiql.Contains("UNDER")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_ActiveItemInBatchResults_SkipsDuplicateFetch()
    {
        // Active item ID 2 is already in WIQL results
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2, 3 });
        var items = new[] { CreateWorkItem(1, "A"), CreateWorkItem(2, "B"), CreateWorkItem(3, "C") };
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(items);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(2);
        _adoService.FetchChildrenAsync(2, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        // FetchAsync should NOT be called for activeId=2 since it was in the batch
        await _adoService.DidNotReceive().FetchAsync(2, Arg.Any<CancellationToken>());
        // FetchChildrenAsync SHOULD still be called — children may be outside sprint scope
        await _adoService.Received(1).FetchChildrenAsync(2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_HydratesAncestors_WhenOrphanParentIdsExist()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 10 });
        var item10 = CreateWorkItem(10, "Child Task");
        _adoService.FetchBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(10)),
            Arg.Any<CancellationToken>())
            .Returns(new[] { item10 });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // First call returns orphan parent IDs, second call returns empty (all resolved)
        var callCount = 0;
        _workItemRepo.GetOrphanParentIdsAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult<IReadOnlyList<int>>(new[] { 5 })
                    : Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
            });

        var parent5 = CreateWorkItem(5, "Parent Feature");
        _adoService.FetchBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(5)),
            Arg.Any<CancellationToken>())
            .Returns(new[] { parent5 });

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        // Verify the orphan parent was fetched and saved
        await _workItemRepo.Received().GetOrphanParentIdsAsync(Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 5)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_AncestorHydration_CapsAt5Levels()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // Always return orphan IDs — should stop after 5 iterations
        _workItemRepo.GetOrphanParentIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<int>>(new[] { 999 }));
        _adoService.FetchBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(999)),
            Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(999, "Phantom") });

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        // Exactly 5 iterations of orphan hydration
        await _workItemRepo.Received(5).GetOrphanParentIdsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_TypeStateSequencesFetchException_StderrOutputGoesViaFormatter()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // Override the default to throw on type-states fetch
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WorkItemTypeWithStates>>(_ => throw new InvalidOperationException("network error"));

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var cmd = new RefreshCommand(_contextStore, _workItemRepo, _adoService, _iterationService,
            _pendingChangeStore, _config, _paths, _processTypeStore, formatterFactory, hintEngine);

        var originalErr = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            await cmd.ExecuteAsync("json");
        }
        finally
        {
            Console.SetError(originalErr);
        }

        var stderrOutput = sw.ToString();
        stderrOutput.ShouldNotContain("\x1b[");
        stderrOutput.ShouldContain("Could not fetch type");
    }

    [Fact]
    public async Task Refresh_ProcessConfigFetchException_StderrOutputGoesViaFormatter()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // Override the default to throw on process config fetch
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns<ProcessConfigurationData>(_ => throw new InvalidOperationException("service unavailable"));

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var cmd = new RefreshCommand(_contextStore, _workItemRepo, _adoService, _iterationService,
            _pendingChangeStore, _config, _paths, _processTypeStore, formatterFactory, hintEngine);

        var originalErr = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            await cmd.ExecuteAsync("json");
        }
        finally
        {
            Console.SetError(originalErr);
        }

        var stderrOutput = sw.ToString();
        stderrOutput.ShouldNotContain("\x1b[");
        stderrOutput.ShouldContain("Could not fetch type data");
    }

    [Fact]
    public async Task Refresh_DoesNotOverwriteDisplayTypeColors()
    {
        // Arrange: set custom user-specified Display.TypeColors before refresh
        _config.Display.TypeColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bug"] = "FF0000",
            ["CustomType"] = "00FF00",
        };

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // Act
        var result = await _cmd.ExecuteAsync();

        // Assert: Display.TypeColors is unchanged after refresh
        result.ShouldBe(0);
        _config.Display.TypeColors.ShouldNotBeNull();
        _config.Display.TypeColors.Count.ShouldBe(2);
        _config.Display.TypeColors.ShouldContainKeyAndValue("Bug", "FF0000");
        _config.Display.TypeColors.ShouldContainKeyAndValue("CustomType", "00FF00");
    }

    [Fact]
    public async Task Refresh_UpdatesLastRefreshedAtTimestamp()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
        // Verify last_refreshed_at was persisted after refresh
        await _contextStore.Received(1).SetValueAsync(
            "last_refreshed_at", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static WorkItem CreateWorkItem(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
