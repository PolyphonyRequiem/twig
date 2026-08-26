using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Twig.Infrastructure.Persistence.Transport;
using Twig.Infrastructure.Persistence.Transport.Adapters.Herdr;

namespace Twig.Infrastructure.Tests.Persistence.Transport.Adapters.Herdr;

/// <summary>
/// Synthetic <see cref="IHerdrHostSurface"/> for adapter tests. Every
/// method returns a pre-scripted <see cref="HerdrOperationOutcome"/> +
/// value; the tests never spawn a real <c>herdr</c> process. This is
/// the "synthetic Herdr surface" §12.2 tests are meant to run against.
/// </summary>
internal sealed class FakeHerdrHostSurface : IHerdrHostSurface
{
    public HerdrOperationOutcome StatusOutcome { get; set; } = HerdrOperationOutcome.Ok;
    public HerdrHostStatus StatusValue { get; set; } = HerdrHostStatus.Idle;
    public System.DateTimeOffset StatusRecordedAt { get; set; } = new(2026, 8, 26, 12, 0, 0, System.TimeSpan.Zero);
    public System.Func<int, Task>? StatusDelay { get; set; }

    public HerdrOperationOutcome LivenessOutcome { get; set; } = HerdrOperationOutcome.Ok;
    public TransportLivenessPresence LivenessPresence { get; set; } = TransportLivenessPresence.Present;
    public System.DateTimeOffset LivenessRecordedAt { get; set; } = new(2026, 8, 26, 12, 0, 0, System.TimeSpan.Zero);

    public HerdrOperationOutcome PreflightOutcome { get; set; } = HerdrOperationOutcome.Ok;
    public bool PreflightConfirmed { get; set; } = true;

    public HerdrOperationOutcome CloseOutcome { get; set; } = HerdrOperationOutcome.Ok;

    public HerdrOperationOutcome RemainingOutcome { get; set; } = HerdrOperationOutcome.Ok;
    public HerdrRemainingSummary RemainingSummary { get; set; } = HerdrRemainingSummary.Subset;

    public System.Func<Task>? ThrowFromStatus { get; set; }
    public System.Func<Task>? ThrowFromLiveness { get; set; }
    public System.Func<Task>? ThrowFromPreflight { get; set; }
    public System.Func<Task>? ThrowFromClose { get; set; }
    public System.Func<Task>? ThrowFromRemaining { get; set; }

    public List<(HerdrTargetLocator target, int budget)> StatusCalls { get; } = [];
    public List<(HerdrTargetLocator target, int budget)> LivenessCalls { get; } = [];
    public List<HerdrTargetLocator> PreflightCalls { get; } = [];
    public List<HerdrTargetLocator> CloseCalls { get; } = [];
    public List<HerdrTargetLocator> RemainingCalls { get; } = [];

    public async Task<HerdrStatusReadout> QueryStatusAsync(HerdrTargetLocator target, int budgetMs, CancellationToken ct)
    {
        StatusCalls.Add((target, budgetMs));
        if (ThrowFromStatus is not null) await ThrowFromStatus().ConfigureAwait(false);
        if (StatusDelay is not null) await StatusDelay(budgetMs).ConfigureAwait(false);
        return new HerdrStatusReadout(StatusOutcome, StatusValue, StatusRecordedAt);
    }

    public async Task<HerdrLivenessReadout> QueryLivenessAsync(HerdrTargetLocator target, int budgetMs, CancellationToken ct)
    {
        LivenessCalls.Add((target, budgetMs));
        if (ThrowFromLiveness is not null) await ThrowFromLiveness().ConfigureAwait(false);
        return new HerdrLivenessReadout(LivenessOutcome, LivenessPresence, LivenessRecordedAt);
    }

    public async Task<HerdrPreflightReadout> PreflightCloseAsync(HerdrTargetLocator target, CancellationToken ct)
    {
        PreflightCalls.Add(target);
        if (ThrowFromPreflight is not null) await ThrowFromPreflight().ConfigureAwait(false);
        return new HerdrPreflightReadout(PreflightOutcome, PreflightConfirmed);
    }

    public async Task<HerdrCloseReadout> CloseAsync(HerdrTargetLocator target, CancellationToken ct)
    {
        CloseCalls.Add(target);
        if (ThrowFromClose is not null) await ThrowFromClose().ConfigureAwait(false);
        return new HerdrCloseReadout(CloseOutcome);
    }

    public async Task<HerdrRemainingReadout> ObservePartialCloseRemainingAsync(HerdrTargetLocator parent, CancellationToken ct)
    {
        RemainingCalls.Add(parent);
        if (ThrowFromRemaining is not null) await ThrowFromRemaining().ConfigureAwait(false);
        return new HerdrRemainingReadout(RemainingOutcome, RemainingSummary);
    }
}
