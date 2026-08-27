using System.Threading;
using System.Threading.Tasks;
using Twig.Domain.Common;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §7.1 <c>ITransportAdapter</c>. The single interface every
/// host adapter registers with core. Names follow AB#736 §9's
/// language-neutral form; the CLR shape here maps 1:1.
/// <para>
/// <see cref="AdapterId"/> is the opaque registration key (e.g.
/// <c>"herdr"</c>, <c>"windows-terminal"</c>, <c>"null"</c>). Core
/// resolves the adapter for a given target by string equality against
/// this key — no discovery, no ordering-driven priority, no fallback
/// (§7.2). Unknown <c>adapterId</c> raises
/// <see cref="TransportAttachmentFailure.AdapterNotRegistered"/>.
/// </para>
/// <para>
/// <see cref="Capabilities"/> is the declared §3.3 OPTIONAL subset only.
/// The mandatory §3.1 common-denominator capabilities
/// (<c>RecordIdentity</c>, <c>DescribeAdapter</c>) are NEVER members
/// (§3.1). Core inspects this set before dispatching a call: for an
/// undeclared §3.3 capability, core applies the per-operation §3.2
/// degradation rather than invoking the adapter; the adapter is never
/// called (§7.1).
/// </para>
/// <para>
/// The optional dispatch methods below (<see cref="ReportStatusAsync"/>,
/// <see cref="ProbeLivenessAsync"/>, <see cref="DetachAsync"/>,
/// <see cref="CloseAsync"/>, <see cref="PartialCloseAsync"/>) are
/// implemented ONLY when the corresponding capability is declared.
/// Adapters that do not declare a capability MUST throw
/// <see cref="System.NotSupportedException"/> from the method (unreachable
/// by construction — the dispatcher gates on
/// <see cref="Capabilities"/> and never calls an undeclared method).
/// </para>
/// </summary>
internal interface ITransportAdapter
{
    string AdapterId { get; }
    System.Collections.Generic.IReadOnlySet<TransportCapability> Capabilities { get; }

    /// <summary>§3.1 mandatory. Accept an opaque
    /// <see cref="TransportAdapterTarget.HostAttachmentId"/> +
    /// <see cref="TransportAdapterTarget.HostAttachmentIdKind"/>
    /// supplied at attachment time and echo it back on read. Never
    /// discovered by the adapter, because Windows Terminal cannot
    /// discover.</summary>
    Result<TransportAttachmentRecord> RecordIdentity(RecordIdentityRequest request);

    /// <summary>§3.1 mandatory. Return the adapter's registration
    /// metadata: <see cref="AdapterId"/>, a display name, the set of
    /// declared optional capabilities, and a stable adapter version.
    /// </summary>
    AdapterDescription DescribeAdapter();

    /// <summary>§3.3 optional. Only invoked when
    /// <see cref="Capabilities"/> contains
    /// <see cref="TransportCapability.StatusReporting"/>. Runs a bounded
    /// host query under the §5.1 500 ms budget (or the caller-override
    /// clamp). Timeout returns
    /// <see cref="Result.Ok"/> with a §5.2 embedded observation whose
    /// <see cref="TransportStatusObservation.TimeoutError"/> is
    /// <see cref="TransportAttachmentFailure.ProbeTimeout"/>; non-timeout
    /// adapter failure returns
    /// <see cref="TransportAttachmentFailure.ProbeAdapterFailed"/> as
    /// <see cref="Result.Fail"/>.</summary>
    Task<Result<TransportStatusObservation>> ReportStatusAsync(
        TransportAdapterTarget target,
        TransportProbeOptions? options,
        CancellationToken ct);

    /// <summary>§3.3 optional. Only invoked when
    /// <see cref="Capabilities"/> contains
    /// <see cref="TransportCapability.LivenessProbe"/>. Runs a bounded
    /// existence/availability probe under the §5.1 2000 ms budget.
    /// Timeout returns <see cref="Result.Ok"/> with a §5.2 embedded
    /// observation whose
    /// <see cref="TransportLivenessObservation.Error"/> is
    /// <see cref="TransportAttachmentFailure.ProbeTimeout"/>; non-timeout
    /// adapter failure returns
    /// <see cref="TransportAttachmentFailure.ProbeAdapterFailed"/> as
    /// <see cref="Result.Fail"/>.</summary>
    Task<Result<TransportLivenessObservation>> ProbeLivenessAsync(
        TransportAdapterTarget target,
        TransportProbeOptions? options,
        CancellationToken ct);

    /// <summary>§3.3 optional. Only invoked when
    /// <see cref="Capabilities"/> contains
    /// <see cref="TransportCapability.Detach"/>. Adapter drops any
    /// host-side tracking it owns; never terminates a host session
    /// (§6.1). Returns <see cref="Result.Ok"/> or a
    /// <see cref="TransportAttachmentFailure.DetachAdapterFailed"/>
    /// dispatch-level failure.</summary>
    Task<Result> DetachAsync(TransportAdapterTarget target, CancellationToken ct);

    /// <summary>§3.3 optional. Only invoked when
    /// <see cref="Capabilities"/> contains
    /// <see cref="TransportCapability.Close"/>. Adapter runs the host's
    /// close verb (§6.2). Reachable only via explicit caller invocation
    /// (§1.1(c)). Returns <see cref="Result.Ok"/> or
    /// <see cref="TransportAttachmentFailure.CloseAdapterFailed"/>.
    /// </summary>
    Task<Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct);

    /// <summary>§3.3 optional. Only invoked when
    /// <see cref="Capabilities"/> contains
    /// <see cref="TransportCapability.PartialClose"/>. Adapter attempts
    /// the scoped close (§6.3). Reachable only via explicit caller
    /// invocation (§1.1(c)). Returns either a bounded outcome via
    /// <see cref="Result.Ok"/> or
    /// <see cref="TransportAttachmentFailure.PartialCloseAdapterFailed"/>
    /// on an internal failure that could not produce a bounded
    /// outcome.</summary>
    Task<Result<TransportPartialCloseOutcome>> PartialCloseAsync(
        TransportAdapterTarget target,
        PartialCloseScope scope,
        CancellationToken ct);
}
