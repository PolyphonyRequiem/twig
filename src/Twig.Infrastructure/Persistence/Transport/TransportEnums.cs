namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Roles a <see cref="TransportAdapterTarget"/> may play inside a
/// <see cref="TransportAttachmentRecord"/>. Fixed by contract §2.1 and
/// §7.4 so a shape validator can decide role from the record alone.
/// </summary>
internal enum TransportAdapterRole
{
    Worktree = 0,
    Agent = 1,
    Terminal = 2,
}

/// <summary>
/// Core-neutral status enumeration per contract §4.1. Adapters MUST map
/// host status to one of these values by table lookup only (§4.2) — no
/// synthesis. See <see cref="RecordedStatusExtensions"/> for the fixed
/// string forms used inside the persisted record and matched by the
/// §2.2 row-5 validator.
/// </summary>
internal enum RecordedStatus
{
    /// <summary>Adapter observed a host state that means "ready for input
    /// or turn-finished" but cannot distinguish. The only value Herdr's
    /// <c>idle</c> maps to (§4.3). Callers MUST NOT read this as proof of
    /// any turn boundary.</summary>
    IdleAmbiguous = 0,

    /// <summary>Host reports the session actively producing output/turn
    /// work.</summary>
    Working = 1,

    /// <summary>Host reports a recognized approval/question/waiting-for-input
    /// UI.</summary>
    Blocked = 2,

    /// <summary>Host reports background work finished. HINT, not a
    /// completion proof; callers still consult Change Proposal / plan
    /// status for authoritative completion (§4.1).</summary>
    Done = 3,

    /// <summary>Host is present but the state is not confidently
    /// classifiable.</summary>
    Unknown = 4,

    /// <summary>No <c>StatusReporting</c> capability. Distinct from
    /// <see cref="Unknown"/>: <c>unknown</c> means "adapter probed and the
    /// host was inconclusive"; <c>unobservable</c> means "no probe is
    /// possible on this transport".</summary>
    Unobservable = 5,
}

/// <summary>
/// Fixed wire-form for each <see cref="RecordedStatus"/> value. The
/// persisted string a §2.2 row 5 validator matches on and the value the
/// null / Windows-Terminal degradation returns.
/// </summary>
internal static class RecordedStatusExtensions
{
    public const string IdleAmbiguous = "idle-ambiguous";
    public const string Working = "working";
    public const string Blocked = "blocked";
    public const string Done = "done";
    public const string Unknown = "unknown";
    public const string Unobservable = "unobservable";

    public static string ToWire(this RecordedStatus status) => status switch
    {
        RecordedStatus.IdleAmbiguous => IdleAmbiguous,
        RecordedStatus.Working => Working,
        RecordedStatus.Blocked => Blocked,
        RecordedStatus.Done => Done,
        RecordedStatus.Unknown => Unknown,
        RecordedStatus.Unobservable => Unobservable,
        _ => throw new System.ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>§4.1 — parse the exact §4.1 wire form. Returns
    /// <c>false</c> on any string outside the six-value catalogue so the
    /// §2.2 row-5 validator can raise
    /// <see cref="TransportAttachmentFailure.UnknownStatus"/>.</summary>
    public static bool TryParse(string wire, out RecordedStatus status)
    {
        switch (wire)
        {
            case IdleAmbiguous: status = RecordedStatus.IdleAmbiguous; return true;
            case Working: status = RecordedStatus.Working; return true;
            case Blocked: status = RecordedStatus.Blocked; return true;
            case Done: status = RecordedStatus.Done; return true;
            case Unknown: status = RecordedStatus.Unknown; return true;
            case Unobservable: status = RecordedStatus.Unobservable; return true;
            default: status = default; return false;
        }
    }
}

/// <summary>
/// The optional capabilities from contract §3.3. Exhaustive at v1;
/// adding an entry is a schema change. NOTE the mandatory §3.1
/// common-denominator capabilities (<c>RecordIdentity</c>,
/// <c>DescribeAdapter</c>) are NOT members of this enum: every adapter
/// implements them and they are never declared in a
/// <see cref="AdapterDescription.Capabilities"/> set. The §2.2 row-6
/// validator raises <see cref="TransportAttachmentFailure.UnknownCapability"/>
/// on a persisted set containing either name — encoded via the
/// <see cref="TransportCapabilityExtensions"/> string catalogue.
/// </summary>
internal enum TransportCapability
{
    /// <summary>§3.3 — adapter runs a bounded host query under the §5.1
    /// budget and returns <see cref="TransportStatusObservation"/>.</summary>
    StatusReporting = 0,

    /// <summary>§3.3 — adapter runs a bounded existence/availability
    /// probe under the §5.1 budget and returns
    /// <see cref="TransportLivenessObservation"/>.</summary>
    LivenessProbe = 1,

