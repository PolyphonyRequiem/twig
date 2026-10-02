using Shouldly;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public sealed class AdoConcurrencyThrottleTests
{
    private static readonly AdoRateLimitBudget Actor = new("https://dev.azure.com/fixture", "aad:tenant:actor");
    private static readonly AdoRateLimitBudget Sibling = new("https://dev.azure.com/fixture", "aad:tenant:sibling");

    [Fact]
    public async Task ConcurrencySlotBlocksUntilReleased()
    {
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: 1);
        var first = await throttle.AcquireAsync(Actor, CancellationToken.None);
        var pending = throttle.AcquireAsync(Sibling, CancellationToken.None);
        pending.IsCompleted.ShouldBeFalse();
        first.Dispose();
        using var second = await pending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PausedActorDoesNotOccupyTheOnlySlotOrPauseSiblingBudgets()
    {
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: 1);
        throttle.SetPause(Actor, TimeSpan.FromMinutes(1));
        using var cancel = new CancellationTokenSource();
        var paused = throttle.AcquireAsync(Actor, cancel.Token);
        try
        {
            using var sibling = await throttle.AcquireAsync(Sibling, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            paused.IsCompleted.ShouldBeFalse();
        }
        finally { cancel.Cancel(); }
        await Should.ThrowAsync<OperationCanceledException>(() => paused);
    }

    [Fact]
    public async Task PausePublishedWhileQueuedIsRecheckedWithoutStarvingSibling()
    {
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: 1);
        var held = await throttle.AcquireAsync(Sibling, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var queued = throttle.AcquireAsync(Actor, cancel.Token);
        throttle.SetPause(Actor, TimeSpan.FromMinutes(1));
        held.Dispose();
        try
        {
            using var sibling = await throttle.AcquireAsync(Sibling, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            queued.IsCompleted.ShouldBeFalse("a queued actor cannot escape a pause recorded while it waited");
        }
        finally { cancel.Cancel(); }
        await Should.ThrowAsync<OperationCanceledException>(() => queued);
    }

    [Fact]
    public async Task ShorterConcurrentResponseCannotEraseAnOutstandingDeadline()
    {
        var clock = new MutableClock();
        using var throttle = new AdoConcurrencyThrottle(clock: clock);
        throttle.SetPause(Actor, TimeSpan.FromMinutes(1));
        throttle.SetPause(Actor, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        using var cancel = new CancellationTokenSource();
        var waiting = throttle.AcquireAsync(Actor, cancel.Token);
        waiting.IsCompleted.ShouldBeFalse();
        cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task ExpiredDeadlineAdmitsTheOriginalActorAgain()
    {
        var clock = new MutableClock();
        using var throttle = new AdoConcurrencyThrottle(clock: clock);
        throttle.SetPause(Actor, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        using var resumed = await throttle.AcquireAsync(Actor, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        throttle.SetPause(Actor, TimeSpan.FromMinutes(1));
        using var cancel = new CancellationTokenSource();
        var waiting = throttle.AcquireAsync(Actor, cancel.Token);
        waiting.IsCompleted.ShouldBeFalse("a later pause must survive cleanup of the expired entry");
        cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task SamePrincipalInAnotherAuthorityHasAnIndependentBudget()
    {
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: 1);
        throttle.SetPause(Actor, TimeSpan.FromMinutes(1));
        var otherAuthority = Actor with { Authority = "https://dev.azure.com/another-fixture" };
        using var slot = await throttle.AcquireAsync(otherAuthority, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellationOfQueuedRequestDoesNotConsumeASlot()
    {
        using var throttle = new AdoConcurrencyThrottle(maxConcurrency: 1);
        var held = await throttle.AcquireAsync(Actor, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var queued = throttle.AcquireAsync(Sibling, cancel.Token);
        cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => queued);
        held.Dispose();
        using var available = await throttle.AcquireAsync(Sibling, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
