using Shouldly;
using Twig.Commands;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class SprintCommandTests : IDisposable
{
    private readonly string _testDir;
    private readonly TwigPaths _paths;
    private readonly OutputFormatterFactory _formatterFactory;

    public SprintCommandTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-sprint-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var twigDir = Path.Combine(_testDir, ".twig");
        Directory.CreateDirectory(twigDir);
        _paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(),
            new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()),
            new MinimalOutputFormatter());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    private SprintCommand CreateCommand(TwigConfiguration? config = null)
    {
        config ??= new TwigConfiguration();
        return new SprintCommand(config, _paths, _formatterFactory);
    }

    // ── Add ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_RelativeExpression_ReturnsZero()
    {
        var config = new TwigConfiguration();
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.AddAsync("@current"));

        result.ShouldBe(0);
        stdout.ShouldContain("Added");
        stdout.ShouldContain("@current");
        config.Workspace.Sprints.ShouldNotBeNull();
        config.Workspace.Sprints.Count.ShouldBe(1);
        config.Workspace.Sprints[0].Expression.ShouldBe("@current");
    }

    [Fact]
    public async Task Add_RelativeExpressionWithOffset_ReturnsZero()
    {
        var config = new TwigConfiguration();
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.AddAsync("@current-1"));

        result.ShouldBe(0);
        stdout.ShouldContain("Added");
        config.Workspace.Sprints.ShouldNotBeNull();
        config.Workspace.Sprints[0].Expression.ShouldBe("@current-1");
    }

    [Fact]
    public async Task Add_AbsolutePath_ReturnsZero()
    {
        var config = new TwigConfiguration();
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.AddAsync(@"Project\Sprint 5"));

        result.ShouldBe(0);
        stdout.ShouldContain("Added");
        config.Workspace.Sprints.ShouldNotBeNull();
        config.Workspace.Sprints[0].Expression.ShouldBe(@"Project\Sprint 5");
    }

    [Fact]
    public async Task Add_Duplicate_ReturnsOne()
    {
        var config = new TwigConfiguration();
        config.Workspace.Sprints = [new SprintEntry { Expression = "@current" }];
        var cmd = CreateCommand(config);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.AddAsync("@current"));

        result.ShouldBe(1);
        stderr.ShouldContain("already configured");
    }

    [Fact]
    public async Task Add_DuplicateCaseInsensitive_ReturnsOne()
    {
        var config = new TwigConfiguration();
        config.Workspace.Sprints = [new SprintEntry { Expression = "@Current" }];
        var cmd = CreateCommand(config);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.AddAsync("@CURRENT"));

        result.ShouldBe(1);
        stderr.ShouldContain("already configured");
    }

    [Fact]
    public async Task Add_EmptyExpression_ReturnsTwo()
    {
        var cmd = CreateCommand();

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.AddAsync(""));

        result.ShouldBe(2);
        stderr.ShouldContain("cannot be empty");
    }

    [Fact]
    public async Task Add_WhitespaceExpression_ReturnsTwo()
    {
        var cmd = CreateCommand();

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.AddAsync("   "));

        result.ShouldBe(2);
        stderr.ShouldContain("cannot be empty");
    }

    [Fact]
    public async Task Add_MultipleExpressions_AccumulatesInList()
    {
        var config = new TwigConfiguration();
        var cmd = CreateCommand(config);

        await StdoutCapture.RunAsync(() => cmd.AddAsync("@current"));
        await StdoutCapture.RunAsync(() => cmd.AddAsync("@current-1"));

        config.Workspace.Sprints.ShouldNotBeNull();
        config.Workspace.Sprints.Count.ShouldBe(2);
        config.Workspace.Sprints[0].Expression.ShouldBe("@current");
        config.Workspace.Sprints[1].Expression.ShouldBe("@current-1");
    }

    // ── Remove ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_ExistingExpression_ReturnsZero()
    {
        var config = new TwigConfiguration();
        config.Workspace.Sprints = [new SprintEntry { Expression = "@current" }];
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.RemoveAsync("@current"));

        result.ShouldBe(0);
        stdout.ShouldContain("Removed");
        config.Workspace.Sprints.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Remove_CaseInsensitiveMatch_ReturnsZero()
    {
        var config = new TwigConfiguration();
        config.Workspace.Sprints = [new SprintEntry { Expression = "@Current" }];
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.RemoveAsync("@CURRENT"));

        result.ShouldBe(0);
        stdout.ShouldContain("Removed");
        config.Workspace.Sprints.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Remove_NonexistentExpression_ReturnsOne()
    {
        var config = new TwigConfiguration();
        config.Workspace.Sprints = [new SprintEntry { Expression = "@current" }];
        var cmd = CreateCommand(config);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.RemoveAsync("@current+1"));

        result.ShouldBe(1);
        stderr.ShouldContain("not configured");
    }

    [Fact]
    public async Task Remove_EmptySprintsList_ReturnsOne()
    {
        var config = new TwigConfiguration();
        var cmd = CreateCommand(config);

        var (result, stderr) = await StderrCapture.RunAsync(
            () => cmd.RemoveAsync("@current"));

        result.ShouldBe(1);
        stderr.ShouldContain("No sprint expressions configured");
    }

    // ── List ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_Empty_ShowsHintMessage()
    {
        var cmd = CreateCommand();

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.ListAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("No sprint expressions configured");
        stdout.ShouldContain("twig workspace sprint add");
    }

    [Fact]
    public async Task List_WithEntries_ShowsExpressions()
    {
        var config = new TwigConfiguration();
        config.Workspace.Sprints =
        [
            new SprintEntry { Expression = "@current" },
            new SprintEntry { Expression = "@current-1" },
        ];
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.ListAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("@current");
        stdout.ShouldContain("@current-1");
        stdout.ShouldContain("2 sprint expression(s) configured.");
    }

    [Fact]
    public async Task List_SingleEntry_ShowsSingularCount()
    {
        var config = new TwigConfiguration();
        config.Workspace.Sprints =
        [
            new SprintEntry { Expression = "@current" },
        ];
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.ListAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("@current");
        stdout.ShouldContain("1 sprint expression(s) configured.");
    }

    [Fact]
    public async Task List_NullSprintsList_ShowsHintMessage()
    {
        var config = new TwigConfiguration();
        config.Workspace.Sprints = null;
        var cmd = CreateCommand(config);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.ListAsync());

        result.ShouldBe(0);
        stdout.ShouldContain("No sprint expressions configured");
    }
}
