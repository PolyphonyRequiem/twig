using System.Globalization;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
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

public sealed class BenchConfigurationCommandTests : RefreshCommandTestBase
{
    private readonly IAuthenticationProvider _authentication = Substitute.For<IAuthenticationProvider, IConnectionOperationGuard>();
    private readonly IConnectionBindingService _bindings = Substitute.For<IConnectionBindingService>();

    public BenchConfigurationCommandTests()
    {
        ((IConnectionOperationGuard)_authentication).AcquireOperationAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<IDisposable>());
        _bindings.ResolveAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedConnectionBinding(new IdentityBinding("binding", "connection", "actor", 1),
                new AuthenticationIdentity("actor", "Fixture", "tenant", "object", "issuer", "host", "credential", null),
                _testDir, "fixture", 1,
                new ConnectionOperationSnapshot("org", "MyProject", "team", "fingerprint", 1, "manifest", "config", "unicode", "config")));
    }

    [Fact]
    public async Task JsonRead_ReportsUncachedExplicitPinsAndActualRawDigestWithoutFetching()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var bench = await benches.GetOrCreateDefaultAsync([BenchSelector.ForCurrentSprintCanonical("old@example.test"), BenchSelector.ForItem(987)]);
        var command = CreateCommand(benches, new StringWriter());
        var (exit, output) = await CaptureAsync(() => command.ExecuteAsync(output: "json", expectBinding: "binding", expectIdentity: "actor"));
        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        var result = json.RootElement;
        result.GetProperty("settingsDigest").GetString().ShouldBe(BenchQueryRule.SettingsDigest(bench.Selectors));
        var ownership = result.GetProperty("assigneeSummary").GetString();
        ownership.ShouldNotBeNull();
        ownership.ShouldContain("Test User");
        var pin = result.GetProperty("pins")[0];
        pin.GetProperty("id").GetInt32().ShouldBe(987);
        pin.GetProperty("cached").GetBoolean().ShouldBeFalse();
        pin.TryGetProperty("title", out _).ShouldBeFalse();
        await _adoService.DidNotReceive().FetchBatchWithLinksAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AreaAndSprintEdits_AffectOnlyCapturedBenchAndDoNotChangeWorkspaceSettings()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var bench = (await benches.CreateAsync("configured"))!;
        var other = (await benches.CreateAsync("other"))!;
        await benches.SetCurrentAsync(bench.Id);
        var workspaceBefore = _config.Workspace;
        var command = CreateCommand(benches, new StringWriter());
        var (exit, output) = await CaptureAsync(() => command.ExecuteAsync("area", "add", "Project\\Team", exact: true,
            output: "json", expectBench: bench.Id.ToString(CultureInfo.InvariantCulture), expectSettings: BenchQueryRule.SettingsDigest(bench.Selectors)));
        exit.ShouldBe(0);
        using var areaJson = JsonDocument.Parse(output);
        areaJson.RootElement.GetProperty("automaticEnabled").GetBoolean().ShouldBeFalse();
        areaJson.RootElement.GetProperty("areas")[0].GetProperty("includeChildren").GetBoolean().ShouldBeFalse();
        var digest = areaJson.RootElement.GetProperty("settingsDigest").GetString();
        (exit, output) = await CaptureAsync(() => command.ExecuteAsync("sprint", "add", "@current+1", output: "json", expectSettings: digest));
        exit.ShouldBe(0);
        using var sprintJson = JsonDocument.Parse(output);
        sprintJson.RootElement.GetProperty("sprints")[0].GetProperty("expression").GetString().ShouldBe("@Current+1");
        sprintJson.RootElement.GetProperty("automaticEnabled").GetBoolean().ShouldBeTrue();
        (await benches.GetByNameAsync(other.Name))!.Selectors.ShouldBeEmpty();
        _config.Workspace.ShouldBeSameAs(workspaceBefore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BenchSwitchAfterSuccessfulCas_ReturnsCommittedCapturedConfiguration(bool isDefault)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var originalQuery = BenchSelector.ForCurrentSprintCanonical("old@example.test");
        var pin = BenchSelector.ForItem(987);
        var captured = isDefault
            ? await benches.GetOrCreateDefaultAsync([originalQuery, pin])
            : (await benches.CreateAsync("captured"))!;
        if (!isDefault)
        {
            await benches.AddSelectorAsync(captured.Id, originalQuery);
            await benches.AddSelectorAsync(captured.Id, pin);
        }
        captured = (await benches.GetByNameAsync(captured.Name))!;
        var other = (await benches.CreateAsync("other"))!;
        await benches.AddSelectorAsync(other.Id, BenchSelector.ForItem(456));
        await benches.SetCurrentAsync(captured.Id);
        var committed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Pause only after the real repository has committed; the switch runs before readback.
        var scheduled = Substitute.For<IBenchRepository>();
        scheduled.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(call => benches.GetCurrentAsync(call.ArgAt<CancellationToken>(0)));
        scheduled.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => benches.GetByNameAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        scheduled.TryReplaceQuerySelectorsAsync(Arg.Any<long>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<BenchSelector>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var ct = call.ArgAt<CancellationToken>(3);
                var applied = await benches.TryReplaceQuerySelectorsAsync(call.ArgAt<long>(0),
                    call.ArgAt<string>(1), call.ArgAt<IReadOnlyCollection<BenchSelector>>(2), ct);
                committed.TrySetResult(applied);
                await release.Task.WaitAsync(ct);
                return applied;
            });
        var stderr = new StringWriter();
        var command = CreateCommand(scheduled, stderr);
        var edit = CaptureAsync(() => command.ExecuteAsync("area", "add", "Project\\Team", exact: true,
            output: "json", expectBench: captured.Id.ToString(CultureInfo.InvariantCulture),
            expectBinding: "binding", expectIdentity: "actor",
            expectSettings: BenchQueryRule.SettingsDigest(captured.Selectors)));
        try
        {
            (await committed.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
            await benches.SetCurrentAsync(other.Id);
        }
        finally
        {
            release.TrySetResult();
            await edit;
        }

        var (exit, output) = await edit;
        exit.ShouldBe(0);
        stderr.ToString().ShouldBeEmpty();
        var stored = (await benches.GetByNameAsync(captured.Name))!;
        var rule = BenchQueryRule.Parse(stored.Selectors.Single(selector => selector.Kind == SelectorKind.Query));
        rule.Areas.ShouldBe([new AreaPathFilter("Project\\Team", false)]);
        rule.Sprints.Select(sprint => sprint.Raw).ShouldBe(["@Current"]);
        rule.UniqueName.ShouldBe(isDefault ? "Test User" : "old@example.test");
        stored.Selectors.ShouldContain(pin);
        (await benches.GetCurrentAsync())!.Id.ShouldBe(other.Id);
        (await benches.GetByNameAsync(other.Name))!.Selectors.ShouldBe([BenchSelector.ForItem(456)]);
        using var json = JsonDocument.Parse(output);
        var result = json.RootElement;
        result.GetProperty("benchId").GetString().ShouldBe(captured.Id.ToString(CultureInfo.InvariantCulture));
        result.GetProperty("benchName").GetString().ShouldBe(captured.Name);
        result.GetProperty("settingsDigest").GetString().ShouldBe(BenchQueryRule.SettingsDigest(stored.Selectors));
        result.GetProperty("areas")[0].GetProperty("path").GetString().ShouldBe("Project\\Team");
        result.GetProperty("areas")[0].GetProperty("includeChildren").GetBoolean().ShouldBeFalse();
        result.GetProperty("assigneeSummary").GetString().ShouldBe("Canonical principal: " + rule.UniqueName);
        result.GetProperty("pins")[0].GetProperty("id").GetInt32().ShouldBe(987);
    }

    [Fact]
    public async Task StaleSettingsAndOrigin_RefuseBeforeChangingCapturedSelectors()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var bench = (await benches.CreateAsync("captured"))!;
        await benches.SetCurrentAsync(bench.Id);
        var stale = BenchQueryRule.SettingsDigest(bench.Selectors);
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForCurrentSprint("Saved owner"));
        var stderr = new StringWriter();
        var command = CreateCommand(benches, stderr);
        var (exit, _) = await CaptureAsync(() => command.ExecuteAsync("sprint", "remove", "@Current", output: "json", expectSettings: stale));
        exit.ShouldBe(1);
        (await benches.GetByNameAsync(bench.Name))!.Selectors.ShouldBe([BenchSelector.ForCurrentSprint("Saved owner")]);
        stderr.GetStringBuilder().Clear();
        (exit, _) = await CaptureAsync(() => command.ExecuteAsync("area", "add", "Project", output: "json", expectIdentity: "old-actor"));
        exit.ShouldBe(1);
        (await benches.GetByNameAsync(bench.Name))!.Selectors.ShouldBe([BenchSelector.ForCurrentSprint("Saved owner")]);
    }

    [Theory]
    [InlineData("area", "Project\\\\Team")]
    [InlineData("sprint", "Project\\\\Sprint")]
    [InlineData("sprint", "@Current+")]
    public async Task InvalidPathOrExpression_ReturnsUsageErrorWithoutSaving(string section, string value)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var bench = (await benches.CreateAsync("empty"))!;
        await benches.SetCurrentAsync(bench.Id);
        var command = CreateCommand(benches, new StringWriter());
        var (exit, _) = await CaptureAsync(() => command.ExecuteAsync(section, "add", value, output: "json"));
        exit.ShouldBe(2);
        (await benches.GetByNameAsync(bench.Name))!.Selectors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("human")]
    [InlineData("minimal")]
    public async Task TextRead_LabelsUnknownPinsHonestly(string outputFormat)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var bench = (await benches.CreateAsync("fixture"))!;
        await benches.SetCurrentAsync(bench.Id);
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(987));
        var command = CreateCommand(benches, new StringWriter());
        var (exit, output) = await CaptureAsync(() => command.ExecuteAsync(output: outputFormat));
        exit.ShouldBe(0);
        output.ShouldContain("987");
        output.ShouldContain("uncached / unverified");
    }

    private BenchConfigurationCommand CreateCommand(IBenchRepository benches, TextWriter stderr)
    {
        var ctx = new CommandContext(new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory, new HintEngine(new DisplayConfig { Hints = false }), _config, Stderr: stderr);
        var defaults = new DefaultBenchSelectors(_iterationService);
        return new(ctx, new CurrentBenchResolver(benches, defaults), defaults, benches, _workItemRepo,
            _authentication, _bindings, _paths, new RendererFactory());
    }

    private static async Task<(int Exit, string Output)> CaptureAsync(Func<Task<int>> run)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try { Console.SetOut(output); return (await run(), output.ToString()); }
        finally { Console.SetOut(original); }
    }
}
