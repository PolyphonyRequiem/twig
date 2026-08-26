using System.Collections.Generic;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Structured target the dispatch layer passes to every
/// <see cref="ITransportAdapter"/> method (§7.4). Replaces the earlier
/// flat <c>adapterId</c>/<c>hostAttachmentId</c>/<c>hostAttachmentIdKind</c>
/// fields so the adapter can workspace-qualify a Herdr ID, distinguish
/// tab from pane, and run AB#746's mandated preflight against live
/// records.
/// <para>
/// <see cref="AdapterContext"/> is opaque to core (§7.4); the adapter
/// defines its keys. <see cref="HostAttachmentId"/> is treated by core
/// as an opaque string. <see cref="AdapterId"/> is the registration key
/// defined in §7.
/// </para>
/// </summary>
internal sealed record TransportAdapterTarget(
    TransportAdapterRole Role,
    string AdapterId,
    string HostAttachmentId,
    string HostAttachmentIdKind,
    IReadOnlyDictionary<string, string> AdapterContext);

/// <summary>
/// Contract §7.4 <c>RecordIdentityRequest</c>. Every §7 adapter's
/// <see cref="ITransportAdapter.RecordIdentity"/> takes exactly this
/// shape. <see cref="AgentSessionKind"/> is populated only when
/// <see cref="AgentTarget"/> is non-null.
/// </summary>
internal sealed record RecordIdentityRequest(
    string WorktreeFingerprint,
    TransportAdapterTarget WorktreeTarget,
    TransportAdapterTarget? AgentTarget,
    string? AgentSessionKind,
    TransportAdapterTarget? TerminalTarget,
    IReadOnlySet<TransportCapability> AgentCapabilities,
    IReadOnlySet<TransportCapability> TerminalCapabilities,
    RecordedStatus AgentRecordedStatus,
    System.DateTimeOffset AgentRecordedAt);

/// <summary>
/// Contract §7.4 <c>PartialCloseScope</c>. Scope is opaque to core:
/// <see cref="ScopeKind"/> and <see cref="ScopeId"/> are adapter-defined
/// so an adapter (e.g. Herdr's pane scope) can honour it verbatim.
/// </summary>
internal sealed record PartialCloseScope(
    string ScopeKind,
    string ScopeId,
    PartialCloseReason Reason);

/// <summary>Reason a <see cref="PartialCloseScope"/> was raised. Fixed
/// three-value catalogue in §7.4.</summary>
internal enum PartialCloseReason
{
    UserRequested = 0,
    CascadeHint = 1,
    AdapterInternal = 2,
}

/// <summary>
/// Contract §7.4 <c>AdapterDescription</c>. Returned from
/// <see cref="ITransportAdapter.DescribeAdapter"/>. <see cref="Capabilities"/>
/// carries the DECLARED §3.3 optional set — the mandatory §3.1
/// common-denominator names are excluded from this set by the same rule
/// the shape validator enforces on persisted <c>capabilities</c> blocks
/// (§3.1). <see cref="HumanReadable"/> is for diagnostics only and is
/// never a router key.
/// </summary>
internal sealed record AdapterDescription(
    string AdapterId,
    string DisplayName,
    string AdapterVersion,
    IReadOnlySet<TransportCapability> Capabilities,
    IReadOnlySet<TransportAdapterRole> SupportedRoles,
    string HumanReadable);
