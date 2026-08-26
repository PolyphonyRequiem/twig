using System.Collections.Generic;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// The three-field transport record per contract §2.1. Envelope-wrapped
/// for CAS by <see cref="TransportAttachmentEnvelope"/>. Each payload
/// is independently nullable subject to the §2.2 shape validator; the
/// only two accepted shapes are direct-human (<see cref="Worktree"/> +
/// <see cref="Terminal"/> only) and agent-driven
/// (<see cref="Worktree"/> + <see cref="Agent"/>, with optional
/// <see cref="Terminal"/>).
/// </summary>
internal sealed record TransportAttachmentRecord(
    TransportWorktreePayload? Worktree,
    TransportAgentPayload? Agent,
    TransportTerminalPayload? Terminal);

/// <summary>
/// Contract §2.1 worktree payload. <see cref="WorktreeFingerprint"/>
/// MUST byte-equal the worktree's fingerprint recorded by AB#736 §3.2;
/// on mismatch, the read raises
/// <see cref="TransportAttachmentFailure.WorktreeFingerprintMismatch"/>.
/// </summary>
internal sealed record TransportWorktreePayload(
    string WorktreeFingerprint,
    TransportAdapterTarget Target);

/// <summary>
/// Contract §2.1 agent payload. <see cref="SessionKind"/> is opaque and
/// adapter-defined; <see cref="RecordedStatus"/> is core-neutral per
/// §4.1; <see cref="Capabilities"/> is the DECLARED §3.3 optional set
/// (mandatory §3.1 names are rejected by §2.2 row 6).
/// </summary>
internal sealed record TransportAgentPayload(
    TransportAdapterTarget Target,
    string SessionKind,
    RecordedStatus RecordedStatus,
    System.DateTimeOffset RecordedAt,
    IReadOnlySet<TransportCapability> Capabilities);

/// <summary>
/// Contract §2.1 terminal payload. Carries only the target and
/// declared-capabilities block; no recorded status because a
/// terminal-host attachment does not report §4.1 status by itself.
/// </summary>
internal sealed record TransportTerminalPayload(
    TransportAdapterTarget Target,
    IReadOnlySet<TransportCapability> Capabilities);