    /// <summary>§3.3 — adapter releases any host-side tracking it owns
    /// (Twig-side stop-tracking). Detach never terminates a host session.
    /// </summary>
    Detach = 2,

    /// <summary>§3.3 — adapter issues the host-defined close for the
    /// referenced <see cref="TransportAdapterTarget.HostAttachmentId"/>.
    /// Reachable only via explicit caller invocation (§1.1(c), §6.2).
    /// </summary>
    Close = 3,

    /// <summary>§3.3 — adapter attempts to close a subset of the host
    /// attachment (e.g. a single pane inside a tab) scoped by
    /// <see cref="PartialCloseScope"/>. Reachable only via explicit caller
    /// invocation (§1.1(c), §6.3).</summary>
    PartialClose = 4,
}

/// <summary>
/// Wire strings and lookup helpers for the §3.3 optional capability
/// catalogue. These are the exact tokens a persisted <c>capabilities</c>
/// block may contain; every other string a validator sees fails with
/// <see cref="TransportAttachmentFailure.UnknownCapability"/>.
/// </summary>
internal static class TransportCapabilityExtensions
{
    public const string StatusReporting = "StatusReporting";
    public const string LivenessProbe = "LivenessProbe";
    public const string Detach = "Detach";
    public const string Close = "Close";
    public const string PartialClose = "PartialClose";

    /// <summary>The mandatory §3.1 common-denominator names. Their
    /// presence in a persisted <c>capabilities</c> set is a §2.2 row-6
    /// failure.</summary>
    public const string RecordIdentity = "RecordIdentity";

    /// <summary>See <see cref="RecordIdentity"/>.</summary>
    public const string DescribeAdapter = "DescribeAdapter";

    public static string ToWire(this TransportCapability capability) => capability switch
    {
        TransportCapability.StatusReporting => StatusReporting,
        TransportCapability.LivenessProbe => LivenessProbe,
        TransportCapability.Detach => Detach,
        TransportCapability.Close => Close,
        TransportCapability.PartialClose => PartialClose,
        _ => throw new System.ArgumentOutOfRangeException(nameof(capability), capability, null),
    };

    public static bool TryParse(string wire, out TransportCapability capability)
    {
        switch (wire)
        {
            case StatusReporting: capability = TransportCapability.StatusReporting; return true;
            case LivenessProbe: capability = TransportCapability.LivenessProbe; return true;
            case Detach: capability = TransportCapability.Detach; return true;
            case Close: capability = TransportCapability.Close; return true;
            case PartialClose: capability = TransportCapability.PartialClose; return true;
            default: capability = default; return false;
        }
    }

    /// <summary>Returns <c>true</c> for the two §3.1 common-denominator
    /// names that MUST NOT appear in a persisted <c>capabilities</c>
    /// block.</summary>
    public static bool IsCommonDenominator(string wire) =>
        wire == RecordIdentity || wire == DescribeAdapter;
}

/// <summary>Persistence-boundary field of every §5 observation. Fixed by
/// contract §5.3 as a three-value enumeration; both status and liveness
/// carry it (the earlier draft omitted it from liveness, §5.3
/// corrects).</summary>
internal enum TransportFreshness
{
    /// <summary><c>now - recordedAt &lt;= freshWindowMs</c>. Default
    /// <c>freshWindowMs = 2000</c> ms.</summary>
    Fresh = 0,

    /// <summary><c>now - recordedAt &gt; freshWindowMs</c>. Callers MAY
    /// still consume it. Also the mandatory label for every
    /// bounded-failure observation per §5.3's carve-out.</summary>
    Stale = 1,

    /// <summary>The adapter does not declare the corresponding capability
    /// (§3.2 dispatch degradation); <c>recordedAt</c> is <c>null</c>.
    /// </summary>
    Unobservable = 2,
}

/// <summary>Liveness presence per contract §3.3 and §5.2.</summary>
internal enum TransportLivenessPresence
{
    Present = 0,
    Absent = 1,

    /// <summary>Absent-capability degradation (§3.2) OR an inconclusive
    /// probe.</summary>
    Unknown = 2,

    /// <summary>The adapter honoured its budget and returned a
    /// bounded-failure observation (e.g. timeout §5.2). Not a
    /// dispatch-level failure; embedded inside <c>Result.Ok</c>.</summary>
    Error = 3,
}

/// <summary>Post-state confidence a §6.3 partial-close attempt reports.
/// The UNVERIFIED-safe rule: <see cref="Unknown"/> when the adapter
/// cannot independently confirm the post-state; callers MUST NOT
/// re-issue a compensating <c>Close</c>.</summary>
internal enum TransportPartialCloseRemaining
{
    /// <summary>Adapter could not confirm. Callers MUST NOT compensate.
    /// </summary>
    Unknown = 0,

    /// <summary>Adapter observed at least one sibling remaining.</summary>
    Subset = 1,

    /// <summary>Adapter observed no siblings remaining.</summary>
    None = 2,
}
