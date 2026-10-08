using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
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

public sealed class BenchDetailCommandTests : RefreshCommandTestBase
{
    private readonly IAuthenticationProvider _authentication = Substitute.For<IAuthenticationProvider, IConnectionOperationGuard>();
    private readonly IConnectionBindingService _bindings = Substitute.For<IConnectionBindingService>();

    public BenchDetailCommandTests()
    {
        ((IConnectionOperationGuard)_authentication).AcquireOperationAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<IDisposable>());
        _bindings.ResolveAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedConnectionBinding(new IdentityBinding("binding", "connection", "actor", 1),
                new AuthenticationIdentity("actor", "Fixture", "tenant", "object", "issuer", "host", "credential", null),
                _testDir, "fixture", 1,
                new ConnectionOperationSnapshot("org", "MyProject", "team", "fingerprint", 1, "manifest", "config", "unicode", "config")));
    }

    [Fact]
    public async Task Json_PreservesEveryCachedHtmlFieldAndLongTailWithoutIdentityDiscovery()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var bench = await benches.GetOrCreateDefaultAsync([]);
        var item = CreateWorkItem(42, "Full detail");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["Custom.Body"] = string.Concat(Enumerable.Range(1, 65).Select(i => $"<p>paragraph-{i:D2}</p>")),
            ["Custom.Acceptance"] = "<h2>Acceptance</h2><p>" + new string('z', 250) + " LAST-VALUE</p>",
            ["Custom.Empty"] = null,
        });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns([
            new FieldDefinition("Custom.Body", "Body", "html", false),
            new FieldDefinition("Custom.Acceptance", "Acceptance Criteria", "html", false),
            new FieldDefinition("Custom.Empty", "Blank field", "html", false),
        ]);
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter()).ExecuteAsync(42, width: 64, output: "json",
            expectBench: bench.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), expectBinding: "binding", expectIdentity: "actor"));
        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        var ansi = json.RootElement.GetProperty("ansi").GetString()!;
        ansi.ShouldContain("paragraph-65");
        ansi.ShouldContain("LAST-VALUE");
        ansi.ShouldContain("Blank field");
        ansi.ShouldContain("empty in cached snapshot");
        ansi.ShouldNotContain("more lines");
        await _iterationService.DidNotReceive().GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheMissAndStaleOrigin_RefuseBeforeConsumingUnadmittedCachedContent()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        var stderr = new StringWriter();
        var command = CreateCommand(benches, stderr);
        var (exit, output) = await CaptureAsync(() => command.ExecuteAsync(987, output: "json", expectIdentity: "other"));
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        await _workItemRepo.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        stderr.GetStringBuilder().Clear();
        (exit, output) = await CaptureAsync(() => command.ExecuteAsync(987, output: "json"));
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        using var error = JsonDocument.Parse(stderr.ToString());
        error.RootElement.GetProperty("error").GetString()!.ShouldContain("not cached");
    }

    [Fact]
    public async Task NegativeSeed_MinimalShowsCompleteLocalDraftAndUnknownFreshnessTruthfully()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        var item = new WorkItem { Id = -3, Title = "Local draft", IsSeed = true, Type = WorkItemType.Task };
        item.ImportFields(new Dictionary<string, string?> { ["Custom.Body"] = "<pre>    if (ready) {\n        finish();\n    }</pre>" });
        _workItemRepo.GetByIdAsync(-3, Arg.Any<CancellationToken>()).Returns(item);
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns([new FieldDefinition("Custom.Body", "Body", "html", false)]);
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter()).ExecuteAsync(-3, width: 80, output: "minimal"));
        exit.ShouldBe(0);
        output.ShouldContain("local seed");
        output.ShouldContain("unpublished");
        output.ShouldContain("    if (ready)");
        output.ShouldContain("        finish();");
        output.ShouldNotContain("\x1b");
    }

    [Fact]
    public async Task BenchSwitchDuringMetadataRead_RefusesBeforeEmittingCapturedDetail()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var captured = (await benches.CreateAsync("captured"))!;
        var replacement = (await benches.CreateAsync("replacement"))!;
        await benches.SetCurrentAsync(captured.Id);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Private captured detail"));
        var metadataStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource<IReadOnlyList<FieldDefinition>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            metadataStarted.TrySetResult();
            return metadata.Task;
        });
        var stderr = new StringWriter();
        var command = CreateCommand(benches, stderr);
        // One capture spans the entire scheduled race; no overlapping stdout swaps.
        var (exit, output) = await CaptureAsync(async () =>
        {
            var read = command.ExecuteAsync(42, output: "json",
                expectBench: captured.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                await metadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await benches.SetCurrentAsync(replacement.Id);
            }
            finally { metadata.TrySetResult(Array.Empty<FieldDefinition>()); }
            return await read;
        });
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        using var error = JsonDocument.Parse(stderr.ToString());
        error.RootElement.GetProperty("error").GetString()!.ShouldContain("Bench changed");
        (await benches.GetCurrentAsync())!.Id.ShouldBe(replacement.Id);
    }

    private BenchDetailCommand CreateCommand(IBenchRepository benches, TextWriter stderr)
    {
        var ctx = new CommandContext(new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory, new HintEngine(new DisplayConfig { Hints = false }), _config, Stderr: stderr);
        return new(ctx, new CurrentBenchResolver(benches, new DefaultBenchSelectors(_iterationService)), benches,
            _workItemRepo, _fieldDefinitionStore, _authentication, _bindings, _paths, new RendererFactory());
    }

    private static async Task<(int Exit, string Output)> CaptureAsync(Func<Task<int>> run)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try { Console.SetOut(output); return (await run(), output.ToString()); }
        finally { Console.SetOut(original); }
    }
}
