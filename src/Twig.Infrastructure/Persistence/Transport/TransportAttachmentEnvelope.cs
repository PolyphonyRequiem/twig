namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Envelope state per contract §2.1. <c>attached</c> requires
/// <see cref="TransportAttachmentEnvelope.Record"/> present; <c>detached</c>
/// requires it null and marks the tombstone (§8.2).
/// </summary>
internal enum TransportAttachmentEnvelopeState
{
    /// <summary>Live attachment. <c>record</c> is the §2.1 record.
    /// </summary>
    Attached = 0,

    /// <summary>Tombstone (§8.2). <c>record</c> is null; the envelope's
    /// <c>revision</c> remains the CAS anchor.</summary>
    Detached = 1,
}

/// <summary>
/// The persisted envelope of <c>transport.json</c> per contract §2.1.
/// <see cref="Revision"/> is a positive integer that increments on every
/// mutation and is preserved across the <see cref="Detached"/> tombstone
/// state so detach + reattach cannot silently rewind the CAS token.
/// <para>
/// Envelope semantics (§2.1):
/// <list type="bullet">
///   <item><see cref="TransportAttachmentEnvelopeState.Attached"/>
///     REQUIRES <see cref="Record"/> present.</item>
///   <item><see cref="TransportAttachmentEnvelopeState.Detached"/>
///     REQUIRES <see cref="Record"/> = <c>null</c>.</item>
///   <item><see cref="ConnectionRef"/> is the AB#736 §5.1 hash of the
///     live <c>twig.json</c> connection block; a mismatch at read time
///     raises
///     <see cref="TransportAttachmentFailure.ConnectionMismatch"/>
///     (§8.4).</item>
/// </list>
/// </para>
/// </summary>
internal sealed record TransportAttachmentEnvelope(
    long Revision,
    string ConnectionRef,
    System.DateTimeOffset RecordedAt,
    TransportAttachmentEnvelopeState State,
    TransportAttachmentRecord? Record);

/// <summary>Envelope plus its on-disk revision counter, returned by
/// <see cref="ITransportAttachmentStore.ReadWithRevisionAsync"/>.
/// <see cref="Envelope"/> is <c>null</c> when the file does not exist
/// (§8.2 "never-attached" case) — the caller MUST NOT synthesize a
/// default record. <see cref="Revision"/> is 0 for that case, and the
/// CAS token for the next call otherwise.</summary>
internal readonly record struct VersionedTransportEnvelope(
    TransportAttachmentEnvelope? Envelope,
    long Revision);

/// <summary>Result payload for every mutating store call. Carries the
/// new CAS token the caller uses on the next call.</summary>
internal readonly record struct TransportWriteOutcome(long WrittenRevision);
