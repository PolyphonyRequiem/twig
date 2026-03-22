using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Twig.Domain.Services;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public class RenderWithSyncTests
{
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _renderer;

    public RenderWithSyncTests()
    {
        _testConsole = new TestConsole();
        _renderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()))
        {
            SyncStatusDelay = TimeSpan.Zero // eliminate delays in tests
        };
    }

    // ── Cached view rendered before sync ────────────────────────────

    [Fact]
    public async Task RenderWithSyncAsync_CachedView_RenderedBeforeSync()
    {
        var syncCalled = false;

        await _renderer.RenderWithSyncAsync(
            buildCachedView: () => Task.FromResult<IRenderable>(new Markup("Cached Data")),
            performSync: () =>
            {
                // By the time sync is called, cached view should already be rendered
                _testConsole.Output.ShouldContain("Cached Data");
                syncCalled = true;
                return Task.FromResult<SyncResult>(new SyncResult.UpToDate());
            },
            buildRevisedView: _ => Task.FromResult<IRenderable?>(null),
            ct: CancellationToken.None);

        syncCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task RenderWithSyncAsync_SyncingIndicator_ShownDuringSync()
    {
        await _renderer.RenderWithSyncAsync(
            buildCachedView: () => Task.FromResult<IRenderable>(new Markup("Data")),
            performSync: () =>
            {
                // Syncing indicator should be visible during the sync call
                _testConsole.Output.ShouldContain("syncing...");
                return Task.FromResult<SyncResult>(new SyncResult.UpToDate());
            },
            buildRevisedView: _ => Task.FromResult<IRenderable?>(null),
            ct: CancellationToken.None);
    }

    // ── UpToDate shows and clears ───────────────────────────────────

    [Fact]
    public async Task RenderWithSyncAsync_UpToDate_ShowsStatusThenClears()
    {
        await _renderer.RenderWithSyncAsync(
            buildCachedView: () => Task.FromResult<IRenderable>(new Markup("My Data")),
            performSync: () => Task.FromResult<SyncResult>(new SyncResult.UpToDate()),
            buildRevisedView: _ => Task.FromResult<IRenderable?>(null),
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        // The final output should contain the data but the status should be cleared
        output.ShouldContain("My Data");
        output.ShouldContain("up to date");
    }

    // ── Updated replaces content ────────────────────────────────────

    [Fact]
    public async Task RenderWithSyncAsync_Updated_RevisedViewReplacesCachedView()
    {
        await _renderer.RenderWithSyncAsync(
            buildCachedView: () => Task.FromResult<IRenderable>(new Markup("Old Data")),
            performSync: () => Task.FromResult<SyncResult>(new SyncResult.Updated(3)),
            buildRevisedView: result =>
            {
                result.ShouldBeOfType<SyncResult.Updated>();
                return Task.FromResult<IRenderable?>(new Markup("New Data"));
            },
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("New Data");
    }

    [Fact]
    public async Task RenderWithSyncAsync_Updated_ShowsChangedCount()
    {
        await _renderer.RenderWithSyncAsync(
            buildCachedView: () => Task.FromResult<IRenderable>(new Markup("Data")),
            performSync: () => Task.FromResult<SyncResult>(new SyncResult.Updated(5)),
            buildRevisedView: _ => Task.FromResult<IRenderable?>(new Markup("Updated Data")),
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("5 items updated");
    }

    [Fact]
    public async Task RenderWithSyncAsync_Updated_SingleItem_ShowsSingularLabel()
    {
        await _renderer.RenderWithSyncAsync(
            buildCachedView: () => Task.FromResult<IRenderable>(new Markup("Data")),
            performSync: () => Task.FromResult<SyncResult>(new SyncResult.Updated(1)),
            buildRevisedView: _ => Task.FromResult<IRenderable?>(new Markup("Updated Data")),
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("1 item updated");
        output.ShouldNotContain("1 items updated");
    }

    [Fact]
    public async Task RenderWithSyncAsync_Updated_NullRevisedView_KeepsCachedView()
    {
        await _renderer.RenderWithSyncAsync(
            buildCachedView: () => Task.FromResult<IRenderable>(new Markup("Cached")),
            performSync: () => Task.FromResult<SyncResult>(new SyncResult.Updated(2)),
            buildRevisedView: _ => Task.FromResult<IRenderable?>(null),
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Cached");
    }

    // ── Failed shows warning and persists ────────────────────────────

    [Fact]
    public async Task RenderWithSyncAsync_Failed_ShowsWarning()
    {
        await _renderer.RenderWithSyncAsync(
            buildCachedView: () => Task.FromResult<IRenderable>(new Markup("Cached View")),
            performSync: () => Task.FromResult<SyncResult>(new SyncResult.Failed("network error")),
            buildRevisedView: _ => Task.FromResult<IRenderable?>(null),
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("sync failed");
        output.ShouldContain("Cached View");
    }

    [Fact]
    public async Task RenderWithSyncAsync_Failed_DoesNotCallBuildRevisedView()
    {
        var revisedCalled = false;

        await _renderer.RenderWithSyncAsync(
            buildCachedView: () => Task.FromResult<IRenderable>(new Markup("Data")),
            performSync: () => Task.FromResult<SyncResult>(new SyncResult.Failed("timeout")),
            buildRevisedView: _ =>
            {
                revisedCalled = true;
                return Task.FromResult<IRenderable?>(null);
            },
            ct: CancellationToken.None);

        revisedCalled.ShouldBeFalse();
    }

    // ── Skipped shows up-to-date ────────────────────────────────────

    [Fact]
    public async Task RenderWithSyncAsync_Skipped_ShowsUpToDate()
    {
        await _renderer.RenderWithSyncAsync(
            buildCachedView: () => Task.FromResult<IRenderable>(new Markup("Skipped Data")),
            performSync: () => Task.FromResult<SyncResult>(new SyncResult.Skipped("already synced")),
            buildRevisedView: _ => Task.FromResult<IRenderable?>(null),
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Skipped Data");
        output.ShouldContain("up to date");
    }

    // ── FIX-001: Live region cached view must reserve terminal space ──

    [Fact]
    public async Task RenderWithSyncAsync_SpaceCachedView_ProducesMoreOutputThanEmptyString()
    {
        // FIX-001: Text(" ") must produce strictly more output than Text(string.Empty)
        // when used as the cached view in RenderWithSyncAsync. After the sync indicator
        // clears, the Live region collapses to the cached view. If that view measures
        // at 0 lines (Text(string.Empty)), Spectre erases into content above — clipping
        // the status panel's bottom border. Text(" ") reserves at least one terminal line.
        //
        // This test runs both variants through the same Live region path and compares
        // total output length. The only difference between the two runs is the cached
        // view renderable, so any length difference proves the space-based view produces
        // a measurable footprint that the empty-string view does not.

        // Run with Text(" ") — the fixed cached view
        var spaceConsole = new TestConsole();
        var spaceRenderer = new SpectreRenderer(spaceConsole, new SpectreTheme(new DisplayConfig()))
        {
            SyncStatusDelay = TimeSpan.Zero
        };
        await spaceRenderer.RenderWithSyncAsync(
            buildCachedView: () => Task.FromResult<IRenderable>(new Text(" ")),
            performSync: () => Task.FromResult<SyncResult>(new SyncResult.UpToDate()),
            buildRevisedView: _ => Task.FromResult<IRenderable?>(null),
            ct: CancellationToken.None);

        // Run with Text(string.Empty) — the broken cached view
        var emptyConsole = new TestConsole();
        var emptyRenderer = new SpectreRenderer(emptyConsole, new SpectreTheme(new DisplayConfig()))
        {
            SyncStatusDelay = TimeSpan.Zero
        };
        await emptyRenderer.RenderWithSyncAsync(
            buildCachedView: () => Task.FromResult<IRenderable>(new Text(string.Empty)),
            performSync: () => Task.FromResult<SyncResult>(new SyncResult.UpToDate()),
            buildRevisedView: _ => Task.FromResult<IRenderable?>(null),
            ct: CancellationToken.None);

        // Text(" ") must produce strictly more output than Text(string.Empty),
        // proving the single-space cached view reserves terminal space
        spaceConsole.Output.Length.ShouldBeGreaterThan(
            emptyConsole.Output.Length,
            "Text(\" \") cached view must produce more output than Text(string.Empty) " +
            "to prevent Spectre's Live region from collapsing and clipping content above");
    }

    // ── Cancellation ────────────────────────────────────────────────

    [Fact]
    public async Task RenderWithSyncAsync_CancelledDuringDelay_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var renderer = new SpectreRenderer(new TestConsole(), new SpectreTheme(new DisplayConfig()))
        {
            SyncStatusDelay = TimeSpan.FromSeconds(30) // long delay so cancellation fires first
        };

        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => renderer.RenderWithSyncAsync(
                buildCachedView: () => Task.FromResult<IRenderable>(new Markup("Data")),
                performSync: () => Task.FromResult<SyncResult>(new SyncResult.UpToDate()),
                buildRevisedView: _ => Task.FromResult<IRenderable?>(null),
                ct: cts.Token));
    }
}
