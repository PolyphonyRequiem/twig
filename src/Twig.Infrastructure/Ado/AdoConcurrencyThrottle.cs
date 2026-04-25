namespace Twig.Infrastructure.Ado;

/// <summary>
/// Process-wide concurrency limiter for ADO HTTP requests.
/// Caps the number of in-flight ADO API calls and respects 429 Retry-After headers
/// by pausing all queued requests for the duration of the retry window.
/// </summary>
/// <remarks>
/// Default concurrency of 4 is conservative — at ~200ms per request, 4 concurrent
/// calls produce ~20 requests/second, well within ADO's per-user rate limit.
/// </remarks>
internal sealed class AdoConcurrencyThrottle : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private long _pauseUntilTicks = DateTimeOffset.MinValue.UtcTicks;

    public AdoConcurrencyThrottle(int maxConcurrency = 4)
    {
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <summary>
    /// Acquires a concurrency slot, honoring any active Retry-After pause.
    /// Dispose the returned handle to release the slot.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        var pauseUntilTicks = Interlocked.Read(ref _pauseUntilTicks);
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (pauseUntilTicks > nowTicks)
        {
            await Task.Delay(TimeSpan.FromTicks(pauseUntilTicks - nowTicks), ct);
        }

        await _semaphore.WaitAsync(ct);
        return new SemaphoreReleaser(_semaphore);
    }

    /// <summary>
    /// Records a 429 rate-limit response. All subsequent <see cref="AcquireAsync"/> calls
    /// will pause until the retry window expires.
    /// </summary>
    public void SetPause(TimeSpan retryAfter)
    {
        var newTicks = (DateTimeOffset.UtcNow + retryAfter).UtcTicks;
        Interlocked.Exchange(ref _pauseUntilTicks, newTicks);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
