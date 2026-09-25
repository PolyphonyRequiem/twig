using Shouldly;
using Twig.Commands;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class MigrateConfigCommandTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly TwigPaths _paths;

    public MigrateConfigCommandTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), $"twig-migrate-test-{Guid.NewGuid():N}");
        var twigDir = Path.Combine(_repoRoot, ".twig");
        _paths = TwigPaths.ForContext(twigDir, "myorg", "myproject", _repoRoot);
        Directory.CreateDirectory(_repoRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
    }

    [Fact]
    public async Task DryRun_AlreadySplit_ReportsNoChangesAndDoesNotModifyFiles()
    {
        var config = new TwigConfiguration
        {
            Organization = "myorg",
            Project = "myproject",
        };
        await config.SaveSplitAsync(_paths);
        var gitignorePath = Path.Combine(_repoRoot, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, $".twig/{Environment.NewLine}");
        var repoBefore = await File.ReadAllBytesAsync(_paths.RepoConfigPath);
        var gitignoreBefore = await File.ReadAllBytesAsync(gitignorePath);
        var command = new MigrateConfigCommand(
            _paths,
            new OutputFormatterFactory(new HumanOutputFormatter()),
            new RendererFactory());

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => command.ExecuteAsync(dryRun: true));

        result.ShouldBe(0);
        stdout.ShouldContain("nothing to do");
        stdout.ShouldNotContain("would write");
        stdout.ShouldNotContain("would rewrite");
        (await File.ReadAllBytesAsync(_paths.RepoConfigPath)).ShouldBe(repoBefore);
        File.Exists(_paths.ConfigPath).ShouldBeFalse();
        (await File.ReadAllBytesAsync(gitignorePath)).ShouldBe(gitignoreBefore);
    }

    [Fact]
    public async Task Migrate_RemovesDefaultsOnlyLocalConfig()
    {
        var config = new TwigConfiguration { Organization = "myorg", Project = "myproject" };
        await config.SaveSplitAsync(_paths);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.ConfigPath)!);
        await File.WriteAllTextAsync(_paths.ConfigPath, """{"auth":{},"display":{}}""");
        await File.WriteAllTextAsync(Path.Combine(_repoRoot, ".gitignore"), $".twig/{Environment.NewLine}");
        var command = new MigrateConfigCommand(
            _paths,
            new OutputFormatterFactory(new HumanOutputFormatter()),
            new RendererFactory());

        var (previewResult, preview) = await StdoutCapture.RunAsync(
            () => command.ExecuteAsync(dryRun: true));
        previewResult.ShouldBe(0);
        preview.ShouldContain("would remove");
        File.Exists(_paths.ConfigPath).ShouldBeTrue();

        var (applyResult, applied) = await StdoutCapture.RunAsync(
            () => command.ExecuteAsync());
        applyResult.ShouldBe(0);
        applied.ShouldContain("removed");
        File.Exists(_paths.ConfigPath).ShouldBeFalse();
        var loaded = await TwigConfiguration.LoadSplitAsync(_paths);
        loaded.Auth.Method.ShouldBe("azcli");
        loaded.Display.Icons.ShouldBe(GlobalDisplayPreferences.Load(_paths.GlobalDisplayPath).Icons ?? "auto");
    }
}
