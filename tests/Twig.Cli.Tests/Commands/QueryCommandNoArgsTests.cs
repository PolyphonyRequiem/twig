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

/// <summary>
/// Tests for <c>twig query</c> no-args detection and summary rendering (#1639).
/// Verifies that invoking the command with no filter arguments shows a helpful
/// summary instead of executing a broad WIQL query.
/// </summary>
public sealed class QueryCommandNoArgsTests
{
    private readonly IAdoWorkItemService _adoService;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly ITelemetryClient _telemetryClient;

    public QueryCommandNoArgsTests()
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

    private QueryCommand CreateCommand(TwigConfiguration? configOverride = null) =>
        new(_adoService, _workItemRepo, configOverride ?? _config, _formatterFactory, _hintEngine, _telemetryClient);

    private static async Task<(int ExitCode, string Output)> CaptureOutput(Func<Task<int>> run)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try { return (await run(), writer.ToString()); }
        finally { Console.SetOut(original); }
    }

    // --- T-1639.1: No-args detection ---

    [Fact]
    public async Task NoArgs_ShowsSummary_ExitCode0()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureOutput(() => cmd.ExecuteAsync());

        exitCode.ShouldBe(0);
        output.ShouldContain("twig query — Search and filter work items");
        output.ShouldContain("Usage:");
        output.ShouldContain("Available filters:");
        output.ShouldContain("Examples:");
    }

    [Fact]
    public async Task NoArgs_DoesNotCallAdo()
    {
        var cmd = CreateCommand();
        await CaptureOutput(() => cmd.ExecuteAsync());

        await _adoService.DidNotReceive()
            .QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive()
            .FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoArgs_TopAlone_ShowsSummary()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureOutput(() => cmd.ExecuteAsync(top: 50));

        exitCode.ShouldBe(0);
        output.ShouldContain("twig query — Search and filter work items");

        // --top alone should not trigger ADO call
        await _adoService.DidNotReceive()
            .QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // --- T-1639.2: Output format handling ---

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    [InlineData("json-full")]
    public async Task NoArgs_OutputJsonVariants_ShowsSummaryText(string outputFormat)
    {
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureOutput(() => cmd.ExecuteAsync(outputFormat: outputFormat));

        exitCode.ShouldBe(0);
        output.ShouldContain("twig query — Search and filter work items");
        output.ShouldContain("Available filters:");
    }

    [Fact]
    public async Task NoArgs_OutputIds_ProducesEmptyOutput()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureOutput(() => cmd.ExecuteAsync(outputFormat: "ids"));

        exitCode.ShouldBe(0);
        output.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoArgs_OutputMinimal_ProducesNoOutput()
    {
        var cmd = CreateCommand();
        var (exitCode, output) = await CaptureOutput(() => cmd.ExecuteAsync(outputFormat: "minimal"));

        exitCode.ShouldBe(0);
        output.ShouldBeEmpty();
    }

    // --- Backward-compat regression ---

    [Fact]
    public async Task WithSearchText_ExecutesNormally()
    {
        var items = new[] { new WorkItemBuilder(1, "Login bug").InState("New").Build() };
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(items);

        var cmd = CreateCommand();
        var (exitCode, _) = await CaptureOutput(() => cmd.ExecuteAsync(searchText: "text"));

        exitCode.ShouldBe(0);
        await _adoService.Received(1)
            .QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // --- T-1639.2: Summary content validation ---

    [Fact]
    public async Task Summary_IncludesAllFilterFlags()
    {
        var cmd = CreateCommand();
        var (_, output) = await CaptureOutput(() => cmd.ExecuteAsync());

        output.ShouldContain("--type");
        output.ShouldContain("--state");
        output.ShouldContain("--assignedTo");
        output.ShouldContain("--areaPath");
        output.ShouldContain("--iterationPath");
        output.ShouldContain("--createdSince");
        output.ShouldContain("--changedSince");
        output.ShouldContain("--top");
        output.ShouldContain("--output");
    }

    [Fact]
    public async Task Summary_IncludesAllOutputFormatNames()
    {
        var cmd = CreateCommand();
        var (_, output) = await CaptureOutput(() => cmd.ExecuteAsync());

        output.ShouldContain("human");
        output.ShouldContain("json");
        output.ShouldContain("json-full");
        output.ShouldContain("json-compact");
        output.ShouldContain("minimal");
        output.ShouldContain("ids");
    }

    [Fact]
    public async Task Summary_ShowsConfiguredAreaPaths()
    {
        var configWithPaths = new TwigConfiguration
        {
            Organization = "https://dev.azure.com/org",
            Project = "MyProject",
            Defaults = new DefaultsConfig { AreaPaths = ["MyProject\\TeamA", "MyProject\\TeamB"] }
        };
        var cmd = CreateCommand(configOverride: configWithPaths);
        var (_, output) = await CaptureOutput(() => cmd.ExecuteAsync());

        output.ShouldContain("Area paths:");
        output.ShouldContain("MyProject\\TeamA");
        output.ShouldContain("MyProject\\TeamB");
    }

    [Fact]
    public async Task Summary_ShowsNoneWhenNoAreaPathsConfigured()
    {
        var cmd = CreateCommand();
        var (_, output) = await CaptureOutput(() => cmd.ExecuteAsync());

        output.ShouldContain("(none configured)");
    }
}
