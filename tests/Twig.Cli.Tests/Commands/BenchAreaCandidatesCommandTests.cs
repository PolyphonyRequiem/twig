using System.Globalization;
using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Hints;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class BenchAreaCandidatesCommandTests : RefreshCommandTestBase
{
    private readonly IAuthenticationProvider _authentication = Substitute.For<IAuthenticationProvider, IConnectionOperationGuard>();
    private readonly IConnectionBindingService _bindings = Substitute.For<IConnectionBindingService>();

    public BenchAreaCandidatesCommandTests()
    {
        ((IConnectionOperationGuard)_authentication).AcquireOperationAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<IDisposable>());
        _bindings.ResolveAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedConnectionBinding(new IdentityBinding("binding", "connection", "actor", 1),
                new AuthenticationIdentity("actor", "Fixture", "tenant", "object", "issuer", "host", "credential", null),
                _testDir, "fixture", 1,
                new ConnectionOperationSnapshot("org", "MyProject", "configured-team", "fingerprint", 1, "manifest", "config", "unicode", "config")));
    }

    [Fact]
    public async Task ReadingTeamCandidates_DoesNotImportPathsOrChangeBenchOrWorkspace()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var bench = (await benches.CreateAsync("captured"))!;
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(987));
        await benches.SetCurrentAsync(bench.Id);
        var selectors = (await benches.GetCurrentAsync())!.Selectors.ToArray();
        _config.Team = "configured-team";
        _config.Defaults.AreaPathEntries = [new() { Path = "Project\\Existing", IncludeChildren = false }];
        await _config.SaveAsync(_paths.ConfigPath);
        var configBytes = await File.ReadAllBytesAsync(_paths.ConfigPath);
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(string Path, bool IncludeChildren)> { ("Project\\Official", true), ("Project\\Exact", false) });
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter()).ExecuteAsync(output: "json",
            expectBench: bench.Id.ToString(CultureInfo.InvariantCulture), expectBinding: "binding", expectIdentity: "actor"));
        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("settingsDigest").GetString().ShouldBe(BenchQueryRule.SettingsDigest(selectors));
        (await benches.GetCurrentAsync())!.Selectors.ShouldBe(selectors);
        _config.Defaults.AreaPathEntries.ShouldHaveSingleItem().Path.ShouldBe("Project\\Existing");
        _config.Defaults.AreaPathEntries![0].IncludeChildren.ShouldBeFalse();
        _config.Team.ShouldBe("configured-team");
        (await File.ReadAllBytesAsync(_paths.ConfigPath)).ShouldBe(configBytes);
    }

    [Theory]
    [InlineData("old-binding", "actor")]
    [InlineData("binding", "old-actor")]
    public async Task WrongOrigin_RefusesBeforeReadingTeamData(string expectedBinding, string expectedIdentity)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var bench = (await benches.CreateAsync("captured"))!;
        await benches.SetCurrentAsync(bench.Id);
        var stderr = new StringWriter();
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, stderr).ExecuteAsync(output: "json",
            expectBinding: expectedBinding, expectIdentity: expectedIdentity));
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        using var error = JsonDocument.Parse(stderr.ToString());
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
        await _iterationService.DidNotReceive().GetTeamAreaPathsAsync(Arg.Any<CancellationToken>());
        (await benches.GetCurrentAsync())!.Selectors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BenchOrSettingsChangingDuringLookup_RefusesObservationRatherThanRetargeting(bool switchBench)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var captured = (await benches.CreateAsync("captured"))!;
        var other = (await benches.CreateAsync("other"))!;
        await benches.SetCurrentAsync(captured.Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<IReadOnlyList<(string Path, bool IncludeChildren)>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.SetResult();
            return result.Task;
        });
        var stderr = new StringWriter();
        var read = CaptureAsync(() => CreateCommand(benches, stderr).ExecuteAsync(output: "json",
            expectBench: captured.Id.ToString(CultureInfo.InvariantCulture), expectBinding: "binding", expectIdentity: "actor"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (switchBench) await benches.SetCurrentAsync(other.Id);
        else await benches.AddSelectorAsync(captured.Id, BenchSelector.ForCurrentSprint("saved owner"));
        result.SetResult([("Project\\Official", false)]);
        var (exit, output) = await read;
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        using var error = JsonDocument.Parse(stderr.ToString());
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
        (await benches.GetByNameAsync(other.Name))!.Selectors.ShouldBeEmpty();
        var stored = (await benches.GetByNameAsync(captured.Name))!;
        if (switchBench) stored.Selectors.ShouldBeEmpty();
        else stored.Selectors.ShouldBe([BenchSelector.ForCurrentSprint("saved owner")]);
    }

    [Fact]
    public async Task FailedTeamLookup_ReportsFailureWithoutGuessingWorkspacePaths()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var captured = (await benches.CreateAsync("captured"))!;
        await benches.SetCurrentAsync(captured.Id);
        _config.Defaults.AreaPathEntries = [new() { Path = "Project\\NotATeamObservation", IncludeChildren = true }];
        _iterationService.GetTeamAreaPathsAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("Team lookup unavailable"));
        var stderr = new StringWriter();
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, stderr).ExecuteAsync(output: "json"));
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        using var error = JsonDocument.Parse(stderr.ToString());
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
        (await benches.GetCurrentAsync())!.Selectors.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoBench_ReadRefusesWithoutCreatingDefaultOrDiscoveringIdentity()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter()).ExecuteAsync(output: "json"));
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        (await benches.GetCurrentAsync()).ShouldBeNull();
        (await benches.GetByNameAsync(Twig.Domain.Aggregates.Bench.DefaultName)).ShouldBeNull();
        await _iterationService.DidNotReceive().GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>());
        await _iterationService.DidNotReceive().GetTeamAreaPathsAsync(Arg.Any<CancellationToken>());
    }

    private BenchAreaCandidatesCommand CreateCommand(IBenchRepository benches, TextWriter stderr)
    {
        var ctx = new CommandContext(new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory, new HintEngine(new DisplayConfig { Hints = false }), _config, Stderr: stderr);
        return new(ctx, new CurrentBenchResolver(benches, new DefaultBenchSelectors(_iterationService)), benches, _workItemRepo,
            _iterationService, _authentication, _bindings, _paths, new RendererFactory());
    }

    private static async Task<(int Exit, string Output)> CaptureAsync(Func<Task<int>> run)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try { Console.SetOut(output); return (await run(), output.ToString()); }
        finally { Console.SetOut(original); }
    }
}
