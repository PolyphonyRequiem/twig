using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Twig.Domain.Common;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §7.3 mandatory null adapter — the "no live host" path
/// AB#745 requires. Registered under <c>adapterId = "null"</c>;
/// declares no §3.3 optional capabilities. Every §3.3 dispatch reaches
/// the absent-capability degradation (§3.2); this adapter is never
/// invoked for those operations by the dispatcher (§7.1).
/// <para>
/// The null adapter's ONE valid recorded shape (§7.3) is a
/// direct-human record: <c>worktree.target.adapterId = "null"</c>,
/// <c>agent = null</c>, <c>terminal.target.adapterId = "null"</c>. The
/// shape validator (§2.2) accepts this because it structurally matches
/// the direct-human row.
/// </para>
/// </summary>
internal sealed class NullTransportAdapter : ITransportAdapter
{
    public const string Id = "null";
    public const string HostAttachmentIdKindNull = "null";

    private static readonly IReadOnlySet<TransportCapability> _emptyCaps = new HashSet<TransportCapability>();
    private static readonly IReadOnlySet<TransportAdapterRole> _supportedRoles = new HashSet<TransportAdapterRole>
    {
        TransportAdapterRole.Worktree,
        TransportAdapterRole.Terminal,
    };

    public string AdapterId => Id;
    public IReadOnlySet<TransportCapability> Capabilities => _emptyCaps;

    public Result<TransportAttachmentRecord> RecordIdentity(RecordIdentityRequest request)
    {
        // §7.3 — the null adapter's ONE valid recorded shape is a
        // direct-human record. Reject an agent request from the null
        // adapter at construction time so no other shape becomes
        // persistable.
        if (request.AgentTarget is not null)
            return Result.Fail<TransportAttachmentRecord>(TransportAttachmentFailure.OrphanTerminal);
        if (request.TerminalTarget is null)
            return Result.Fail<TransportAttachmentRecord>(TransportAttachmentFailure.BareWorktree);

        var record = new TransportAttachmentRecord(
            Worktree: new TransportWorktreePayload(
                WorktreeFingerprint: request.WorktreeFingerprint,
                Target: request.WorktreeTarget),
            Agent: null,
            Terminal: new TransportTerminalPayload(
                Target: request.TerminalTarget,
                Capabilities: request.TerminalCapabilities));

        // Shape validator sanity — the null adapter's echo of the
        // caller's request MUST match the direct-human row by
        // construction. A caller passing an invalid combination sees
        // the §2.2 identifier verbatim.
        var shape = TransportShapeValidator.ValidateRecord(record);
        if (!shape.IsSuccess)
            return Result.Fail<TransportAttachmentRecord>(shape.Error);
        return Result.Ok(record);
    }

    public AdapterDescription DescribeAdapter() =>
        new(
            AdapterId: Id,
            DisplayName: "No live host",
            AdapterVersion: "1.0.0",
            Capabilities: _emptyCaps,
            SupportedRoles: _supportedRoles,
            HumanReadable: "Null transport adapter — records identity only; no host observation, close, or partial close.");

    public Task<Result<TransportStatusObservation>> ReportStatusAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct)
        => throw new System.NotSupportedException("Null adapter does not declare StatusReporting; dispatcher applies §3.2 degradation.");

    public Task<Result<TransportLivenessObservation>> ProbeLivenessAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct)
        => throw new System.NotSupportedException("Null adapter does not declare LivenessProbe; dispatcher applies §3.2 degradation.");

    public Task<Result> DetachAsync(TransportAdapterTarget target, CancellationToken ct)
        => throw new System.NotSupportedException("Null adapter does not declare Detach; dispatcher applies §3.2 degradation.");

    public Task<Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct)
        => throw new System.NotSupportedException("Null adapter does not declare Close; dispatcher applies §3.2 degradation.");

    public Task<Result<TransportPartialCloseOutcome>> PartialCloseAsync(TransportAdapterTarget target, PartialCloseScope scope, CancellationToken ct)
        => throw new System.NotSupportedException("Null adapter does not declare PartialClose; dispatcher applies §3.2 degradation.");
}
