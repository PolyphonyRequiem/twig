using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Twig.Domain.Common;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §12.3 Windows Terminal adapter (AB#747). Registers with core
/// under <c>adapterId = "windows-terminal"</c>. Declares NO §3.3
/// optional capabilities — Windows Terminal exposes no query,
/// enumeration, or status surface (<c>local://host-surfaces.md</c>),
/// and even the identity handle a caller passes is not a probe
/// mechanism because a nonexistent <c>wt.exe --window &lt;id&gt;</c>
/// silently CREATES a new window (§3.4 rationale, §12.3 MUST NOT).
/// <para>
/// Callers therefore receive the §3.2 absent-capability degradations
/// through the dispatcher: <c>status = unobservable</c>,
/// <c>liveness = unknown / unobservable</c>,
/// <see cref="TransportAttachmentFailure.CloseNotSupported"/>,
/// <see cref="TransportAttachmentFailure.PartialCloseNotSupported"/>,
/// and record-level detach success. Detach at the record level is
/// always available (§3.2, §6.1).
/// </para>
/// <para>
/// The two implemented mandatory §3.1 common-denominator operations:
/// </para>
/// <list type="bullet">
///   <item><see cref="RecordIdentity"/> — echoes the caller-supplied
///     terminal target, normalizing <see cref="TransportAdapterTarget.HostAttachmentId"/>
///     per §7.4 into a decimal string with no leading zeros for the
///     integer path (<see cref="HostAttachmentIdKindInteger"/>) or the
///     caller's exact string for the named path
///     (<see cref="HostAttachmentIdKindName"/>).</item>
///   <item><see cref="DescribeAdapter"/> — returns fixed metadata for
///     the empty §3.3 declared set.</item>
/// </list>
/// <para>
/// Non-interference (contract §9): attach, probe, detach, and close
/// invocations against a Windows Terminal Transport Attachment MUST
/// NOT trigger any claim, Change Proposal, plan-lifecycle, or ADO
/// mutation. The adapter carries no such dependency by construction —
/// it holds no references outside the transport namespace.
/// </para>
/// <para>
/// Side-effect-free probe guarantee (§12.3 emphasis): this adapter
/// MUST NEVER invoke <c>wt.exe</c> on any observation path — not from
/// <see cref="RecordIdentity"/>, not from <see cref="DescribeAdapter"/>,
/// and not from the four optional dispatch methods (which throw
/// <see cref="System.NotSupportedException"/> because the dispatcher
/// gates on <see cref="Capabilities"/> and never reaches them). The
/// class shells to no process, ever. A test asserts the reachable
/// call graph.
/// </para>
/// </summary>
internal sealed class WindowsTerminalTransportAdapter : ITransportAdapter
{
    /// <summary>Fixed registration key from §7 / §12.3.</summary>
    public const string Id = "windows-terminal";

    /// <summary>§7.4 mandated <see cref="TransportAdapterTarget.HostAttachmentIdKind"/>
    /// for the integer <c>--window &lt;id&gt;</c> path. The
    /// <see cref="TransportAdapterTarget.HostAttachmentId"/> under this
    /// kind is the caller's integer, normalized to decimal with no
    /// leading zeros.</summary>
    public const string HostAttachmentIdKindInteger = "wt-window-integer";

    /// <summary>§7.4 mandated <see cref="TransportAdapterTarget.HostAttachmentIdKind"/>
    /// for the named <c>--window &lt;name&gt;</c> path (see
    /// <c>local://host-surfaces.md</c> §Identity). The
    /// <see cref="TransportAdapterTarget.HostAttachmentId"/> under this
    /// kind is the caller's exact string, unmodified.</summary>
    public const string HostAttachmentIdKindName = "wt-window-name";

    private const string DisplayNameConstant = "Windows Terminal";
    private const string AdapterVersionConstant = "1.0.0";
    private const string HumanReadableConstant =
        "Windows Terminal transport adapter — records identity only; no host observation, no close, no partial close (contract §12.3).";

    private static readonly IReadOnlySet<TransportCapability> _emptyCaps = new HashSet<TransportCapability>();
    private static readonly IReadOnlySet<TransportAdapterRole> _supportedRoles = new HashSet<TransportAdapterRole>
    {
        TransportAdapterRole.Terminal,
    };

    public string AdapterId => Id;
    public IReadOnlySet<TransportCapability> Capabilities => _emptyCaps;

    /// <summary>
    /// §3.1 mandatory. Echoes the caller-supplied terminal target after
    /// normalizing <see cref="TransportAdapterTarget.HostAttachmentId"/>
    /// per §7.4. Accepts both §2.2 shapes when the terminal target's
    /// <see cref="TransportAdapterTarget.AdapterId"/> is
    /// <see cref="Id"/> — direct-human (agent null) and agent-driven
    /// (agent non-null). Every other combination lands on the §2.2
    /// shape validator's named identifiers.
    /// </summary>
    public Result<TransportAttachmentRecord> RecordIdentity(RecordIdentityRequest request)
    {
        // The Windows Terminal adapter always constructs its record
        // around a Windows Terminal terminal target. A caller reaching
        // this method without a terminal target has requested a shape
        // this adapter cannot own; the shape validator (§2.2 row 3)
        // owns the message, so surface it verbatim.
        if (request.TerminalTarget is null)
            return Result.Fail<TransportAttachmentRecord>(TransportAttachmentFailure.BareWorktree);

        // A caller pointed at this adapter for a non-Windows-Terminal
        // terminal target has hit the wrong adapter; refuse to
        // construct a record whose payload adapterId disagrees with
        // the adapter that built it. Surface the schema-level
        // identifier — the request is malformed by construction.
        if (!string.Equals(request.TerminalTarget.AdapterId, Id, System.StringComparison.Ordinal))
            return Result.Fail<TransportAttachmentRecord>(TransportAttachmentFailure.RecordInvalid);

        // §7.4 normalization. This is the ONLY interpretation of the
        // caller-supplied host handle the adapter performs; no probe,
        // no lookup, no shell-out.
        var normalizedTerminal = NormalizeWindowsTerminalTarget(request.TerminalTarget);
        if (!normalizedTerminal.IsSuccess)
            return Result.Fail<TransportAttachmentRecord>(normalizedTerminal.Error);

        TransportAgentPayload? agent = null;
        if (request.AgentTarget is not null)
        {
            // §7.4 SessionKind is opaque and adapter-defined; core
            // requires it non-null when the agent target is present.
            var sessionKind = request.AgentSessionKind;
            if (string.IsNullOrEmpty(sessionKind))
                return Result.Fail<TransportAttachmentRecord>(TransportAttachmentFailure.RecordInvalid);
            agent = new TransportAgentPayload(
                Target: request.AgentTarget,
                SessionKind: sessionKind,
                RecordedStatus: request.AgentRecordedStatus,
                RecordedAt: request.AgentRecordedAt,
                Capabilities: request.AgentCapabilities);
        }

        var record = new TransportAttachmentRecord(
            Worktree: new TransportWorktreePayload(
                WorktreeFingerprint: request.WorktreeFingerprint,
                Target: request.WorktreeTarget),
            Agent: agent,
            Terminal: new TransportTerminalPayload(
                Target: normalizedTerminal.Value,
                Capabilities: request.TerminalCapabilities));

        // §2.2 shape validator — the produced record MUST be one of
        // the two accepted shapes. This closes the acceptance line
        // "Attachments the adapter produces validate as
        // agent-driven-with-host or direct-human and never any other
        // shape".
        var shape = TransportShapeValidator.ValidateRecord(record);
        if (!shape.IsSuccess)
            return Result.Fail<TransportAttachmentRecord>(shape.Error);
        return Result.Ok(record);
    }

    public AdapterDescription DescribeAdapter() =>
        new(
            AdapterId: Id,
            DisplayName: DisplayNameConstant,
            AdapterVersion: AdapterVersionConstant,
            Capabilities: _emptyCaps,
            SupportedRoles: _supportedRoles,
            HumanReadable: HumanReadableConstant);

    // §3.3 optional capabilities: NOT declared. The dispatcher gates
    // every one of these methods on Capabilities.Contains(...) and
    // never invokes them for this adapter (§3.2, §7.1). Throwing here
    // is the contract-mandated defense against a caller who bypasses
    // the dispatcher — the exception surfaces the routing bug rather
    // than silently pretending to observe Windows Terminal (§3.4).
    //
    // Critically, NONE of these methods shells out to wt.exe: the
    // §12.3 "MUST NOT probe by sending wt.exe --window <id>" rule is
    // an unconditional side-effect ban, so even the unreachable path
    // never touches the process API.

    public Task<Result<TransportStatusObservation>> ReportStatusAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct)
        => throw new System.NotSupportedException("Windows Terminal adapter does not declare StatusReporting; dispatcher applies §3.2 degradation. See contract §12.3 / §3.4 — no query surface exists.");

    public Task<Result<TransportLivenessObservation>> ProbeLivenessAsync(TransportAdapterTarget target, TransportProbeOptions? options, CancellationToken ct)
        => throw new System.NotSupportedException("Windows Terminal adapter does not declare LivenessProbe; dispatcher applies §3.2 degradation. See contract §12.3 — probing the caller-supplied window handle would silently CREATE a new window.");

    public Task<Result> DetachAsync(TransportAdapterTarget target, CancellationToken ct)
        => throw new System.NotSupportedException("Windows Terminal adapter does not declare Detach; dispatcher returns Result.Ok() per §3.2 (record-level detach always available).");

    public Task<Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct)
        => throw new System.NotSupportedException("Windows Terminal adapter does not declare Close; dispatcher returns transport-close-not-supported per §3.2. See contract §12.3 — no close-by-ID surface exists in Windows Terminal.");

    public Task<Result<TransportPartialCloseOutcome>> PartialCloseAsync(TransportAdapterTarget target, PartialCloseScope scope, CancellationToken ct)
        => throw new System.NotSupportedException("Windows Terminal adapter does not declare PartialClose; dispatcher returns transport-partial-close-not-supported per §3.2. See contract §12.3.");

    /// <summary>
    /// §7.4 mandated normalization for a Windows Terminal terminal
    /// target. For the integer kind (<see cref="HostAttachmentIdKindInteger"/>),
    /// the caller-supplied string is parsed as a non-negative decimal
    /// integer and rendered back with no leading zeros. For the named
    /// kind (<see cref="HostAttachmentIdKindName"/>), the caller's
    /// string is used exactly as supplied. Every other kind, and every
    /// unparseable integer, returns
    /// <see cref="TransportAttachmentFailure.RecordInvalid"/>.
    /// </summary>
    private static Result<TransportAdapterTarget> NormalizeWindowsTerminalTarget(TransportAdapterTarget target)
    {
        if (string.IsNullOrEmpty(target.HostAttachmentId))
            return Result.Fail<TransportAdapterTarget>(TransportAttachmentFailure.RecordInvalid);

        switch (target.HostAttachmentIdKind)
        {
            case HostAttachmentIdKindInteger:
            {
                // Windows Terminal window IDs are non-negative
                // integers per local://host-surfaces.md. Invariant
                // culture; no thousands separators, no signs, no
                // whitespace — a raw decimal handle only.
                if (!int.TryParse(
                        target.HostAttachmentId,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                    return Result.Fail<TransportAdapterTarget>(TransportAttachmentFailure.RecordInvalid);
                if (parsed < 0)
                    return Result.Fail<TransportAdapterTarget>(TransportAttachmentFailure.RecordInvalid);
                var normalized = parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (string.Equals(normalized, target.HostAttachmentId, System.StringComparison.Ordinal))
                    return Result.Ok(target); // already canonical; avoid allocation.
                return Result.Ok(target with { HostAttachmentId = normalized });
            }

            case HostAttachmentIdKindName:
                // Named path: caller's exact string, no rewrite.
                return Result.Ok(target);

            default:
                // Any other kind is a caller bug — §7.4 fixes the two
                // WT-legal kinds and nothing else.
                return Result.Fail<TransportAdapterTarget>(TransportAttachmentFailure.RecordInvalid);
        }
    }
}
