using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ShowCommand_ColdReadTests : IDisposable
{
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly SqliteWorkItemRepository _items;
    private readonly SqliteWorkItemLinkRepository _links;
    private readonly SqlitePendingChangeStore _pending;
    private readonly IAdoWorkItemService _ado = Substitute.For<IAdoWorkItemService>();
    private readonly IContextStore _context = Substitute.For<IContextStore>();
    private readonly ITelemetryClient _telemetry = Substitute.For<ITelemetryClient>();
    private readonly StringWriter _stderr = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "twig-cold-show-" + Guid.NewGuid().ToString("N"));

    public ShowCommand_ColdReadTests()
    {
        _items = new(_store, new WorkItemMapper());
        _links = new(_store);
        _pending = new(_store);
        Directory.CreateDirectory(_temp);
    }

    private ShowCommand Command(TestConsole? console = null)
    {
        var formatter = new OutputFormatterFactory(new HumanOutputFormatter());
        var renderer = console is null ? null : new SpectreRenderer(console, new SpectreTheme(new DisplayConfig())) { SyncStatusDelay = TimeSpan.Zero };
        var ctx = new CommandContext(new RenderingPipelineFactory(formatter, renderer!, isOutputRedirected: () => console is null),
            formatter, new HintEngine(new DisplayConfig { Hints = false }), new TwigConfiguration(), Stderr: _stderr, TelemetryClient: _telemetry);
        return new ShowCommand(ctx, _items, _links,
            new SyncCoordinatorFactory(_items, _ado, new ProtectedCacheWriter(_items, _pending), _pending, _links, 30, 30),
            new StatusFieldConfigReader(new TwigPaths(_temp, Path.Combine(_temp, "config"), Path.Combine(_temp, "twig.db"))),
            contextStore: _context, activeItemResolver: new ActiveItemResolver(_context, _items, _ado), pendingChangeStore: _pending);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(99, false)]
    [InlineData(99, true)]
    public async Task RefreshFetchesExplicitItemAndLinksWithoutChangingSelection(int? active, bool cached)
    {
        _context.GetActiveWorkItemIdAsync().Returns(active);
        if (cached) await _items.SaveAsync(new WorkItemBuilder(42, "Old title").Build());
        var fresh = new WorkItemBuilder(42, "Remote title").Build();
        _ado.FetchWithLinksAsync(42, Arg.Any<CancellationToken>()).Returns((fresh, new[] { new WorkItemLink(42, 77, "Predecessor") }));
        _ado.FetchAsync(77, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(77, "Dependency").Build());

        var (exit, stdout) = await Capture(() => Command().ExecuteAsync(42, "json", refresh: true));

        exit.ShouldBe(0);
        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("id").GetInt32().ShouldBe(42);
        doc.RootElement.GetProperty("title").GetString().ShouldBe("Remote title");
        doc.RootElement.GetProperty("links")[0].GetProperty("targetId").GetInt32().ShouldBe(77);
        doc.RootElement.GetProperty("linksVerifiedAt").ValueKind.ShouldBe(JsonValueKind.String);
        (await _context.GetActiveWorkItemIdAsync()).ShouldBe(active);
        await _context.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _ado.Received(1).FetchWithLinksAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ColdHumanRefreshRendersFetchedItem()
    {
        _ado.FetchWithLinksAsync(42, Arg.Any<CancellationToken>()).Returns((new WorkItemBuilder(42, "Fresh human item").Build(), Array.Empty<WorkItemLink>()));
        var console = new TestConsole();
        var exit = await Command(console).ExecuteAsync(42, "human", refresh: true);
        exit.ShouldBe(0);
        console.Output.ShouldContain("Fresh human item");
        await _ado.Received(1).FetchWithLinksAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DefaultMissDoesNotFetchOrChangeSelection()
    {
        var (exit, stdout) = await Capture(() => Command().ExecuteAsync(42, "json"));
        exit.ShouldBe(1);
        stdout.ShouldBeEmpty();
        using var error = JsonDocument.Parse(_stderr.ToString());
        error.RootElement.GetProperty("error").GetString()!.ShouldContain("local cache");
        _ado.ReceivedCalls().ShouldBeEmpty();
        _context.ReceivedCalls().ShouldBeEmpty();
        _telemetry.Received().TrackEvent("CommandExecuted",
            Arg.Is<Dictionary<string, string>>(p => p["command"] == "show" && p["exit_code"] == "1"),
            Arg.Any<Dictionary<string, double>>());
    }

    [Fact]
    public async Task ActiveRefreshFetchesRootWithLinksAndRendersRemoteTitle()
    {
        _context.GetActiveWorkItemIdAsync().Returns(42);
        await _items.SaveAsync(new WorkItemBuilder(42, "Cached").Build());
        _ado.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((new WorkItemBuilder(42, "Remote active").Build(), Array.Empty<WorkItemLink>()));
        var (exit, stdout) = await Capture(() => Command().ExecuteAsync(outputFormat: "json", refresh: true));
        exit.ShouldBe(0);
        using var document = JsonDocument.Parse(stdout);
        document.RootElement.GetProperty("title").GetString().ShouldBe("Remote active");
        document.RootElement.GetProperty("linksVerifiedAt").ValueKind.ShouldBe(JsonValueKind.String);
        await _ado.Received(1).FetchWithLinksAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshPreservesDirtyRootUnrelatedPendingAndSeed()
    {
        await _items.SaveAsync(new WorkItemBuilder(42, "Local title").Dirty().Build());
        await _items.SaveAsync(new WorkItemBuilder(-1, "Draft seed").AsSeed().Build());
        await _pending.AddChangeAsync(42, "field", "System.Title", "Original", "Local title");
        await _pending.AddChangeAsync(99, "note", null, null, "Unrelated note");
        _ado.FetchWithLinksAsync(42, Arg.Any<CancellationToken>()).Returns((new WorkItemBuilder(42, "Remote title").Build(), Array.Empty<WorkItemLink>()));

        var (exit, stdout) = await Capture(() => Command().ExecuteAsync(42, "json", refresh: true));

        exit.ShouldBe(0);
        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("title").GetString().ShouldBe("Local title");
        (await _items.GetByIdAsync(42))!.IsDirty.ShouldBeTrue();
        (await _pending.GetChangesAsync(42)).ShouldHaveSingleItem().NewValue.ShouldBe("Local title");
        (await _pending.GetChangesAsync(99)).ShouldHaveSingleItem().NewValue.ShouldBe("Unrelated note");
        (await _items.GetByIdAsync(-1))!.Title.ShouldBe("Draft seed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreachableRootFailsHonestlyWithoutEvictingLocalWork(bool cached)
    {
        if (cached) await _items.SaveAsync(new WorkItemBuilder(42, "Local title").Dirty().Build());
        await _pending.AddChangeAsync(42, "note", null, null, "Keep this note");
        _ado.FetchWithLinksAsync(42, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("Work item 42 not found or inaccessible"));

        var (exit, stdout) = await Capture(() => Command().ExecuteAsync(42, "json", refresh: true));

        exit.ShouldBe(1);
        stdout.ShouldBeEmpty();
        using var error = JsonDocument.Parse(_stderr.ToString());
        error.RootElement.GetProperty("error").GetString()!.ShouldContain("not found or inaccessible");
        (await _pending.GetChangesAsync(42)).ShouldHaveSingleItem().NewValue.ShouldBe("Keep this note");
        if (cached) (await _items.GetByIdAsync(42))!.Title.ShouldBe("Local title");
        await _ado.DidNotReceive().FetchAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PartialLinksAreNotReportedAsSuccessfulRefreshOrDiscardedPending()
    {
        await _items.SaveAsync(new WorkItemBuilder(77, "Local dependency").Dirty().Build());
        await _pending.AddChangeAsync(77, "note", null, null, "Pending dependency note");
        _ado.FetchWithLinksAsync(42, Arg.Any<CancellationToken>()).Returns((new WorkItemBuilder(42, "Root").Build(), new[] { new WorkItemLink(42, 77, "Predecessor") }));
        _ado.FetchAsync(77, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("Work item 77 not found"));

        var (exit, stdout) = await Capture(() => Command().ExecuteAsync(42, "json", refresh: true));

        exit.ShouldBe(1);
        stdout.ShouldBeEmpty();
        using var error = JsonDocument.Parse(_stderr.ToString());
        error.RootElement.GetProperty("error").GetString()!.ShouldContain("77");
        (await _pending.GetChangesAsync(77)).ShouldHaveSingleItem().NewValue.ShouldBe("Pending dependency note");
        (await _items.GetByIdAsync(77))!.Title.ShouldBe("Local dependency");
        (await _links.GetLinksAsync(42)).ShouldHaveSingleItem().TargetId.ShouldBe(77);
    }

    [Theory]
    [InlineData("Authentication required")]
    [InlineData("Network unavailable")]
    public async Task RemoteFailureRetainsItsCauseRatherThanBecomingCacheAbsence(string cause)
    {
        _ado.FetchWithLinksAsync(42, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException(cause));
        var (exit, stdout) = await Capture(() => Command().ExecuteAsync(42, "json", refresh: true));
        exit.ShouldBe(1);
        stdout.ShouldBeEmpty();
        using var error = JsonDocument.Parse(_stderr.ToString());
        error.RootElement.GetProperty("error").GetString()!.ShouldContain(cause);
    }

    [Fact]
    public async Task SeedRefreshDoesNotContactAdo()
    {
        await _items.SaveAsync(new WorkItemBuilder(-1, "Local seed").AsSeed().Build());
        var (exit, stdout) = await Capture(() => Command().ExecuteAsync(-1, "json", refresh: true));
        exit.ShouldBe(0);
        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("id").GetInt32().ShouldBe(-1);
        _ado.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationPropagatesWithoutFallbackOrPendingMutation()
    {
        using var cts = new CancellationTokenSource();
        _ado.FetchWithLinksAsync(42, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cts.Cancel();
            return Task.FromException<(WorkItem, IReadOnlyList<WorkItemLink>)>(new OperationCanceledException(cts.Token));
        });
        await Should.ThrowAsync<OperationCanceledException>(() => Command().ExecuteAsync(42, "json", refresh: true, ct: cts.Token));
        _stderr.ToString().ShouldBeEmpty();
        await _ado.DidNotReceive().FetchAsync(42, Arg.Any<CancellationToken>());
    }

    private static async Task<(int Exit, string Stdout)> Capture(Func<Task<int>> action)
    {
        var original = Console.Out;
        using var stdout = new StringWriter();
        Console.SetOut(stdout);
        try { return (await action(), stdout.ToString()); }
        finally { Console.SetOut(original); }
    }

    public void Dispose()
    {
        _store.Dispose();
        _stderr.Dispose();
        Directory.Delete(_temp, true);
    }
}
