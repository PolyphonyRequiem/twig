namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §5 <c>TransportStatusObservation</c>. Every
/// <c>StatusReporting</c> invocation returns this, whether the adapter
/// obtained a live host value, timed out (§5.2), or the capability is
/// absent (§3.2). Bounded observation, never an exception.
/// <para>
/// Freshness rule (§5.3): <see cref="Freshness"/> is computed against
/// <see cref="RecordedAt"/>; a bounded-failure observation (timeout, or
/// any other §5.2 <c>Result.Ok</c> failure) MUST report
/// <see cref="TransportFreshness.Stale"/> regardless of
/// <see cref="RecordedAt"/> per §5.3's carve-out.
/// </para>
/// <para>
/// <see cref="TimeoutError"/> carries
/// <see cref="TransportAttachmentFailure.ProbeTimeout"/> on the timeout
/// path (§5.2); <c>null</c> otherwise. Callers rendering "we tried to
/// probe" surface the observation.
/// </para>
/// </summary>
internal sealed record TransportStatusObservation(
    RecordedStatus Status,
    System.DateTimeOffset? RecordedAt,
    TransportFreshness Freshness,
    string? TimeoutError);

/// <summary>
/// Contract §5 <c>TransportLivenessObservation</c>. Every
/// <c>LivenessProbe</c> invocation returns this. Bounded observation,
/// never an exception; the §5.2 timeout carve-out embeds
/// <see cref="TransportAttachmentFailure.ProbeTimeout"/> in
/// <see cref="Error"/> and sets <see cref="Presence"/> =
/// <see cref="TransportLivenessPresence.Error"/> with
/// <see cref="Freshness"/> = <see cref="TransportFreshness.Stale"/> per
/// §5.3.
/// </summary>
internal sealed record TransportLivenessObservation(
    TransportLivenessPresence Presence,
    System.DateTimeOffset? RecordedAt,
    TransportFreshness Freshness,
    string? Error);

/// <summary>
/// Contract §6.3 <c>TransportPartialCloseOutcome</c>. Returned from a
/// completed partial-close attempt. <see cref="ObservedRemaining"/> is
/// populated as <see cref="TransportPartialCloseRemaining.Subset"/> or
/// <see cref="TransportPartialCloseRemaining.None"/> only when the
/// adapter can independently confirm; otherwise MUST be
/// <see cref="TransportPartialCloseRemaining.Unknown"/> and callers MUST
/// NOT re-issue a compensating <c>Close</c> (§6.3 UNVERIFIED-safe rule).
/// </summary>
internal sealed record TransportPartialCloseOutcome(
    bool Attempted,
    TransportPartialCloseRemaining ObservedRemaining,
    string? Error);
