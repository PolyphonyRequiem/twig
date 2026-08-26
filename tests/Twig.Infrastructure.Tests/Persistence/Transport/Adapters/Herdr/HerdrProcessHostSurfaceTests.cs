using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Twig.Infrastructure.Persistence.Transport.Adapters.Herdr;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.Transport.Adapters.Herdr;

/// <summary>
/// Contract §5.3 timestamp discipline for
/// <see cref="HerdrProcessHostSurface"/>. The successful-observation
/// path stamps <c>RecordedAt</c> AT THE MOMENT the value is obtained
/// from the host (i.e. after a successful <c>InvokeAsync</c> returns),
/// not before launch. Stamping pre-launch would return `Stale` from a
/// slow-but-successful query, defeating §5.3's "no-cache adapter's
/// observation is always fresh on return" rule.
///
/// <para>
/// The tests inject a synthetic invoker via the internal test seam so
/// they never spawn a real <c>herdr</c> process. A stepping
/// <see cref="TimeProvider"/> simulates arbitrary elapsed time during
/// the invoker call so a test can distinguish "timestamp taken before"
/// from "timestamp taken after".
/// </para>
/// </summary>
public sealed class HerdrProcessHostSurfaceTests
{
    private static readonly System.DateTimeOffset _t0 = new(2026, 8, 26, 12, 0, 0, System.TimeSpan.Zero);

    private static HerdrTargetLocator LocatorPane() =>
        new(Workspace: "w3", Tab: "w3:t1", Pane: "w3:p1", AgentTarget: null,
            HostAttachmentIdKind: HerdrAdapterConstants.HostAttachmentIdKindPane, HostAttachmentId: "w3:p1");

    private static string SnapshotWithStatus(string status)
        => "{\"workspace\":\"w3\",\"tabs\":[{\"id\":\"w3:t1\",\"panes\":[{\"id\":\"w3:p1\"}]}],\"agent_status\":\"" + status + "\"}";

    /// <summary>
    /// Defect 5 (Spec-axis final review): a slow-but-successful query
    /// MUST return `Fresh`. Before the fix the surface stamped
    /// `recordedAt` before launching the CLI, so a query that took
    /// longer than `FreshWindowMs` returned `Stale` even on the OK
    /// path. The stepping clock here jumps forward past the fresh
    /// window during the invoker's simulated work, so a
    /// pre-invoke stamp yields <c>_t0</c> (which the adapter would then
    /// treat as Stale against the post-invoke `now`) and a post-invoke
    /// stamp yields the advanced time (Fresh against `now`).
    /// </summary>
    [Fact]
    public async Task QueryStatus_ok_stamps_recordedAt_after_a_slow_invoke_completes()
    {
        var clock = new SteppingTimeProvider(_t0);
        var invoker = new Invoker((args, budget, ct) =>
        {
            // Simulate a slow-but-successful invoke by advancing the
            // clock past the fresh window inside the invoker's task.
            clock.Advance(System.TimeSpan.FromMilliseconds(
                Twig.Infrastructure.Persistence.Transport.TransportProbeBudget.FreshWindowMs + 500));
            return Task.FromResult(new HerdrProcessHostSurface.ProcessInvocation(
                HerdrOperationOutcome.Ok, SnapshotWithStatus("working")));
        });
        var surface = new HerdrProcessHostSurface("herdr", clock, invoker.Invoke);

        var readout = await surface.QueryStatusAsync(LocatorPane(), budgetMs: 1_000, CancellationToken.None);

        readout.Outcome.ShouldBe(HerdrOperationOutcome.Ok);
        readout.Status.ShouldBe(HerdrHostStatus.Working);
        // The timestamp MUST reflect the moment the value was obtained
        // from the host — i.e. after the invoke, not before. That is
        // the whole point of the fix.
        readout.RecordedAt.ShouldBeGreaterThan(_t0);
        readout.RecordedAt.ShouldBe(clock.LastReturned);

        // §5.3 successful-observation freshness against a same-instant
        // `now` MUST be `Fresh`.
        Twig.Infrastructure.Persistence.Transport.TransportProbeBudget
            .Compute(readout.RecordedAt, clock.GetUtcNow())
            .ShouldBe(Twig.Infrastructure.Persistence.Transport.TransportFreshness.Fresh);
    }

