using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class QueryCommandTests
{
    private readonly IAdoWorkItemService _adoService;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly ITelemetryClient _telemetryClient;

    public QueryCommandTests()
    {
        _adoService = Substitute.For<IAdoWorkItemService>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _config = new TwigConfiguration { Organization = "https://dev.azure.com/org", Project = "MyProject" };
        _telemetryClient = Substitute.For<ITelemetryClient>();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    private QueryCommand CreateCommand(TextWriter? stderr = null) =>
        new(_adoService, _workItemRepo, _config, _formatterFactory, _hintEngine, _telemetryClient, stderr);

    private static IReadOnlyList<WorkItem> BuildItems(params (int Id, string Title, string State)[] specs) =>
        specs.Select(s => new WorkItemBuilder(s.Id, s.Title).InState(s.State).Build()).ToList();

    private void SetupAdoReturns(IReadOnlyList<int> ids, IReadOnlyList<WorkItem> items)
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ids);
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(items);
    }

    private static async Task<(int ExitCode, string Output)> CaptureOutput(Func<Task<int>> run)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try { return (await run(), writer.ToString()); }
        finally { Console.SetOut(original); }
    }

    // FR-01: Keyword search

    [Fact]
    public async Task ExecuteAsync_KeywordSearch_CallsQueryWithContainsInWiql()
    {
        var items = BuildItems((1, "MCP server integration", "Doing"));
        SetupAdoReturns([1], items);
        var cmd = CreateCommand();

        var (exitCode, output) = await CaptureOutput(() => cmd.ExecuteAsync(searchText: "MCP server"));

        exitCode.ShouldBe(0);

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => wiql.Contains("[System.Title] CONTAINS 'MCP server'")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());

        output.ShouldContain("MCP server integration");
        output.ShouldContain("Found 1 item(s)");
    }

    // FR-02–FR-06, FR-14: Combined filters with AND logic

    [Fact]
    public async Task ExecuteAsync_CombinedFilters_AllClausesPresent()
    {
        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync(
            type: "Issue",
            state: "Doing",
            assignedTo: "Daniel Green",
            areaPath: "MyProject\\Team1",
            iterationPath: "MyProject\\Sprint 1");

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql =>
                wiql.Contains("[System.WorkItemType] = 'Issue'") &&
                wiql.Contains("[System.State] = 'Doing'") &&
                wiql.Contains("[System.AssignedTo] = 'Daniel Green'") &&
                wiql.Contains("[System.AreaPath] UNDER 'MyProject\\Team1'") &&
                wiql.Contains("[System.IterationPath] UNDER 'MyProject\\Sprint 1'")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleFilters_GeneratesAndJoinedClauses()
    {
        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync(
            searchText: "MCP server",
            type: "Issue",
            assignedTo: "Daniel Green");

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql =>
                wiql.Contains("[System.Title] CONTAINS 'MCP server'") &&
                wiql.Contains(" AND [System.WorkItemType] = 'Issue'") &&
                wiql.Contains(" AND [System.AssignedTo] = 'Daniel Green'")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // FR-07, FR-08, FR-09: Time filters

    [Theory]
    [InlineData("2w", 14)]
    [InlineData("1m", 30)]
    public async Task ExecuteAsync_ChangedSince_ProducesCorrectDaysInWiql(string duration, int expectedDays)
    {
        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync(changedSince: duration);

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => wiql.Contains($"[System.ChangedDate] >= @Today - {expectedDays}")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithChangedSince7d_GeneratesAtTodayMinus7InWiql()
    {
        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync(changedSince: "7d");

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql =>
                wiql == "SELECT [System.Id] FROM WorkItems WHERE [System.ChangedDate] >= @Today - 7 ORDER BY [System.ChangedDate] DESC"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("3d", 3)]
    [InlineData("4w", 28)]
    [InlineData("2m", 60)]
    public async Task ExecuteAsync_CreatedSince_ProducesCorrectDaysInWiql(string duration, int expectedDays)
    {
        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync(createdSince: duration);

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => wiql.Contains($"[System.CreatedDate] >= @Today - {expectedDays}")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // FR-10, DD-01: $top passed to QueryByWiqlAsync

    [Fact]
    public async Task ExecuteAsync_TopParameter_PassedToAdoService()
    {
        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync(top: 5);

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Any<string>(),
            5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DefaultTop_Is25()
    {
        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync();

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Any<string>(),
            25,
            Arg.Any<CancellationToken>());
    }

    // FR-13, FR-19, DD-04: IDs output skips formatter and hints

    [Fact]
    public async Task ExecuteAsync_IdsOutput_PrintsOneIdPerLine()
    {
        var items = BuildItems(
            (1234, "Item A", "New"),
            (1235, "Item B", "Doing"),
            (1240, "Item C", "Done"));
        SetupAdoReturns([1234, 1235, 1240], items);
        var cmd = CreateCommand();

        var (result, output) = await CaptureOutput(() => cmd.ExecuteAsync(searchText: "test", outputFormat: "ids"));

        result.ShouldBe(0);
        output.Trim().ShouldBe("1234\n1235\n1240");
    }

    // FR-16: Results cached in SQLite

    [Fact]
    public async Task ExecuteAsync_ResultsCached_SaveBatchAsyncCalled()
    {
        var items = BuildItems((42, "Cached item", "New"));
        SetupAdoReturns([42], items);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync(searchText: "cached");

        await _workItemRepo.Received(1).SaveBatchAsync(
            Arg.Is<IEnumerable<WorkItem>>(batch => batch.Any(i => i.Id == 42)),
            Arg.Any<CancellationToken>());
    }

    // FR-17: Default area path from config

    [Fact]
    public async Task ExecuteAsync_DefaultAreaPathFromConfig_AppliedWhenNoFlag()
    {
        _config.Defaults = new DefaultsConfig
        {
            AreaPathEntries =
            [
                new AreaPathEntry { Path = "MyProject\\TeamA", IncludeChildren = true },
                new AreaPathEntry { Path = "MyProject\\TeamB", IncludeChildren = false }
            ]
        };

        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync();

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql =>
                wiql.Contains("[System.AreaPath] UNDER 'MyProject\\TeamA'") &&
                wiql.Contains("[System.AreaPath] = 'MyProject\\TeamB'")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitAreaPath_OverridesDefaults()
    {
        _config.Defaults = new DefaultsConfig
        {
            AreaPathEntries = [new AreaPathEntry { Path = "MyProject\\Default", IncludeChildren = true }]
        };

        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync(areaPath: "MyProject\\Explicit");

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql =>
                wiql.Contains("[System.AreaPath] UNDER 'MyProject\\Explicit'") &&
                !wiql.Contains("MyProject\\Default")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // FR-20: Invalid duration produces error and exit code 1

    [Theory]
    [InlineData("7x")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("d7")]
    [InlineData("7")]
    public async Task ExecuteAsync_InvalidChangedSince_ReturnsExitCode1(string invalidDuration)
    {
        var stderrWriter = new StringWriter();
        var cmd = CreateCommand(stderr: stderrWriter);

        var result = await cmd.ExecuteAsync(changedSince: invalidDuration);

        result.ShouldBe(1);
        stderrWriter.ToString().ShouldContain("error: Invalid duration");
        // No WIQL query should have been executed
        await _adoService.DidNotReceive().QueryByWiqlAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("10y")]
    public async Task ExecuteAsync_InvalidCreatedSince_ReturnsExitCode1(string invalidDuration)
    {
        var stderrWriter = new StringWriter();
        var cmd = CreateCommand(stderr: stderrWriter);

        var result = await cmd.ExecuteAsync(createdSince: invalidDuration);

        result.ShouldBe(1);
        stderrWriter.ToString().ShouldContain("error: Invalid duration");
        await _adoService.DidNotReceive().QueryByWiqlAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // FR-21, DD-10: No-filter query executes with defaults

    [Fact]
    public async Task ExecuteAsync_NoFilters_QueriesWithDefaultsAndReturnsExitCode0()
    {
        var items = BuildItems((100, "Recent item", "New"));
        SetupAdoReturns([100], items);
        var cmd = CreateCommand();

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql =>
                wiql.Contains("SELECT [System.Id] FROM WorkItems") &&
                wiql.Contains("ORDER BY [System.ChangedDate] DESC")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // NFR-03, NFR-05: Zero results — exit code 0, friendly message

    [Fact]
    public async Task ExecuteAsync_ZeroResults_ReturnsExitCode0()
    {
        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        var result = await cmd.ExecuteAsync(searchText: "nonexistent");

        result.ShouldBe(0);
        // SaveBatchAsync should not be called for empty results
        await _workItemRepo.DidNotReceive().SaveBatchAsync(
            Arg.Any<IEnumerable<WorkItem>>(), Arg.Any<CancellationToken>());
    }

    // NFR-06: Telemetry emitted

    [Fact]
    public async Task ExecuteAsync_Telemetry_EmittedWithCorrectProperties()
    {
        var items = BuildItems((1, "Test", "New"), (2, "Test 2", "Doing"));
        SetupAdoReturns([1, 2], items);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync(searchText: "test");

        _telemetryClient.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(p =>
                p["command"] == "query" &&
                p["exit_code"] == "0"),
            Arg.Is<Dictionary<string, double>>(m =>
                m.ContainsKey("duration_ms") &&
                m["result_count"] == 2));
    }

    [Fact]
    public async Task ExecuteAsync_InvalidDuration_TelemetryEmittedWithExitCode1()
    {
        var stderrWriter = new StringWriter();
        var cmd = CreateCommand(stderr: stderrWriter);

        await cmd.ExecuteAsync(changedSince: "bad");

        _telemetryClient.Received(1).TrackEvent(
            "CommandExecuted",
            Arg.Is<Dictionary<string, string>>(p =>
                p["command"] == "query" &&
                p["exit_code"] == "1"),
            Arg.Any<Dictionary<string, double>>());
    }

    // FR-15: ORDER BY ChangedDate DESC

    [Fact]
    public async Task ExecuteAsync_DefaultOrder_ChangedDateDesc()
    {
        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync(searchText: "test");

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => wiql.Contains("ORDER BY [System.ChangedDate] DESC")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // Default area path fallback: AreaPaths list

    [Fact]
    public async Task ExecuteAsync_DefaultAreaPathList_AppliedAsUnder()
    {
        _config.Defaults = new DefaultsConfig
        {
            AreaPaths = ["MyProject\\TeamX", "MyProject\\TeamY"]
        };

        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync();

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql =>
                wiql.Contains("[System.AreaPath] UNDER 'MyProject\\TeamX'") &&
                wiql.Contains("[System.AreaPath] UNDER 'MyProject\\TeamY'")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // Default area path fallback: single AreaPath

    [Fact]
    public async Task ExecuteAsync_DefaultSingleAreaPath_AppliedAsUnder()
    {
        _config.Defaults = new DefaultsConfig
        {
            AreaPath = "MyProject\\Solo"
        };

        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        await cmd.ExecuteAsync();

        await _adoService.Received(1).QueryByWiqlAsync(
            Arg.Is<string>(wiql => wiql.Contains("[System.AreaPath] UNDER 'MyProject\\Solo'")),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // JSON output format

    [Fact]
    public async Task ExecuteAsync_JsonOutput_ProducesValidJson()
    {
        var items = BuildItems((1, "Test item", "New"));
        SetupAdoReturns([1], items);
        var cmd = CreateCommand();

        var (result, output) = await CaptureOutput(() => cmd.ExecuteAsync(searchText: "test", outputFormat: "json"));

        result.ShouldBe(0);
        output.ShouldContain("\"count\": 1");
        output.ShouldContain("\"truncated\": false");
        output.ShouldContain("\"id\": 1");
    }

    // IDs output with zero results — no output

    [Fact]
    public async Task ExecuteAsync_IdsOutputZeroResults_NoOutput()
    {
        SetupAdoReturns([], []);
        var cmd = CreateCommand();

        var (result, output) = await CaptureOutput(() => cmd.ExecuteAsync(outputFormat: "ids"));

        result.ShouldBe(0);
        output.Trim().ShouldBeEmpty();
    }

    // DD-09: Truncation heuristic

    [Fact]
    public async Task ExecuteAsync_TruncationHeuristic_IndicatedWhenCountEqualsTop()
    {
        // Generate exactly 5 items to match top=5
        var items = BuildItems(
            (1, "A", "New"), (2, "B", "New"), (3, "C", "New"),
            (4, "D", "New"), (5, "E", "New"));
        SetupAdoReturns([1, 2, 3, 4, 5], items);
        var cmd = CreateCommand();

        var (result, output) = await CaptureOutput(() => cmd.ExecuteAsync(top: 5));

        result.ShouldBe(0);
        output.ShouldContain("results limited");
    }
}
