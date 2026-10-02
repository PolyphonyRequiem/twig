using System.Collections.Concurrent;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Shared concurrency limiter for ADO HTTP requests. Server pauses belong to a
/// principal/authority budget, not the transport or an identity's management alias.
/// </summary>
internal sealed class AdoConcurrencyThrottle : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly ConcurrentDictionary<AdoRateLimitBudget, long> _pauses = new();
    private readonly TimeProvider _clock;

    public AdoConcurrencyThrottle(int maxConcurrency = 4, TimeProvider? clock = null)
    {
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Waits outside concurrency slots and rechecks a pause after queued admission.</summary>
    public async Task<IDisposable> AcquireAsync(AdoRateLimitBudget? budget, CancellationToken ct)
    {
        while (true)
        {
            var remaining = RemainingPause(budget);
            if (remaining > TimeSpan.Zero)
            {
                // Task.Delay limits a single timer's range; do not shorten the server deadline.
                await Task.Delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, _clock, ct).ConfigureAwait(false);
                continue;
            }

            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            if (RemainingPause(budget) <= TimeSpan.Zero)
                return new SemaphoreReleaser(_semaphore);
            _semaphore.Release();
        }
    }

    /// <summary>Concurrent responses may extend a deadline, never shorten another response's pause.</summary>
    public void SetPause(AdoRateLimitBudget? budget, TimeSpan retryAfter)
    {
        if (budget is not { } key || retryAfter <= TimeSpan.Zero)
            return;
        var now = _clock.GetUtcNow().UtcTicks;
        var deadline = now + Math.Min(retryAfter.Ticks, DateTimeOffset.MaxValue.UtcTicks - now);
        _pauses.AddOrUpdate(key, static (_, next) => next, static (_, previous, next) => Math.Max(previous, next), deadline);
    }

    private TimeSpan RemainingPause(AdoRateLimitBudget? budget)
    {
        if (budget is not { } key || !_pauses.TryGetValue(key, out var deadline))
            return TimeSpan.Zero;
        var now = _clock.GetUtcNow().UtcTicks;
        if (deadline > now)
            return TimeSpan.FromTicks(deadline - now);
        _pauses.TryRemove(new KeyValuePair<AdoRateLimitBudget, long>(key, deadline));
        return TimeSpan.Zero;
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}
