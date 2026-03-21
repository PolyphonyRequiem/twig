using Shouldly;
using Twig.Commands;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class ConfigCommandTests : IDisposable
{
    private readonly string _testDir;
    private readonly TwigPaths _paths;
    private readonly OutputFormatterFactory _formatterFactory;

    public ConfigCommandTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-config-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var twigDir = Path.Combine(_testDir, ".twig");
        Directory.CreateDirectory(twigDir);
        _paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
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

    [Fact]
    public async Task Config_Read_ReturnsValue()
    {
        var config = new TwigConfiguration { Organization = "https://dev.azure.com/myorg" };
        var cmd = new ConfigCommand(config, _paths, _formatterFactory);

        var result = await cmd.ExecuteAsync("organization");
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Config_Read_UnknownKey_ReturnsError()
    {
        var config = new TwigConfiguration();
        var cmd = new ConfigCommand(config, _paths, _formatterFactory);

        var result = await cmd.ExecuteAsync("unknown.key");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Config_Write_SetsValue()
    {
        var config = new TwigConfiguration();
        var cmd = new ConfigCommand(config, _paths, _formatterFactory);

        var result = await cmd.ExecuteAsync("seed.staledays", "7");

        result.ShouldBe(0);
        config.Seed.StaleDays.ShouldBe(7);
        File.Exists(_paths.ConfigPath).ShouldBeTrue();
    }

    [Fact]
    public async Task Config_Write_InvalidValue_ReturnsError()
    {
        var config = new TwigConfiguration();
        var cmd = new ConfigCommand(config, _paths, _formatterFactory);

        var result = await cmd.ExecuteAsync("seed.staledays", "not-a-number");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Config_EmptyKey_ReturnsUsageError()
    {
        var config = new TwigConfiguration();
        var cmd = new ConfigCommand(config, _paths, _formatterFactory);

        var result = await cmd.ExecuteAsync("");

        result.ShouldBe(2);
    }

    [Fact]
    public async Task Config_Read_DotPath_ReturnsNestedValue()
    {
        var config = new TwigConfiguration();
        config.Display.TreeDepth = 5;
        var cmd = new ConfigCommand(config, _paths, _formatterFactory);

        var result = await cmd.ExecuteAsync("display.treedepth");
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Config_Read_DisplayIcons()
    {
        var config = new TwigConfiguration();
        var cmd = new ConfigCommand(config, _paths, _formatterFactory);

        var result = await cmd.ExecuteAsync("display.icons");
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Config_Write_DisplayIcons_Nerd()
    {
        var config = new TwigConfiguration();
        var cmd = new ConfigCommand(config, _paths, _formatterFactory);

        var result = await cmd.ExecuteAsync("display.icons", "nerd");

        result.ShouldBe(0);
        config.Display.Icons.ShouldBe("nerd");
        File.Exists(_paths.ConfigPath).ShouldBeTrue();
    }
}
