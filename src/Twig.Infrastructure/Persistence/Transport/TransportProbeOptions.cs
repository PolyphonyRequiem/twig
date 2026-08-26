using Twig.Domain.Common;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §5.1 <c>TransportProbeOptions</c>. Callers MAY override the
/// per-operation budget via <see cref="TimeoutMs"/>; core clamps to
/// <c>[100, 30000]</c> ms inclusive and raises
/// <see cref="TransportAttachmentFailure.ProbeBudgetInvalid"/> as
/// <see cref="Result.Fail"/> otherwise. A <c>null</c>
/// <see cref="TimeoutMs"/> means "use the default budget":
/// <see cref="TransportProbeBudget.LivenessProbeDefaultMs"/> for
/// <c>LivenessProbe</c>, <see cref="TransportProbeBudget.StatusReportingDefaultMs"/>
/// for <c>StatusReporting</c>.
/// </summary>
internal sealed record TransportProbeOptions(int? TimeoutMs);

/// <summary>
/// Bounded probe defaults and clamp range fixed by contract §5.1. These
/// are constants of the contract; changing one is a schema change.
/// </summary>
internal static class TransportProbeBudget
{
    /// <summary>§5.1 — envelope for a socket round-trip against Herdr's
    /// <c>pane current</c> / <c>agent explain</c>.</summary>
    public const int LivenessProbeDefaultMs = 2000;

    /// <summary>§5.1 — envelope for a snapshot query against
    /// <c>herdr api snapshot</c> or <c>herdr pane current --current</c>.
    /// A timeout at this budget indicates the host observation surface
    /// is not responding within the intended interactive window.</summary>
    public const int StatusReportingDefaultMs = 500;

    /// <summary>§5.1 — inclusive lower bound of the caller-override
    /// clamp.</summary>
    public const int MinClampMs = 100;

    /// <summary>§5.1 — inclusive upper bound of the caller-override
    /// clamp.</summary>
    public const int MaxClampMs = 30_000;

    /// <summary>§5.3 — <c>freshWindowMs</c>. Constant of the contract,
    /// not an adapter tuning knob (§5.3).</summary>
    public const int FreshWindowMs = 2000;

    /// <summary>§5.1 clamp check. Returns <c>true</c> when
    /// <paramref name="timeoutMs"/> is <c>null</c> (use default) or lies
    /// within <c>[100, 30000]</c> ms inclusive. Out-of-range values
    /// raise <see cref="TransportAttachmentFailure.ProbeBudgetInvalid"/>
    /// per §5.1.</summary>
    public static bool IsValid(int? timeoutMs) =>
        timeoutMs is null || (timeoutMs.Value >= MinClampMs && timeoutMs.Value <= MaxClampMs);

    /// <summary>Resolve the effective budget for a
    /// <c>LivenessProbe</c> call under <paramref name="options"/>.</summary>
    public static int ResolveLivenessBudget(TransportProbeOptions? options) =>
        options?.TimeoutMs ?? LivenessProbeDefaultMs;

    /// <summary>Resolve the effective budget for a
    /// <c>StatusReporting</c> call under <paramref name="options"/>.
    /// </summary>
    public static int ResolveStatusBudget(TransportProbeOptions? options) =>
        options?.TimeoutMs ?? StatusReportingDefaultMs;

    /// <summary>§5.3 freshness computation against an observation's own
    /// <c>recordedAt</c>. The §5.3 carve-out (bounded-failure = stale)
    /// is applied by the caller who saw the failure; this method is
    /// the pure timestamp rule for the successful-observation path.
    /// </summary>
    public static TransportFreshness Compute(
        System.DateTimeOffset recordedAt,
        System.DateTimeOffset now)
    {
        var elapsed = now - recordedAt;
        return elapsed.TotalMilliseconds <= FreshWindowMs
            ? TransportFreshness.Fresh
            : TransportFreshness.Stale;
    }
}
