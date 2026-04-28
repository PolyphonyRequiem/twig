using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class AreaCommandDeprecationTests : IDisposable
{
    private readonly string _testDir;
    private readonly TwigPaths _paths;

    public AreaCommandDeprecationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-area-depr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var twigDir = Path.Combine(_testDir, ".twig");
        Directory.CreateDirectory(twigDir);
        _paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));
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

    private TwigCommands CreateCommands(TwigConfiguration? config = null, IIterationService? iterationService = null)
    {
        config ??= new TwigConfiguration();
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(),
            new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()),
            new MinimalOutputFormatter());

        var areaCommand = new AreaCommand(config, _paths, formatterFactory, iterationService: iterationService);

        var services = new ServiceCollection()
            .AddSingleton(areaCommand)
            .BuildServiceProvider();

        return new TwigCommands(services);
    }

    // ── area (view) ────────────────────────────────────────────────────

    [Fact]
    public async Task Area_WritesDeprecationHint_ToStderr()
    {
        var commands = CreateCommands();

        var (_, stderr) = await StderrCapture.RunAsync(
            () => commands.Area(ct: CancellationToken.None));

        stderr.ShouldContain("hint: 'twig area' is deprecated. Use 'twig workspace area' instead.");
    }

    [Fact]
    public async Task Area_StillExecutesCommand_AfterHint()
    {
        var commands = CreateCommands();

        var (exitCode, stderr) = await StderrCapture.RunAsync(
            () => commands.Area(ct: CancellationToken.None));

        exitCode.ShouldBe(0);
        stderr.ShouldContain("deprecated");
    }

    // ── area add ───────────────────────────────────────────────────────

    [Fact]
    public async Task AreaAdd_WritesDeprecationHint_ToStderr()
    {
        var commands = CreateCommands();

        var (_, stderr) = await StderrCapture.RunAsync(
            () => commands.AreaAdd(@"Project\TeamA", ct: CancellationToken.None));

        stderr.ShouldContain("hint: 'twig area add' is deprecated. Use 'twig workspace area add' instead.");
    }

    [Fact]
    public async Task AreaAdd_StillExecutesCommand_AfterHint()
    {
        var commands = CreateCommands();

        var (exitCode, stderr) = await StderrCapture.RunAsync(
            () => commands.AreaAdd(@"Project\TeamA", ct: CancellationToken.None));

        exitCode.ShouldBe(0);
        stderr.ShouldContain("deprecated");
    }

    // ── area remove ────────────────────────────────────────────────────

    [Fact]
    public async Task AreaRemove_WritesDeprecationHint_ToStderr()
    {
        var config = new TwigConfiguration();
        config.Defaults.AreaPathEntries = [new AreaPathEntry { Path = @"Project\TeamA", IncludeChildren = true }];
        var commands = CreateCommands(config);

        var (_, stderr) = await StderrCapture.RunAsync(
            () => commands.AreaRemove(@"Project\TeamA", ct: CancellationToken.None));

        stderr.ShouldContain("hint: 'twig area remove' is deprecated. Use 'twig workspace area remove' instead.");
    }

    // ── area list ──────────────────────────────────────────────────────

    [Fact]
    public async Task AreaList_WritesDeprecationHint_ToStderr()
    {
        var commands = CreateCommands();

        var (_, stderr) = await StderrCapture.RunAsync(
            () => commands.AreaList(ct: CancellationToken.None));

        stderr.ShouldContain("hint: 'twig area list' is deprecated. Use 'twig workspace area list' instead.");
    }

    [Fact]
    public async Task AreaList_StillExecutesCommand_AfterHint()
    {
        var commands = CreateCommands();

        var (exitCode, stderr) = await StderrCapture.RunAsync(
            () => commands.AreaList(ct: CancellationToken.None));

        exitCode.ShouldBe(0);
        stderr.ShouldContain("deprecated");
    }

    // ── area sync ──────────────────────────────────────────────────────

    [Fact]
    public async Task AreaSync_WritesDeprecationHint_ToStderr()
    {
        var commands = CreateCommands();

        var (_, stderr) = await StderrCapture.RunAsync(
            () => commands.AreaSync(ct: CancellationToken.None));

        stderr.ShouldContain("hint: 'twig area sync' is deprecated. Use 'twig workspace area sync' instead.");
    }

    // ── Parameterized coverage ─────────────────────────────────────────

    [Theory]
    [InlineData("area", "twig area", "twig workspace area")]
    [InlineData("area add", "twig area add", "twig workspace area add")]
    [InlineData("area remove", "twig area remove", "twig workspace area remove")]
    [InlineData("area list", "twig area list", "twig workspace area list")]
    [InlineData("area sync", "twig area sync", "twig workspace area sync")]
    public async Task AllAreaAliases_ContainConsistentDeprecationFormat(string command, string oldName, string newName)
    {
        var config = new TwigConfiguration();
        if (command == "area remove")
            config.Defaults.AreaPathEntries = [new AreaPathEntry { Path = @"Project\TeamA", IncludeChildren = true }];

        var commands = CreateCommands(config);

        var (_, stderr) = await StderrCapture.RunAsync(async () =>
        {
            return command switch
            {
                "area" => await commands.Area(ct: CancellationToken.None),
                "area add" => await commands.AreaAdd(@"Project\TeamA", ct: CancellationToken.None),
                "area remove" => await commands.AreaRemove(@"Project\TeamA", ct: CancellationToken.None),
                "area list" => await commands.AreaList(ct: CancellationToken.None),
                "area sync" => await commands.AreaSync(ct: CancellationToken.None),
                _ => throw new ArgumentException($"Unknown command: {command}")
            };
        });

        stderr.ShouldContain($"hint: '{oldName}' is deprecated. Use '{newName}' instead.");
    }
}