    [Fact]
    public async Task QueryLiveness_ok_stamps_recordedAt_after_a_slow_invoke_completes()
    {
        var clock = new SteppingTimeProvider(_t0);
        var invoker = new Invoker((args, budget, ct) =>
        {
            clock.Advance(System.TimeSpan.FromMilliseconds(
                Twig.Infrastructure.Persistence.Transport.TransportProbeBudget.FreshWindowMs + 500));
            return Task.FromResult(new HerdrProcessHostSurface.ProcessInvocation(
                HerdrOperationOutcome.Ok, SnapshotWithStatus("working")));
        });
        var surface = new HerdrProcessHostSurface("herdr", clock, invoker.Invoke);

        var readout = await surface.QueryLivenessAsync(LocatorPane(), budgetMs: 1_000, CancellationToken.None);

        readout.Outcome.ShouldBe(HerdrOperationOutcome.Ok);
        readout.RecordedAt.ShouldBeGreaterThan(_t0);
        Twig.Infrastructure.Persistence.Transport.TransportProbeBudget
            .Compute(readout.RecordedAt, clock.GetUtcNow())
            .ShouldBe(Twig.Infrastructure.Persistence.Transport.TransportFreshness.Fresh);
    }

    /// <summary>Fast-path sanity: a fast successful invoke still
    /// returns a plausible `RecordedAt`. Defends against a fix that
    /// only added time-after-invoke on the slow path.</summary>
    [Fact]
    public async Task QueryStatus_ok_fast_path_recordedAt_is_after_start()
    {
        var clock = new SteppingTimeProvider(_t0);
        // Advance a small amount inside the invoker so the recordedAt
        // is unambiguously post-start but still fresh.
        var invoker = new Invoker((args, budget, ct) =>
        {
            clock.Advance(System.TimeSpan.FromMilliseconds(10));
            return Task.FromResult(new HerdrProcessHostSurface.ProcessInvocation(
                HerdrOperationOutcome.Ok, SnapshotWithStatus("working")));
        });
        var surface = new HerdrProcessHostSurface("herdr", clock, invoker.Invoke);

        var readout = await surface.QueryStatusAsync(LocatorPane(), budgetMs: 1_000, CancellationToken.None);

        readout.Outcome.ShouldBe(HerdrOperationOutcome.Ok);
        readout.RecordedAt.ShouldBeGreaterThanOrEqualTo(_t0 + System.TimeSpan.FromMilliseconds(10));
    }

    private sealed class Invoker
    {
        private readonly System.Func<IReadOnlyList<string>, int, CancellationToken, Task<HerdrProcessHostSurface.ProcessInvocation>> _fn;
        public Invoker(System.Func<IReadOnlyList<string>, int, CancellationToken, Task<HerdrProcessHostSurface.ProcessInvocation>> fn) { _fn = fn; }
        public Task<HerdrProcessHostSurface.ProcessInvocation> Invoke(IReadOnlyList<string> args, int budget, CancellationToken ct)
            => _fn(args, budget, ct);
    }

    /// <summary>Mutable <see cref="TimeProvider"/> that lets a test
    /// simulate arbitrary elapsed time between clock reads. `LastReturned`
    /// is the timestamp returned by the most recent
    /// <see cref="GetUtcNow"/> call — used to prove the surface's
    /// `recordedAt` came from a post-invoke clock read.</summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        public SteppingTimeProvider(System.DateTimeOffset start) { Now = start; }
        public System.DateTimeOffset Now { get; private set; }
        public System.DateTimeOffset LastReturned { get; private set; }
        public void Advance(System.TimeSpan by) => Now += by;
        public override System.DateTimeOffset GetUtcNow()
        {
            LastReturned = Now;
            return Now;
        }
    }
}
