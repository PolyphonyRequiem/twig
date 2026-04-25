using System.Diagnostics;
using Shouldly;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoConcurrencyThrottle"/>: concurrency cap, 429 pause behavior, and disposal.
/// </summary>
public sealed class AdoConcurrencyThrottleTests
{
    [Fact]
    public async Task AcquireAsync_UnderConcurrencyLimit_GrantsSlotImmediately()
    {
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: 4);

        using var slot = await throttle.AcquireAsync(CancellationToken.None);

        slot.ShouldNotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_AtConcurrencyLimit_BlocksUntilSlotReleased()
    {
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: 1);

        // Acquire the only available slot
        var slot1 = await throttle.AcquireAsync(CancellationToken.None);

        // A second acquire should block while the first slot is held
        var pendingTask = throttle.AcquireAsync(CancellationToken.None);

        // Give the task a moment to run — it should still be waiting
        await Task.Delay(50);
        pendingTask.IsCompleted.ShouldBeFalse("second acquire should be blocked while first slot is held");

        // Release the first slot; the pending acquire should now complete
        slot1.Dispose();
        using var slot2 = await pendingTask.WaitAsync(TimeSpan.FromSeconds(5));
        slot2.ShouldNotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_MultipleSlotsReleasedConcurrently_AllUnblock()
    {
        const int concurrency = 3;
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: concurrency);

        // Fill all slots
        var slots = new IDisposable[concurrency];
        for (var i = 0; i < concurrency; i++)
            slots[i] = await throttle.AcquireAsync(CancellationToken.None);

        // One more acquire should block
        var pending = throttle.AcquireAsync(CancellationToken.None);
        await Task.Delay(30);
        pending.IsCompleted.ShouldBeFalse();

        // Release all slots
        foreach (var slot in slots)
            slot.Dispose();

        using var finalSlot = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        finalSlot.ShouldNotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_AfterSetPause_WaitsForPauseDuration()
    {
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: 4);

        throttle.SetPause(TimeSpan.FromMilliseconds(150));

        var sw = Stopwatch.StartNew();
        using var slot = await throttle.AcquireAsync(CancellationToken.None);
        sw.Stop();

        // Should have waited at least ~100ms (allowing generous tolerance for CI)
        sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(80);
    }

    [Fact]
    public async Task AcquireAsync_AfterPauseExpires_GrantsSlotWithoutDelay()
    {
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: 4);

        throttle.SetPause(TimeSpan.FromMilliseconds(50));
        await Task.Delay(120); // Let the pause expire

        var sw = Stopwatch.StartNew();
        using var slot = await throttle.AcquireAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.ShouldBeLessThan(80, "pause should have expired before acquisition");
    }

    [Fact]
    public async Task AcquireAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: 1);

        // Hold the only slot so the next acquire will block
        using var slot1 = await throttle.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => throttle.AcquireAsync(cts.Token));
    }

    [Fact]
    public async Task AcquireAsync_PauseCancelled_ThrowsOperationCanceledException()
    {
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: 4);

        throttle.SetPause(TimeSpan.FromSeconds(30)); // Long pause

        using var cts = new CancellationTokenSource(millisecondsDelay: 50);

        await Should.ThrowAsync<OperationCanceledException>(
            () => throttle.AcquireAsync(cts.Token));
    }

    [Fact]
    public async Task SetPause_CalledMultipleTimes_LastCallWins()
    {
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: 4);

        // Set a long pause then override with a short one
        throttle.SetPause(TimeSpan.FromSeconds(30));
        throttle.SetPause(TimeSpan.FromMilliseconds(100));

        var sw = Stopwatch.StartNew();
        using var slot = await throttle.AcquireAsync(CancellationToken.None);
        sw.Stop();

        // Should complete quickly (short pause won)
        sw.ElapsedMilliseconds.ShouldBeLessThan(5_000);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_DoesNotThrow()
    {
        var throttle = new AdoConcurrencyThrottle(maxConcurrency: 4);
        throttle.Dispose();

        // Second dispose should not throw (SemaphoreSlim.Dispose is safe to call twice)
        var ex = Record.Exception(() => throttle.Dispose());
        ex.ShouldBeNull();
    }

    [Fact]
    public async Task Dispose_SubsequentAcquire_ThrowsObjectDisposedException()
    {
        var throttle = new AdoConcurrencyThrottle(maxConcurrency: 4);
        throttle.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => throttle.AcquireAsync(CancellationToken.None));
    }
}
