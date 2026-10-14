using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Browser;

// These scenarios qualify application behavior, not an OS terminal or installed host.
[Trait("Category", "HostInventory")]
public sealed class BrowserApplicationScenarioTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [HostMigrationFact]
    public async Task EnterEscapeViewsResizeAndCacheRefreshConserveDurableWorkAndFoldedViewport()
    {
        var observer = await PopulateAsync();
        var benches = observer.GetRequiredService<IBenchRepository>();
        var bench = (await benches.GetCurrentAsync())!;
        for (var id = 81; id <= 88; id++)
        {
            await observer.GetRequiredService<IWorkItemRepository>().SaveAsync(
                new WorkItemBuilder(id, $"Viewport row {id}").InState("To Do").Build());
            await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(id));
        }
        var application = Application();
        using var terminal = new ScriptedBrowserTerminal(100, 120);
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = application.RunAsync(terminal, _fixture.Paths.StartDir!, ct: lifetime.Token);
        try
        {
            var tree = await ReadyAsync(terminal, "Selected cached item", deadline.Token);
            tree.Content.ShouldContain("Child cached item");
            var original = await CaptureStoredRowsAsync(observer);

            terminal.Enqueue(Key(BrowserKey.Left));
            var folded = await terminal.ReadUntilAsync(paint => !paint.Working && !paint.Content.Contains("Child cached item", StringComparison.Ordinal), deadline.Token);
            folded.Content.ShouldContain("Selected cached item");
            terminal.Enqueue(Key(BrowserKey.Enter));
            var detail = await ReadyAsync(terminal, "DETAIL42-ORIGINAL", deadline.Token);
            string.Join("\n", detail.VisibleRows).ShouldContain("DETAIL42-ORIGINAL");

            terminal.Resize(64, 120);
            var compact = await terminal.ReadUntilAsync(paint => paint.Width == 64 && !paint.Working, deadline.Token);
            compact.Content.ShouldContain("DETAIL42-ORIGINAL");
            terminal.Resize(100, 120);
            await terminal.ReadUntilAsync(paint => paint.Width == 100 && !paint.Working, deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Escape));
            var restored = await terminal.ReadUntilAsync(paint => !paint.Working && !paint.Content.Contains("DETAIL42-ORIGINAL", StringComparison.Ordinal), deadline.Token);
            restored.Rows.ShouldBe(folded.Rows);
            restored.Top.ShouldBe(folded.Top);


            terminal.Resize(100, 6);
            await terminal.ReadUntilAsync(paint => paint.Height == 6 && !paint.Working, deadline.Token);
            terminal.Enqueue(Key(BrowserKey.End));
            var scrolled = await terminal.ReadUntilAsync(paint => paint.Top > 0 && !paint.Working, deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Enter));
            await ReadyAsync(terminal, "DETAIL42-ORIGINAL", deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Home));
            await terminal.ReadUntilAsync(paint => paint.Top == 0 && paint.Content.Contains("DETAIL42-ORIGINAL", StringComparison.Ordinal), deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Escape));
            var scrolledRestored = await terminal.ReadUntilAsync(paint => !paint.Content.Contains("DETAIL42-ORIGINAL", StringComparison.Ordinal), deadline.Token);
            scrolledRestored.Rows.ShouldBe(scrolled.Rows);
            scrolledRestored.Top.ShouldBe(scrolled.Top);
            scrolledRestored.VisibleRows.ShouldBe(scrolled.VisibleRows);
            ClickFooter(terminal, scrolledRestored, "[b Bench]");
            var configuration = await terminal.ReadUntilAsync(paint => paint.Context.Contains("BENCH CONFIGURATION", StringComparison.Ordinal), deadline.Token);
            configuration.Content.ShouldContain("[1 Pins]");
            configuration.Content.ShouldContain("[2 Areas]");
            configuration.Content.ShouldContain("[3 Sprints]");
            configuration.Content.ShouldContain("[4 Benches]");
            ClickFooter(terminal, configuration, "[i ID]");
            var manualPin = await terminal.ReadUntilAsync(paint => paint.Status.Contains("[Esc Cancel]", StringComparison.Ordinal), deadline.Token);
            string.Join("\n", manualPin.VisibleRows).ShouldContain("> ▏");
            terminal.Enqueue(Character('9'), Key(BrowserKey.Escape));
            await terminal.ReadUntilAsync(paint => paint.Sequence > manualPin.Sequence && !paint.Status.Contains("[Esc Cancel]", StringComparison.Ordinal), deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Escape));
            var configurationRestored = await terminal.ReadUntilAsync(paint => paint.Context.StartsWith("TREE", StringComparison.Ordinal), deadline.Token);
            configurationRestored.Rows.ShouldBe(scrolled.Rows);
            configurationRestored.Top.ShouldBe(scrolled.Top);
            terminal.Enqueue(Character('c'));
            await terminal.ReadUntilAsync(paint => paint.Context.Contains("BENCH CONFIGURATION", StringComparison.Ordinal), deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Escape));
            var aliasRestored = await terminal.ReadUntilAsync(paint => paint.Context.StartsWith("TREE", StringComparison.Ordinal), deadline.Token);
            aliasRestored.Top.ShouldBe(scrolled.Top);
            terminal.Resize(100, 120);
            await terminal.ReadUntilAsync(paint => paint.Height == 120 && !paint.Working, deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Tab));
            var table = await terminal.ReadUntilAsync(paint => !paint.Working && paint.Context.Contains("TABLE", StringComparison.Ordinal), deadline.Token);
            table.Content.ShouldContain("TYPE");
            table.Content.ShouldContain("Child cached item");
            terminal.Enqueue(Character('1'));
            var again = await terminal.ReadUntilAsync(paint => !paint.Working && paint.Context.Contains("TREE", StringComparison.Ordinal), deadline.Token);
            again.Rows.ShouldBe(folded.Rows);
            terminal.Enqueue(Character('2'));
            await terminal.ReadUntilAsync(paint => !paint.Working && paint.Context.Contains("TABLE", StringComparison.Ordinal), deadline.Token);
            (await CaptureStoredRowsAsync(observer)).ShouldBe(original);

            terminal.Enqueue(Key(BrowserKey.Enter));
            await ReadyAsync(terminal, "DETAIL42-ORIGINAL", deadline.Token);
            // The only permitted durable delta is this explicit external cache write.
            var repository = observer.GetRequiredService<IWorkItemRepository>();
            var replacement = (await repository.GetByIdAsync(42))!;
            replacement.ImportFields(new Dictionary<string, string?> { ["System.Description"] = "<p>DETAIL42-REPLACEMENT</p>" });
            await repository.SaveAsync(replacement);
            var refreshedBaseline = await CaptureStoredRowsAsync(observer);
            terminal.Enqueue(Character('R'));
            var refreshed = await ReadyAsync(terminal, "DETAIL42-REPLACEMENT", deadline.Token);
            refreshed.Content.ShouldNotContain("DETAIL42-ORIGINAL");
            (await CaptureStoredRowsAsync(observer)).ShouldBe(refreshedBaseline);
            terminal.Enqueue(Character('q'));
            (await running.WaitAsync(deadline.Token)).ShouldBe(0);
            (await CaptureStoredRowsAsync(observer)).ShouldBe(refreshedBaseline);
            _fixture.Transport.RequestedItems.ShouldBeEmpty();
            _fixture.Transport.Mutations.ShouldBe(0);
        }
        finally
        {
            lifetime.Cancel();
            terminal.Enqueue(new BrowserInput(BrowserInputKind.EndOfInput));
            await running.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [HostMigrationFact]
    public async Task ViewSwitchKeepsSelectedItemVisibleAfterLongWrappedPredecessor()
    {
        var observer = await PopulateAsync();
        await observer.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(20, new string('x', 800)));
        var benches = observer.GetRequiredService<IBenchRepository>();
        await benches.AddSelectorAsync((await benches.GetCurrentAsync())!.Id, BenchSelector.ForItem(20));
        var baseline = await CaptureStoredRowsAsync(observer);
        var application = Application();
        using var terminal = new ScriptedBrowserTerminal(100, 10);
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = application.RunAsync(terminal, _fixture.Paths.StartDir!, ct: lifetime.Token);
        try
        {
            await ReadyAsync(terminal, "Selected cached item", deadline.Token);
            terminal.Enqueue(Character('j'));
            await terminal.ReadUntilAsync(paint => !paint.Working && paint.VisibleRows.Any(row => row.Contains("#42", StringComparison.Ordinal)), deadline.Token);

            terminal.Enqueue(Key(BrowserKey.Tab));
            var table = await terminal.ReadUntilAsync(paint => !paint.Working && paint.Context.StartsWith("TABLE", StringComparison.Ordinal), deadline.Token);

            string.Join("\n", table.VisibleRows).ShouldContain("#42");
            (await CaptureStoredRowsAsync(observer)).ShouldBe(baseline);
            _fixture.Transport.RequestedItems.ShouldBeEmpty();
            _fixture.Transport.Mutations.ShouldBe(0);
        }
        finally
        {
            lifetime.Cancel();
            terminal.Enqueue(new BrowserInput(BrowserInputKind.EndOfInput));
            await running.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [HostMigrationFact]
    public async Task HierarchyArrowsAndVisibleFooterActionsPreserveIndependentPinsAndHelpViewport()
    {
        var observer = await PopulateAsync();
        var benches = observer.GetRequiredService<IBenchRepository>();
        var bench = (await benches.GetCurrentAsync())!;
        var pending = await CaptureStoredRowsAsync(observer, durableOnly: true);
        var application = Application();
        using var terminal = new ScriptedBrowserTerminal(140, 14);
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = application.RunAsync(terminal, _fixture.Paths.StartDir!, ct: lifetime.Token);
        try
        {
            await ReadyAsync(terminal, "Selected cached item", deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Right));
            var child = await terminal.ReadUntilAsync(paint => !paint.Working && paint.Status.Contains("[P Pin subtree]", StringComparison.Ordinal), deadline.Token);
            ClickFooter(terminal, child, "[? Help]");
            await terminal.ReadUntilAsync(paint => paint.Context.StartsWith("BROWSER HELP", StringComparison.Ordinal), deadline.Token);
            terminal.Enqueue(Key(BrowserKey.End));
            await terminal.ReadUntilAsync(paint => paint.Context.StartsWith("BROWSER HELP", StringComparison.Ordinal) && paint.Top > 0, deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Escape));
            var helpRestored = await terminal.ReadUntilAsync(paint => paint.Context.StartsWith("TREE", StringComparison.Ordinal), deadline.Token);
            helpRestored.Rows.ShouldBe(child.Rows);
            helpRestored.Top.ShouldBe(child.Top);
            ClickContext(terminal, helpRestored, "[2 Table]");
            var table = await terminal.ReadUntilAsync(paint => paint.Context.StartsWith("TABLE", StringComparison.Ordinal), deadline.Token);
            ClickContext(terminal, table, "[1 Tree]");
            var tree = await terminal.ReadUntilAsync(paint => paint.Context.StartsWith("TREE", StringComparison.Ordinal), deadline.Token);
            tree.Rows.ShouldBe(child.Rows);
            ClickFooter(terminal, tree, "[p Pin single]");
            var pinned = await terminal.ReadUntilAsync(paint => !paint.Working && paint.Status.Contains("[p Unpin single]", StringComparison.Ordinal), deadline.Token);
            var selectors = (await benches.GetByNameAsync(bench.Name))!.Selectors;
            selectors.ShouldContain(BenchSelector.ForItem(43));
            selectors.ShouldContain(BenchSelector.ForSubtree(42));
            ClickFooter(terminal, pinned, "[p Unpin single]");
            await terminal.ReadUntilAsync(paint => !paint.Working && paint.Status.Contains("[p Pin single]", StringComparison.Ordinal), deadline.Token);
            selectors = (await benches.GetByNameAsync(bench.Name))!.Selectors;
            selectors.ShouldNotContain(BenchSelector.ForItem(43));
            selectors.ShouldContain(BenchSelector.ForSubtree(42));
            terminal.Enqueue(Key(BrowserKey.Enter));
            await ReadyAsync(terminal, "DETAIL43-NEWER", deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Escape));
            await terminal.ReadUntilAsync(paint => !paint.Content.Contains("DETAIL43-NEWER", StringComparison.Ordinal), deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Left), Key(BrowserKey.Enter));
            await ReadyAsync(terminal, "DETAIL42-ORIGINAL", deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Escape));
            await terminal.ReadUntilAsync(paint => !paint.Content.Contains("DETAIL42-ORIGINAL", StringComparison.Ordinal), deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Left));
            await terminal.ReadUntilAsync(paint => !paint.Content.Contains("Child cached item", StringComparison.Ordinal), deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Right));
            await ReadyAsync(terminal, "Child cached item", deadline.Token);
            (await CaptureStoredRowsAsync(observer, durableOnly: true)).ShouldBe(pending);
            terminal.Enqueue(Character('q'));
            (await running.WaitAsync(deadline.Token)).ShouldBe(0);
            _fixture.Transport.RequestedItems.ShouldBeEmpty();
            _fixture.Transport.Mutations.ShouldBe(0);
        }
        finally
        {
            lifetime.Cancel();
            terminal.Enqueue(new BrowserInput(BrowserInputKind.EndOfInput));
            await running.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [HostMigrationFact]
    public async Task ExpandedDetailKeyboardScrollVisitsEveryMiddleRowAndCollapseRestoresBenchViewport()
    {
        var observer = await PopulateAsync();
        var repository = observer.GetRequiredService<IWorkItemRepository>();
        var item = (await repository.GetByIdAsync(42))!;
        var markers = Enumerable.Range(1, 60).Select(index => $"SCROLL-ROW-{index:000}").ToArray();
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = string.Join("", markers.Select(marker => $"<p>{marker}</p>"))
        });
        await repository.SaveAsync(item);
        var baseline = await CaptureStoredRowsAsync(observer);
        var application = Application();
        using var terminal = new ScriptedBrowserTerminal(80, 12);
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = application.RunAsync(terminal, _fixture.Paths.StartDir!, ct: lifetime.Token);
        try
        {
            var bench = await ReadyAsync(terminal, "Selected cached item", deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Enter));
            var paint = await ReadyAsync(terminal, markers[^1], deadline.Token);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Observe(BrowserPaint frame)
            {
                var visible = string.Join("\n", frame.VisibleRows);
                foreach (var marker in markers)
                    if (visible.Contains(marker, StringComparison.Ordinal)) visited.Add(marker);
            }
            Observe(paint);
            while (paint.Top < Math.Max(0, paint.Rows.Count - BrowserTerminalFrame.ContentHeight(paint.Height)))
            {
                var prior = paint;
                terminal.Enqueue(Key(BrowserKey.Down));
                paint = await terminal.ReadUntilAsync(frame => frame.Sequence > prior.Sequence && !frame.Working, deadline.Token);
                paint.Top.ShouldBe(prior.Top + 1);
                Observe(paint);
            }
            visited.Order(StringComparer.Ordinal).ShouldBe(markers);
            terminal.Enqueue(Key(BrowserKey.Up));
            var previousTop = paint.Top;
            await terminal.ReadUntilAsync(frame => !frame.Working && frame.Top == previousTop - 1, deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Escape));
            var restored = await terminal.ReadUntilAsync(frame => !frame.Working && !frame.Content.Contains(markers[0], StringComparison.Ordinal), deadline.Token);
            restored.Rows.ShouldBe(bench.Rows);
            restored.Top.ShouldBe(bench.Top);
            (await CaptureStoredRowsAsync(observer)).ShouldBe(baseline);
            terminal.Enqueue(Character('q'));
            (await running.WaitAsync(deadline.Token)).ShouldBe(0);
            _fixture.Transport.RequestedItems.ShouldBeEmpty();
            _fixture.Transport.Mutations.ShouldBe(0);
        }
        finally
        {
            lifetime.Cancel();
            terminal.Enqueue(new BrowserInput(BrowserInputKind.EndOfInput));
            await running.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [HostMigrationFact]
    public async Task SeedSyncGesturePreservesDisplayedAndDurableStateWithoutRemoteCalls()
    {
        var observer = await PopulateAsync();
        var minted = await observer.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
        var seed = new WorkItemBuilder(minted.Alias.Value, "Local seed draft").InState("To Do")
            .AsSeed(stagedIdentity: minted.Identity).WithField("System.Description", "<p>SEED-LOCAL-BODY</p>").Build();
        await observer.GetRequiredService<IWorkItemRepository>().SaveAsync(seed);
        await observer.GetRequiredService<IPendingChangeStore>().AddChangeAsync(seed.Id, "note", null, null, "Unpublished seed note");
        await observer.GetRequiredService<ISeedLinkRepository>().AddLinkAsync(new(seed.Id, 80, SeedLinkTypes.Related, DateTimeOffset.UtcNow));
        var application = Application();
        using var terminal = new ScriptedBrowserTerminal(100, 120);
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = application.RunAsync(terminal, _fixture.Paths.StartDir!, ct: lifetime.Token);
        try
        {
            await ReadyAsync(terminal, "Local seed draft", deadline.Token);
            var baseline = await CaptureStoredRowsAsync(observer);
            terminal.Enqueue(Key(BrowserKey.Enter));
            var detail = await ReadyAsync(terminal, "SEED-LOCAL-BODY", deadline.Token);
            terminal.Enqueue(Character('S'));
            var afterGesture = await terminal.ReadUntilAsync(paint => paint.Sequence > detail.Sequence && !paint.Working, deadline.Token);
            afterGesture.Rows.ShouldBe(detail.Rows);
            afterGesture.Content.ShouldContain("SEED-LOCAL-BODY");
            (await application.DispatchAsync(new("status"), deadline.Token)).RequiresReconnect.ShouldBeFalse();
            (await CaptureStoredRowsAsync(observer)).ShouldBe(baseline);
            terminal.Enqueue(Key(BrowserKey.Escape));
            await terminal.ReadUntilAsync(paint => !paint.Working && !paint.Content.Contains("SEED-LOCAL-BODY", StringComparison.Ordinal), deadline.Token);
            terminal.Enqueue(Character('q'));
            (await running.WaitAsync(deadline.Token)).ShouldBe(0);
            (await CaptureStoredRowsAsync(observer)).ShouldBe(baseline);
            _fixture.Transport.RequestedItems.ShouldBeEmpty();
            _fixture.Transport.Mutations.ShouldBe(0);
        }
        finally
        {
            lifetime.Cancel();
            terminal.Enqueue(new BrowserInput(BrowserInputKind.EndOfInput));
            await running.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [HostMigrationFact]
    public async Task CanceledOlderDetailCannotClearNewerLoadingStateOrRestoreItsViewport()
    {
        var observer = await PopulateAsync();
        var barrier = new MetadataReadBarrier();
        var application = Application(barrier.Wrap);
        using var terminal = new ScriptedBrowserTerminal(100, 120);
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = application.RunAsync(terminal, _fixture.Paths.StartDir!, ct: lifetime.Token);
        try
        {
            await ReadyAsync(terminal, "Selected cached item", deadline.Token);
            var baseline = await CaptureStoredRowsAsync(observer);
            terminal.Enqueue(Key(BrowserKey.Enter));
            await barrier.Entered.Task.WaitAsync(deadline.Token);
            var olderLoading = await terminal.ReadUntilAsync(paint => paint.Working, deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Escape), Key(BrowserKey.Down), Key(BrowserKey.Enter));
            var newerLoading = await terminal.ReadUntilAsync(paint => paint.Sequence > olderLoading.Sequence && paint.Working, deadline.Token);
            barrier.Release.TrySetResult();
            await barrier.NewerEntered.Task.WaitAsync(deadline.Token);
            // The newer operation starts only after the older result is enqueued. This
            // control reply proves the owner consumed that result while the newer read waits.
            await application.DispatchAsync(new("status"), deadline.Token);
            terminal.Resize(88, 120);
            var stillLoading = await terminal.ReadUntilAsync(paint => paint.Width == 88, deadline.Token);
            stillLoading.Working.ShouldBeTrue();
            stillLoading.Content.ShouldNotContain("DETAIL42-ORIGINAL");
            stillLoading.Content.ShouldNotContain("DETAIL43-NEWER");
            barrier.NewerRelease.TrySetResult();
            var newer = await ReadyAsync(terminal, "DETAIL43-NEWER", deadline.Token);
            newer.Content.ShouldNotContain("DETAIL42-ORIGINAL");
            // The superseded completion must not paint a falsely idle newer request.
            terminal.ObservedPaints.Where(paint => paint.Sequence > newerLoading.Sequence)
                .ShouldAllBe(paint => paint.Working || paint.Content.Contains("DETAIL43-NEWER", StringComparison.Ordinal));
            terminal.Enqueue(Key(BrowserKey.Escape), Key(BrowserKey.Enter));
            await ReadyAsync(terminal, "DETAIL43-NEWER", deadline.Token);
            terminal.Enqueue(Character('q'));
            (await running.WaitAsync(deadline.Token)).ShouldBe(0);
            (await CaptureStoredRowsAsync(observer)).ShouldBe(baseline);
            _fixture.Transport.RequestedItems.ShouldBeEmpty();
            _fixture.Transport.Mutations.ShouldBe(0);
        }
        finally
        {
            barrier.Release.TrySetResult();
            barrier.NewerRelease.TrySetResult();
            lifetime.Cancel();
            terminal.Enqueue(new BrowserInput(BrowserInputKind.EndOfInput));
            await running.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [HostMigrationFact]
    public async Task ChangedRealBenchAuthorityClearsOldContentUntilExplicitFreshRuntimeReconnect()
    {
        var observer = await PopulateAsync();
        var benches = observer.GetRequiredService<IBenchRepository>();
        var replacement = (await benches.CreateAsync("replacement"))!;
        await benches.AddSelectorAsync(replacement.Id, BenchSelector.ForItem(43));
        var application = Application();
        using var terminal = new ScriptedBrowserTerminal(100, 120);
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = application.RunAsync(terminal, _fixture.Paths.StartDir!, ct: lifetime.Token);
        try
        {
            await ReadyAsync(terminal, "Selected cached item", deadline.Token);
            terminal.Enqueue(Key(BrowserKey.Enter));
            await ReadyAsync(terminal, "DETAIL42-ORIGINAL", deadline.Token);
            var originalGeneration = (await application.DispatchAsync(new("status"), deadline.Token)).Generation;
            // A real authority change, not a fabricated capture or failure result.
            await benches.SetCurrentAsync(replacement.Id);
            var changedBaseline = await CaptureStoredRowsAsync(observer);
            terminal.Enqueue(Character('R'));
            var stopped = await terminal.ReadUntilAsync(paint => !paint.Working && paint.Status.StartsWith("STOPPED", StringComparison.Ordinal), deadline.Token);
            stopped.Content.ShouldNotContain("Selected cached item");
            stopped.Content.ShouldNotContain("DETAIL42-ORIGINAL");
            var status = await application.DispatchAsync(new("status"), deadline.Token);
            status.RequiresReconnect.ShouldBeTrue();
            status.Generation.ShouldBeGreaterThan(originalGeneration);
            var changedView = await application.DispatchAsync(new("view", "table"), deadline.Token);
            changedView.RequiresReconnect.ShouldBeTrue();
            var unavailableTable = await terminal.ReadUntilAsync(paint => paint.Context.Contains("TABLE", StringComparison.Ordinal) && paint.Status.StartsWith("STOPPED", StringComparison.Ordinal), deadline.Token);
            unavailableTable.Content.ShouldNotContain("Selected cached item");
            (await CaptureStoredRowsAsync(observer)).ShouldBe(changedBaseline);

            terminal.Enqueue(Character('r', BrowserModifiers.Control));
            var reconnected = await terminal.ReadUntilAsync(paint => !paint.Working && paint.Header.Contains("replacement", StringComparison.Ordinal), deadline.Token);
            reconnected.Content.ShouldContain("Child cached item");
            reconnected.Content.ShouldNotContain("DETAIL42-ORIGINAL");
            (await application.DispatchAsync(new("status"), deadline.Token)).RequiresReconnect.ShouldBeFalse();
            terminal.Enqueue(Key(BrowserKey.Down), Key(BrowserKey.Enter));
            await ReadyAsync(terminal, "DETAIL43-NEWER", deadline.Token);
            terminal.Enqueue(Character('q'));
            (await running.WaitAsync(deadline.Token)).ShouldBe(0);
            // Runtime reconstruction may refresh process metadata, never durable intent.
            (await CaptureStoredRowsAsync(observer, durableOnly: true)).ShouldBe(
                changedBaseline.Where(row => row.StartsWith("pending.", StringComparison.Ordinal)).ToArray());
            (await observer.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(80);
            _fixture.Transport.RequestedItems.ShouldBeEmpty();
            _fixture.Transport.Mutations.ShouldBe(0);
        }
        finally
        {
            lifetime.Cancel();
            terminal.Enqueue(new BrowserInput(BrowserInputKind.EndOfInput));
            await running.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private BrowserApplication Application(Func<IFieldDefinitionStore, IFieldDefinitionStore>? fieldDefinitions = null)
        => new(new BrowserRuntimeFactory(_ => new BrowserRuntime(
            _fixture.CreateRuntimeAsync(fieldDefinitions).GetAwaiter().GetResult())));

    private async Task<ServiceProvider> PopulateAsync()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        var selected = new WorkItemBuilder(42, "Selected cached item").InState("To Do")
            .WithField("System.Description", "<p>DETAIL42-ORIGINAL</p>").Build();
        selected.MarkSynced(3);
        var child = new WorkItemBuilder(43, "Child cached item").InState("To Do").WithParent(42)
            .WithField("System.Description", "<p>DETAIL43-NEWER</p>").Build();
        child.MarkSynced(3);
        var owed = NativeBrowserFixture.Item(80, "Unrelated owed work");
        owed.UpdateField("System.Description", "<p>UNPUBLISHED80</p>");
        await repository.SaveBatchAsync([selected, child, owed]);
        await runtime.GetRequiredService<IPendingChangeStore>().AddChangeAsync(80, "field", "System.Description", "Server body", "<p>UNPUBLISHED80</p>");
        await runtime.GetRequiredService<IPendingChangeStore>().AddChangeAsync(80, "note", null, null, "Unpublished unrelated note");
        await runtime.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(80);
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var bench = (await benches.CreateAsync("scenario"))!;
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForSubtree(42));
        await benches.SetCurrentAsync(bench.Id);
        return runtime;
    }

    private static BrowserInput Key(BrowserKey key) => new(BrowserInputKind.Key, key);
    private static BrowserInput Character(char character, BrowserModifiers modifiers = BrowserModifiers.None)
        => new(BrowserInputKind.Key, Character: character, Modifiers: modifiers);
    private static void ClickFooter(ScriptedBrowserTerminal terminal, BrowserPaint paint, string label)
    {
        var start = paint.Status.IndexOf(label, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        (start + label.Length).ShouldBeLessThanOrEqualTo(paint.Width);
        terminal.Enqueue(new BrowserInput(BrowserInputKind.Mouse, X: start + 1, Y: paint.Height - 1, ButtonDown: true));
    }
    private static void ClickContext(ScriptedBrowserTerminal terminal, BrowserPaint paint, string label)
    {
        var start = paint.Context.IndexOf(label, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        (start + label.Length).ShouldBeLessThanOrEqualTo(paint.Width);
        terminal.Enqueue(new BrowserInput(BrowserInputKind.Mouse, X: start + 1, Y: 1, ButtonDown: true));
    }
    private static Task<BrowserPaint> ReadyAsync(ScriptedBrowserTerminal terminal, string content, CancellationToken ct)
        => terminal.ReadUntilAsync(paint => !paint.Working && paint.Content.Contains(content, StringComparison.Ordinal), ct);

    // Exact storage-value comparison, not file bytes: WAL/checkpoint layout is immaterial.
    // No journal, allocator, pending row, cached field or timestamp is excluded.
    private static async Task<string[]> CaptureStoredRowsAsync(ServiceProvider runtime, bool durableOnly = false)
    {
        var connection = runtime.GetRequiredService<SqliteCacheStore>().GetConnection();
        var snapshot = new List<string>();
        foreach (var schema in durableOnly ? new[] { "pending" } : new[] { "main", "pending" })
        {
            var tables = new List<string>();
            using (var names = connection.CreateCommand())
            {
                names.CommandText = $"SELECT name FROM {schema}.sqlite_schema WHERE type='table' ORDER BY name COLLATE BINARY;";
                await using var reader = await names.ExecuteReaderAsync();
                while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
            }
            foreach (var table in tables)
            {
                using var rows = connection.CreateCommand();
                rows.CommandText = $"SELECT * FROM {schema}.\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
                await using var reader = await rows.ExecuteReaderAsync();
                var prefix = schema + "." + table + ":";
                snapshot.Add(prefix + string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
                while (await reader.ReadAsync())
                    snapshot.Add(prefix + string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index => StoredValue(reader.GetValue(index)))));
            }
        }
        return snapshot.Order(StringComparer.Ordinal).ToArray();
    }

    private static string StoredValue(object value) => value switch
    {
        DBNull => "null",
        string text => "text:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
        byte[] blob => "blob:" + Convert.ToBase64String(blob),
        long number => "integer:" + number.ToString(CultureInfo.InvariantCulture),
        double number => "real:" + number.ToString("R", CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("Unexpected SQLite storage value: " + value.GetType().FullName),
    };

    private sealed class MetadataReadBarrier
    {
        private int _reads;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource NewerEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource NewerRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IFieldDefinitionStore Wrap(IFieldDefinitionStore actual) => new HeldFieldDefinitions(actual, this);

        private sealed class HeldFieldDefinitions(IFieldDefinitionStore actual, MetadataReadBarrier barrier) : IFieldDefinitionStore
        {
            public Task<FieldDefinition?> GetByReferenceNameAsync(string referenceName, CancellationToken ct = default)
                => actual.GetByReferenceNameAsync(referenceName, ct);
            public Task SaveBatchAsync(IReadOnlyList<FieldDefinition> definitions, CancellationToken ct = default)
                => actual.SaveBatchAsync(definitions, ct);
            public async Task<IReadOnlyList<FieldDefinition>> GetAllAsync(CancellationToken ct = default)
            {
                var definitions = await actual.GetAllAsync(ct);
                var read = Interlocked.Increment(ref barrier._reads);
                if (read == 1)
                {
                    barrier.Entered.TrySetResult();
                    // Only scheduling changes: retain the real read and permit a late completion.
                    await barrier.Release.Task;
                }
                else if (read == 2)
                {
                    barrier.NewerEntered.TrySetResult();
                    await barrier.NewerRelease.Task;
                }
                return definitions;
            }
        }
    }
}
