using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for user identity detection during <c>twig init</c>.
/// </summary>
public class InitUserDetectionTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _twigDir;
    private readonly string _configPath;
    private readonly IIterationService _iterationService;
    private readonly TwigPaths _paths;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public InitUserDetectionTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-inituser-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _twigDir = Path.Combine(_testDir, ".twig");
        _configPath = Path.Combine(_twigDir, "config");

        _iterationService = Substitute.For<IIterationService>();
        _iterationService.DetectTemplateNameAsync(Arg.Any<CancellationToken>())
            .Returns("Agile");
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _iterationService.GetWorkItemTypeAppearancesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeAppearance>());
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(string Path, bool IncludeChildren)>());

        _paths = new TwigPaths(_twigDir, _configPath, Path.Combine(_twigDir, "twig.db"));
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
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
    public async Task Init_DetectsUserIdentity_StoresInConfig()
    {
        _iterationService.GetAuthenticatedUserDisplayNameAsync(Arg.Any<CancellationToken>())
            .Returns("Alice Smith");

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0);
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.User.DisplayName.ShouldBe("Alice Smith");
    }

    [Fact]
    public async Task Init_UserDetectionFails_GracefulFallback()
    {
        _iterationService.GetAuthenticatedUserDisplayNameAsync(Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0); // Init should still succeed
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.User.DisplayName.ShouldBeNull();
    }

    [Fact]
    public async Task Init_UserDetectionThrows_GracefulFallback()
    {
        _iterationService.GetAuthenticatedUserDisplayNameAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Network error"));

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);
        var result = await cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject");

        result.ShouldBe(0); // Init should still succeed
        var loaded = await TwigConfiguration.LoadAsync(_configPath);
        loaded.User.DisplayName.ShouldBeNull();
    }

    [Fact]
    public async Task Init_UserDetectionThrowsOCE_PropagatesOut()
    {
        _iterationService.GetAuthenticatedUserDisplayNameAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException("Cancelled"));

        var cmd = new InitCommand(_iterationService, _paths, _formatterFactory, _hintEngine);

        await Should.ThrowAsync<OperationCanceledException>(
            () => cmd.ExecuteAsync("https://dev.azure.com/org", "MyProject"));
    }
}
