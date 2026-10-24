using System.Text.Json;
using System.Text.RegularExpressions;
using NSubstitute;
using Shouldly;
using Twig.Browser;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
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

public sealed partial class BenchDetailCommandTests : RefreshCommandTestBase
{
    private readonly IAuthenticationProvider _authentication = Substitute.For<IAuthenticationProvider, IConnectionOperationGuard>();
    private readonly IConnectionBindingService _bindings = Substitute.For<IConnectionBindingService>();
    private readonly IWorkItemLinkRepository _links = Substitute.For<IWorkItemLinkRepository>();
    private readonly IProcessConfigurationProvider _processConfiguration = Substitute.For<IProcessConfigurationProvider>();
    private readonly IFormLayoutProvider _formLayouts = Substitute.For<IFormLayoutProvider>();

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
        _formLayouts.GetFormLayoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FormLayoutResult.Unavailable());
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
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter(), _formLayouts).ExecuteAsync(42, width: width, output: "json",
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
        _formLayouts.ReceivedCalls().ShouldBeEmpty();
        await _iterationService.DidNotReceive().GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Document_ExportsQualifiedCachedBaselineInsteadOfAnsi()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        var item = CreateWorkItem(42, "Semantic detail");
        item.MarkSynced(17);
        item.ImportFields(new Dictionary<string, string?> { ["Custom.Body"] = "<p>" + new string('x', 500) + " tail</p>" });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter())
            .ExecuteAsync(42, output: "json", document: true));
        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        root.GetProperty("version").GetInt32().ShouldBe(2);
        root.TryGetProperty("ansi", out _).ShouldBeFalse();
        root.GetProperty("itemKey").GetProperty("workItemId").GetInt32().ShouldBe(42);
        root.GetProperty("itemKey").GetProperty("connectionId").GetString().ShouldBe("connection");
        root.GetProperty("itemKey").GetProperty("bindingId").GetString().ShouldBe("binding");
        root.GetProperty("itemKey").GetProperty("checkoutRoot").GetString().ShouldBe(_testDir);
        root.GetProperty("itemKey").TryGetProperty("revision", out _).ShouldBeFalse();
        root.GetProperty("item").GetProperty("revision").GetInt32().ShouldBe(17);
        root.GetProperty("item").GetProperty("fields").GetProperty("Custom.Body").GetString().ShouldEndWith(" tail</p>");
        root.GetProperty("document").GetProperty("revision").GetInt32().ShouldBe(17);
        root.GetProperty("layoutStatus").GetString().ShouldBe("unavailable");
        root.GetProperty("layoutSource").GetString().ShouldBe("twig-fallback");
        root.GetProperty("document").GetProperty("processId").GetString().ShouldBe("twig.fallback");
        root.GetProperty("editing").GetString().ShouldBe("unavailable");
        root.GetProperty("relatedItemsCoverage").GetString().ShouldBe("not-exported");
        output.ShouldNotContain("credential");
        _adoService.ReceivedCalls().ShouldBeEmpty();
        _contextStore.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Document_ServedLayoutPreservesNativeStructureValuesAndAppearance()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        var item = new WorkItem { Id = 42, Title = "Local title", Type = WorkItemType.Parse("Delivery slice").Value };
        item.MarkSynced(17);
        item.SetDirty();
        var body = "<p>" + new string('x', 500) + " tail</p>";
        item.ImportFields(new Dictionary<string, string?> { ["Custom.Body"] = body });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns([
            new FieldDefinition("Custom.Empty", "Empty", "string", false),
            new FieldDefinition("Custom.Boolean", "Boolean", "boolean", false),
        ]);
        var layout = new FormLayout("Custom.DeliverySlice", "process-from-provider", [
            new("details", "Details", "custom", false, false, [
                new("left", [new("values", "Values", true, false, [
                    new("Custom.Body", "Body", "HtmlFieldControl", false, true, false),
                    new("Custom.Empty", "Empty", "FieldControl", false, true, false),
                    new("Custom.Boolean", "Boolean", "FieldControl", false, true, false),
                ])]),
                new("right", [new("extension", "Extension", false, true, [
                    new("contribution.id", "Contribution", "CustomControl", true, false, true),
                ])]),
            ]),
            new("history", "History", "history", true, false, []),
        ])
        { SystemControls = [new("System.Title", "Title", "FieldControl", false, true, false)] };
        _formLayouts.GetFormLayoutAsync("Delivery slice", Arg.Any<CancellationToken>())
            .Returns(new FormLayoutResult.Served(layout));
        _config.TypeAppearances = [new() { Name = "Delivery slice", IconId = "icon_from_metadata", Color = "123456" }];
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter(), _formLayouts)
            .ExecuteAsync(42, width: 1, output: "json", document: true));
        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        root.GetProperty("layoutStatus").GetString().ShouldBe("served");
        root.GetProperty("layoutSource").GetString().ShouldBe("server");
        root.GetProperty("hasLocalChanges").GetBoolean().ShouldBeTrue();
        root.GetProperty("item").GetProperty("revision").GetInt32().ShouldBe(17);
        var document = root.GetProperty("document");
        document.GetProperty("workItemTypeReferenceName").GetString().ShouldBe("Custom.DeliverySlice");
        document.GetProperty("processId").GetString().ShouldBe("process-from-provider");
        document.GetProperty("systemControls")[0].GetProperty("value").GetProperty("full").GetString().ShouldBe("Local title");
        var pages = document.GetProperty("pages");
        pages.GetArrayLength().ShouldBe(2);
        pages[0].GetProperty("visible").GetBoolean().ShouldBeFalse();
        pages[1].GetProperty("pageType").GetString().ShouldBe("history");
        var sections = pages[0].GetProperty("sections");
        sections[0].GetProperty("id").GetString().ShouldBe("left");
        sections[1].GetProperty("id").GetString().ShouldBe("right");
        var controls = sections[0].GetProperty("groups")[0].GetProperty("controls");
        controls[0].GetProperty("value").GetProperty("state").GetInt32().ShouldBe((int)DetailFieldState.HasValue);
        controls[0].GetProperty("value").GetProperty("full").GetString().ShouldBe(body);
        controls[1].GetProperty("value").GetProperty("state").GetInt32().ShouldBe((int)DetailFieldState.EmptyOnServer);
        controls[2].GetProperty("value").GetProperty("state").GetInt32().ShouldBe((int)DetailFieldState.NotCarriedByTwig);
        var contribution = sections[1].GetProperty("groups")[0].GetProperty("controls")[0];
        contribution.GetProperty("isContribution").GetBoolean().ShouldBeTrue();
        contribution.TryGetProperty("value", out _).ShouldBeFalse();
        root.GetProperty("appearance").GetProperty("iconId").GetString().ShouldBe("icon_from_metadata");
        root.GetProperty("appearance").GetProperty("color").GetString().ShouldBe("123456");
        root.GetProperty("iconAssetAuthority").GetString().ShouldBe("unavailable");
        root.GetProperty("editing").GetString().ShouldBe("unavailable");
        _adoService.ReceivedCalls().ShouldBeEmpty();
        _contextStore.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("served")]
    [InlineData("locked")]
    [InlineData("unavailable")]
    public async Task Document_EmptyServedLockedAndUnavailableAreDistinct(string status)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));
        FormLayoutResult result = status switch
        {
            "served" => new FormLayoutResult.Served(new("Custom.Empty", "actual-process", [])),
            "locked" => new FormLayoutResult.Locked("Custom.Locked"),
            _ => new FormLayoutResult.Unavailable(),
        };
        _formLayouts.GetFormLayoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(result);
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter(), _formLayouts)
            .ExecuteAsync(42, output: "json-compact", document: true));
        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        root.GetProperty("layoutStatus").GetString().ShouldBe(status);
        root.GetProperty("layoutSource").GetString().ShouldBe(status == "served" ? "server" : "twig-fallback");
        root.GetProperty("document").GetProperty("pages").GetArrayLength().ShouldBe(status == "served" ? 0 : 1);
        if (status == "locked") root.GetProperty("lockedTypeReferenceName").GetString().ShouldBe("Custom.Locked");
        else root.TryGetProperty("lockedTypeReferenceName", out _).ShouldBeFalse();
        root.GetProperty("appearanceSource").GetString().ShouldBe("unavailable");
        root.TryGetProperty("appearance", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Document_RevisionDriftRetainsStableKeyButChangesBaseline()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        var item = CreateWorkItem(42, "Item");
        item.MarkSynced(17);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        var command = CreateCommand(benches, new StringWriter());
        var (firstExit, firstOutput) = await CaptureAsync(() => command.ExecuteAsync(42, output: "json-full", document: true));
        firstExit.ShouldBe(0);
        using var first = JsonDocument.Parse(firstOutput);
        item.MarkSynced(18);
        var (nextExit, nextOutput) = await CaptureAsync(() => command.ExecuteAsync(42, output: "json-full", document: true));
        nextExit.ShouldBe(0);
        using var next = JsonDocument.Parse(nextOutput);
        first.RootElement.GetProperty("itemKey").GetRawText().ShouldBe(next.RootElement.GetProperty("itemKey").GetRawText());
        first.RootElement.GetProperty("item").GetProperty("revision").GetInt32().ShouldBe(17);
        next.RootElement.GetProperty("item").GetProperty("revision").GetInt32().ShouldBe(18);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Document_OriginOrBenchChangeDuringLayoutRefusesAllOutput(bool changeBench)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var captured = (await benches.CreateAsync("captured"))!;
        var replacement = (await benches.CreateAsync("replacement"))!;
        await benches.SetCurrentAsync(captured.Id);
        var binding = await _bindings.ResolveAsync(_config, _paths);
        _bindings.ResolveAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>()).Returns(_ => binding);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Private item"));
        _formLayouts.GetFormLayoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (changeBench) await benches.SetCurrentAsync(replacement.Id);
            else binding = binding with { Identity = binding.Identity with { IdentityId = "different-actor" } };
            return (FormLayoutResult)new FormLayoutResult.Served(new("Custom.Type", "process", []));
        });
        var stderr = new StringWriter();
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, stderr, _formLayouts)
            .ExecuteAsync(42, output: "json", document: true));
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        using var error = JsonDocument.Parse(stderr.ToString());
        error.RootElement.GetProperty("error").GetString()!.ShouldContain("changed");
    }

    [Fact]
    public async Task Document_ProviderFailureIsNotReportedAsSuccessfulFallback()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));
        _formLayouts.GetFormLayoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<FormLayoutResult>(new IOException("Layout read failed")));
        var stderr = new StringWriter();
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, stderr, _formLayouts)
            .ExecuteAsync(42, output: "json", document: true));
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        using var error = JsonDocument.Parse(stderr.ToString());
        error.RootElement.GetProperty("error").GetString().ShouldBe("Layout read failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Document_SeedDoesNotFetchServerLayoutOrPublish(bool sync)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        _workItemRepo.GetByIdAsync(-3, Arg.Any<CancellationToken>())
            .Returns(new WorkItem { Id = -3, Title = "Seed", Type = WorkItemType.Parse("Delivery slice").Value, IsSeed = true });
        var stderr = new StringWriter();
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, stderr, _formLayouts)
            .ExecuteAsync(-3, output: "json", sync: sync, document: true));
        exit.ShouldBe(sync ? 2 : 0);
        if (sync) output.ShouldBeEmpty();
        else
        {
            using var json = JsonDocument.Parse(output);
            json.RootElement.GetProperty("item").GetProperty("isSeed").GetBoolean().ShouldBeTrue();
            json.RootElement.GetProperty("layoutStatus").GetString().ShouldBe("unavailable");
        }
        _formLayouts.ReceivedCalls().ShouldBeEmpty();
        _adoService.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Document_CancellationDuringLayoutNeverEmitsAnObservation()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));
        using var cancellation = new CancellationTokenSource();
        _formLayouts.GetFormLayoutAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<FormLayoutResult>(cancellation.Token);
        });
        var stderr = new StringWriter();
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateCommand(benches, stderr, _formLayouts)
                .ExecuteAsync(42, output: "json", ct: cancellation.Token, document: true));
        }
        finally { Console.SetOut(original); }
        output.ToString().ShouldBeEmpty();
        stderr.ToString().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("human")]
    [InlineData("minimal")]
    [InlineData("ids")]
    public async Task Document_NonJsonRefusesBeforeAnyNativeRead(string format)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var stderr = new StringWriter();
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, stderr, _formLayouts)
            .ExecuteAsync(42, output: format, document: true));
        exit.ShouldBe(2);
        output.ShouldBeEmpty();
        stderr.ToString().ShouldNotBeNullOrWhiteSpace();
        _workItemRepo.ReceivedCalls().ShouldBeEmpty();
        _formLayouts.ReceivedCalls().ShouldBeEmpty();
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
        error.RootElement.GetProperty("error").GetString()!.ShouldNotBeNullOrWhiteSpace();
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
        error.RootElement.GetProperty("error").GetString()!.ShouldNotBeNullOrWhiteSpace();
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitSync_CanRetrieveCacheMissButNeverMaterializesLinkedTargetsOrChildren(bool document)
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
        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter()).ExecuteAsync(42, output: "json", sync: true, document: document));
        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        if (document)
        {
            json.RootElement.GetProperty("item").GetProperty("fields").GetProperty("System.Description")
                .GetString().ShouldBe("<p>Fetched description</p>");
            json.RootElement.GetProperty("relatedItemsCoverage").GetString().ShouldBe("not-exported");
        }
        else
        {
            var ansi = json.RootElement.GetProperty("ansi").GetString()!;
            ansi.ShouldContain("Fetched description");
            ansi.ShouldContain("Cached child");
            ansi.ShouldContain("#99");
        }
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
        error.RootElement.GetProperty("error").GetString()!.ShouldNotBeNullOrWhiteSpace();
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
        message.ShouldNotBeNullOrWhiteSpace();
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

    private BenchDetailCommand CreateCommand(IBenchRepository benches, TextWriter stderr, IFormLayoutProvider? formLayouts = null)
    {
        var ctx = new CommandContext(new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory, new HintEngine(new DisplayConfig { Hints = false }), _config, Stderr: stderr);
        var operations = new BrowserOperationModule(ctx, new CurrentBenchResolver(benches, new DefaultBenchSelectors(_iterationService)),
            _workItemRepo, _authentication, _bindings, _paths);
        var module = new BrowserReadModule(ctx, operations, _workItemRepo, _fieldDefinitionStore,
            _links, _pendingChangeStore, new StatusFieldConfigReader(_paths),
            new SyncCoordinatorFactory(_workItemRepo, _adoService, _protectedCacheWriter, _pendingChangeStore, _links, 30, 30),
            _processConfiguration, new SpectreTheme(_config.Display),
            new BenchEvaluator(_workItemRepo, Substitute.For<IIterationCalendar>(), _pendingChangeStore), formLayouts);
        return new(ctx, module, new RendererFactory());
    }

    private static async Task<(int Exit, string Output)> CaptureAsync(Func<Task<int>> run)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try { Console.SetOut(output); return (await run(), output.ToString()); }
        finally { Console.SetOut(original); }
    }
}
