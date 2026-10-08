using System.Text.Json;
using System.Text.RegularExpressions;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Sync;
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
    private readonly IWorkItemLinkRepository _links = Substitute.For<IWorkItemLinkRepository>();
    private readonly IProcessConfigurationProvider _processConfiguration = Substitute.For<IProcessConfigurationProvider>();

    public BenchDetailCommandTests()
    {
        ((IConnectionOperationGuard)_authentication).AcquireOperationAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<IDisposable>());
        _bindings.ResolveAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedConnectionBinding(new IdentityBinding("binding", "connection", "actor", 1),
                new AuthenticationIdentity("actor", "Fixture", "tenant", "object", "issuer", "host", "credential", null),
                _testDir, "fixture", 1,
                new ConnectionOperationSnapshot("org", "MyProject", "team", "fingerprint", 1, "manifest", "config", "unicode", "config")));
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _links.GetLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItemLink>());
        _pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
    }

    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public async Task Json_SingleOpenRightFramePreservesFullContentAtAnyWidthWithoutIdentityDiscovery(int width)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var bench = await benches.GetOrCreateDefaultAsync([]);
        var item = CreateWorkItem(42, "Full detail");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = string.Concat(Enumerable.Range(1, 65).Select(i => $"<p>description-{i:D2}</p>")),
            ["Custom.Body"] = string.Concat(Enumerable.Range(1, 65).Select(i => $"<p>paragraph-{i:D2}</p>")),
            ["Custom.Acceptance"] = "<h2>Acceptance</h2><p>" + new string('z', 250) + " LAST-VALUE</p>",
            ["Custom.Value"] = new string('v', 220) + " STRING-TAIL",
        });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns([
            new FieldDefinition("Custom.Body", "Body", "html", false),
            new FieldDefinition("Custom.Acceptance", "Acceptance Criteria", "html", false),
        ]);
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter()).ExecuteAsync(42, width: width, output: "json",
            expectBench: bench.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), expectBinding: "binding", expectIdentity: "actor"));
        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        json.RootElement.GetProperty("version").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("benchId").GetString().ShouldBe(bench.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        json.RootElement.GetProperty("bindingId").GetString().ShouldBe("binding");
        json.RootElement.GetProperty("identityId").GetString().ShouldBe("actor");
        json.RootElement.GetProperty("workItemId").GetInt32().ShouldBe(42);
        json.RootElement.GetProperty("title").GetString().ShouldBe("Full detail");
        var ansi = json.RootElement.GetProperty("ansi").GetString()!;
        ansi.ShouldContain("paragraph-65");
        ansi.ShouldContain("description-65");
        ansi.ShouldContain("LAST-VALUE");
        ansi.ShouldNotContain("more lines");
        ansi.ShouldContain("STRING-TAIL");
        var plain = Regex.Replace(ansi, "\\x1b\\[[0-9;:]*m", "");
        var lines = plain.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r', ' ')).ToArray();
        lines[0].ShouldStartWith("┌");
        lines[0].ShouldEndWith("─");
        lines[^1].ShouldStartWith("└");
        lines[^1].ShouldEndWith("─");
        lines.Count(line => line.Contains("Full detail", StringComparison.Ordinal)).ShouldBe(1);
        foreach (var line in lines.Skip(1).SkipLast(1))
        {
            line.ShouldStartWith("│");
            line.Count(character => character == '│').ShouldBe(1);
        }
        plain.ShouldNotContain("┐");
        plain.ShouldNotContain("┘");
        plain.ShouldNotContain("╭");
        plain.ShouldNotContain("╮");
        plain.ShouldNotContain("╰");
        plain.ShouldNotContain("╯");
        _adoService.ReceivedCalls().ShouldBeEmpty();
        _contextStore.ReceivedCalls().ShouldBeEmpty();
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

    [Fact]
    public async Task BenchSwitchDuringFinalBindingRead_RefusesBeforeEmittingCapturedDetail()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var captured = (await benches.CreateAsync("captured"))!;
        var replacement = (await benches.CreateAsync("replacement"))!;
        await benches.SetCurrentAsync(captured.Id);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Captured detail"));
        var binding = await _bindings.ResolveAsync(_config, _paths);
        var bindingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalBinding = new TaskCompletionSource<ResolvedConnectionBinding>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        _bindings.ResolveAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++reads == 1) return Task.FromResult(binding);
                bindingStarted.TrySetResult();
                return finalBinding.Task;
            });
        var (exit, output) = await CaptureAsync(async () =>
        {
            var read = CreateCommand(benches, new StringWriter()).ExecuteAsync(42, output: "json");
            try
            {
                await bindingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await benches.SetCurrentAsync(replacement.Id);
            }
            finally { finalBinding.TrySetResult(binding); }
            return await read;
        });
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        (await benches.GetCurrentAsync())!.Id.ShouldBe(replacement.Id);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(64)]
    public async Task DefaultAndRepeatedRead_ReReadCacheWithoutNetworkOrContextChanges(int width)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        var command = CreateCommand(benches, new StringWriter());
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Initial cached title"));
        var (firstExit, first) = await CaptureAsync(() => command.ExecuteAsync(42, output: "json"));
        var updatedTitle = width < 60 ? "Changed title with TITLE-TAIL" : "Latest cached title " + new string('x', 140) + " TITLE-TAIL";
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, updatedTitle));
        var (secondExit, second) = await CaptureAsync(() => command.ExecuteAsync(42, width: width, output: "json"));
        firstExit.ShouldBe(0);
        secondExit.ShouldBe(0);
        using var initial = JsonDocument.Parse(first);
        using var latest = JsonDocument.Parse(second);
        initial.RootElement.GetProperty("title").GetString().ShouldBe("Initial cached title");
        latest.RootElement.GetProperty("title").GetString().ShouldBe(updatedTitle);
        latest.RootElement.GetProperty("ansi").GetString()!.ShouldContain("TITLE-TAIL");
        _adoService.ReceivedCalls().ShouldBeEmpty();
        _iterationService.ReceivedCalls().ShouldBeEmpty();
        _contextStore.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExplicitSync_CanRetrieveCacheMissButNeverMaterializesLinkedTargetsOrChildren()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        var remote = CreateWorkItem(42, "Fetched selected item");
        remote.SetField("System.Description", "<p>Fetched description</p>");
        WorkItem? cached = null;
        IReadOnlyList<WorkItemLink> cachedLinks = [];
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(_ => cached);
        _workItemRepo.When(repo => repo.SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()))
            .Do(call => cached = call.ArgAt<WorkItem>(0));
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>()).Returns([CreateWorkItem(80, "Cached child")]);
        _links.When(repo => repo.SaveLinksAsync(42, Arg.Any<IReadOnlyList<WorkItemLink>>(), Arg.Any<CancellationToken>()))
            .Do(call => cachedLinks = call.ArgAt<IReadOnlyList<WorkItemLink>>(1));
        _links.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns(_ => cachedLinks);
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)[new(42, 99, LinkTypes.Related)]));
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter()).ExecuteAsync(42, output: "json", sync: true));
        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        var ansi = json.RootElement.GetProperty("ansi").GetString()!;
        ansi.ShouldContain("Fetched description");
        ansi.ShouldContain("Cached child");
        ansi.ShouldContain("#99");
        _adoService.ReceivedCalls().Select(call => call.GetMethodInfo().Name).ShouldBe(["FetchWithLinksAsync"]);
        _contextStore.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExplicitSync_ProtectedLocalEditsAndPendingIndicatorsSurvive()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        var local = CreateWorkItem(42, "Local protected item");
        local.UpdateField("System.Description", "<p>Unpublished local edit</p>");
        PendingChangeRecord[] pending = [new(42, "field", "System.Description", "Old", "<p>Unpublished local edit</p>")];
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(local);
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>()).Returns([local]);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns([42]);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>()).Returns(pending);
        var remote = CreateWorkItem(42, "Remote overwrite");
        remote.SetField("System.Description", "Remote description");
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((remote, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter()).ExecuteAsync(42, output: "json", sync: true));
        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        var ansi = json.RootElement.GetProperty("ansi").GetString()!;
        ansi.ShouldContain("Local protected item");
        ansi.ShouldContain("Unpublished local edit");
        ansi.ShouldNotContain("Remote overwrite");
        ansi.ShouldNotContain("Remote description");
        local.IsDirty.ShouldBeTrue();
        (await _pendingChangeStore.GetChangesAsync(42)).ShouldBe(pending);
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        await _pendingChangeStore.DidNotReceive().ClearChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        _adoService.ReceivedCalls().Select(call => call.GetMethodInfo().Name).ShouldBe(["FetchWithLinksAsync"]);
    }

    [Fact]
    public async Task SeedSync_IsRefusedBeforeAnyRemoteRequest()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        var stderr = new StringWriter();
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, stderr).ExecuteAsync(-3, output: "json", sync: true));
        exit.ShouldBe(2);
        output.ShouldBeEmpty();
        using var error = JsonDocument.Parse(stderr.ToString());
        error.RootElement.GetProperty("error").GetString()!.ShouldContain("seeds cannot sync");
        _adoService.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BenchOrOriginChangeDuringSync_RefusesCapturedDetail(bool changeBench)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var captured = (await benches.CreateAsync("captured"))!;
        var replacement = (await benches.CreateAsync("replacement"))!;
        await benches.SetCurrentAsync(captured.Id);
        var capturedBinding = await _bindings.ResolveAsync(_config, _paths);
        var currentBinding = capturedBinding;
        _bindings.ResolveAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>())
            .Returns(_ => currentBinding);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<(WorkItem, IReadOnlyList<WorkItemLink>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            started.TrySetResult();
            return response.Task;
        });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Captured private detail"));
        var stderr = new StringWriter();
        var command = CreateCommand(benches, stderr);
        var (exit, output) = await CaptureAsync(async () =>
        {
            var read = command.ExecuteAsync(42, output: "json", expectBench: captured.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), sync: true);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (changeBench) await benches.SetCurrentAsync(replacement.Id);
                else currentBinding = capturedBinding with { Identity = capturedBinding.Identity with { IdentityId = "replacement-actor" } };
            }
            finally { response.TrySetResult((CreateWorkItem(42, "Synced private detail"), Array.Empty<WorkItemLink>())); }
            return await read;
        });
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        using var error = JsonDocument.Parse(stderr.ToString());
        error.RootElement.GetProperty("error").GetString()!.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FailedLinkPersistence_ReportsIncompleteSyncInsteadOfPublishingSuccess()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((CreateWorkItem(42, "Refreshed root"), (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>()));
        _links.SaveLinksAsync(42, Arg.Any<IReadOnlyList<WorkItemLink>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Link cache write failed")));
        var stderr = new StringWriter();
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, stderr).ExecuteAsync(42, output: "json", sync: true));
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        using var error = JsonDocument.Parse(stderr.ToString());
        var message = error.RootElement.GetProperty("error").GetString()!;
        message.ShouldContain("incomplete");
        message.ShouldContain("Link cache write failed");
        _adoService.ReceivedCalls().Select(call => call.GetMethodInfo().Name).ShouldBe(["FetchWithLinksAsync"]);
    }

    [Fact]
    public async Task CachedNonHtmlControlsCannotBecomeTerminalCommandsOrOscLinks()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        var item = CreateWorkItem(42, "Title \u001b[2J [red]literal[/]");
        item.State = "State \u001b]52;c;secret\a";
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>&#27;[31m <a href='https://example.test'>inert link</a></p>",
            ["Custom.Value"] = "Value \u001b[H \u0085",
        });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _links.GetLinksAsync(42, Arg.Any<CancellationToken>()).Returns([new WorkItemLink(42, 99, "Related \u001b[2J")]);
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter()).ExecuteAsync(42, width: 120, output: "json"));
        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        var ansi = json.RootElement.GetProperty("ansi").GetString()!;
        var plain = Regex.Replace(ansi, "\\x1b\\[[0-9;:]*m", "");
        plain.ShouldNotContain("\u001b");
        plain.ShouldNotContain("\a");
        plain.ShouldNotContain("\u0085");
        plain.ShouldContain("[red]literal[/]");
        plain.ShouldContain("https://example.test");
        item.Title.ShouldContain("\u001b");
        item.State.ShouldContain("\u001b");
        item.Fields["Custom.Value"]!.ShouldContain("\u001b");
    }

    private BenchDetailCommand CreateCommand(IBenchRepository benches, TextWriter stderr)
    {
        var ctx = new CommandContext(new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory, new HintEngine(new DisplayConfig { Hints = false }), _config, Stderr: stderr);
        return new(ctx, new CurrentBenchResolver(benches, new DefaultBenchSelectors(_iterationService)), benches,
            _workItemRepo, _fieldDefinitionStore, _authentication, _bindings, _paths, new RendererFactory(),
            _links, _pendingChangeStore, new StatusFieldConfigReader(_paths),
            new SyncCoordinatorFactory(_workItemRepo, _adoService, _protectedCacheWriter, _pendingChangeStore, _links, 30, 30),
            _processConfiguration, new SpectreTheme(_config.Display));
    }

    private static async Task<(int Exit, string Output)> CaptureAsync(Func<Task<int>> run)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try { Console.SetOut(output); return (await run(), output.ToString()); }
        finally { Console.SetOut(original); }
    }
}
