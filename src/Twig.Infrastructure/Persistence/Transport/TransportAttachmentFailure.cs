namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Named failure identifiers from the transport-attachment contract §11.
/// Kebab-case string constants — the same style as
/// <see cref="Twig.Domain.Services.Attachment.AttachmentStorageFailure"/>
/// from AB#736 §8 — so downstream verbs can route on the literal without
/// parsing prose. The wire value MUST survive verbatim through every
/// <c>Result</c>-shell surface (§11 pins these strings as stable across
/// releases; adding one is a schema change).
/// <para>
/// Result-shell classification per §11: every identifier below is
/// <c>Result.Fail</c> except <see cref="ProbeTimeout"/>, which is embedded
/// inside a <c>Result.Ok(observation)</c> as the bounded-failure signal per
/// §5.2.
/// </para>
/// </summary>
internal static class TransportAttachmentFailure
{
    /// <summary>§2.2 row 1 / §8.4 — envelope or record schema parse failure
    /// (unparseable JSON, unknown <c>$schema</c>, wrong <c>version</c>,
    /// <c>state</c>/<c>record</c> disagreement, non-integer <c>revision</c>).
    /// </summary>
    public const string RecordInvalid = "transport-record-invalid";

    /// <summary>§2.2 row 2 — <c>state = "attached"</c> but
    /// <c>record.worktree</c> is field-absent.</summary>
    public const string WorktreeMissing = "transport-worktree-missing";

    /// <summary>§2.2 row 3 — only <c>worktree</c> is set with both
    /// <c>agent</c> and <c>terminal</c> <c>null</c>.</summary>
    public const string BareWorktree = "transport-bare-worktree";

    /// <summary>§2.2 row 4 — the record fits neither §2.2 shape row.</summary>
    public const string OrphanTerminal = "transport-orphan-terminal";

    /// <summary>§2.2 row 5 — <c>agent.recordedStatus</c> outside §4.1's
    /// core-neutral enumeration.</summary>
    public const string UnknownStatus = "transport-unknown-status";

    /// <summary>§2.2 row 6 / §2.1 — a capability name outside §3.3's
    /// optional catalogue in a persisted set. Includes the mandatory
    /// common-denominator names (<c>RecordIdentity</c>, <c>DescribeAdapter</c>),
    /// which are §3.1 and MUST NOT appear in a persisted <c>capabilities</c>
    /// block.</summary>
    public const string UnknownCapability = "transport-unknown-capability";

    /// <summary>§8.4 — <c>transport.json.envelope.connectionRef</c>
    /// disagrees with the live <c>twig.json</c> connection ref.</summary>
    public const string ConnectionMismatch = "transport-connection-mismatch";

    /// <summary>§2.1 / §8.4 —
    /// <c>record.worktree.worktreeFingerprint</c> disagrees with the live
    /// §3.2 tuple from AB#736.</summary>
    public const string WorktreeFingerprintMismatch = "transport-worktree-fingerprint-mismatch";

    /// <summary>§8.4 — CAS: <c>expectedRevision</c> disagrees with the
    /// on-disk envelope revision.</summary>
    public const string VersionMismatch = "transport-version-mismatch";

    /// <summary>§7.2 / §7.3 — record's <c>adapterId</c> not in the
    /// registry. Includes the "unknown adapterId does NOT silently fall
    /// through to null" rule from §7.3.</summary>
    public const string AdapterNotRegistered = "transport-adapter-not-registered";

    /// <summary>§3.2 / §7.1 — caller invoked an operation for a capability
    /// name not in the §3.3 catalogue at this schema version (client-bug
    /// rail). NEVER raised for one of the five §3.3 capabilities themselves
    /// — those degrade per §3.2.</summary>
    public const string CapabilityNotDeclared = "transport-capability-not-declared";

    /// <summary>§5.2 — <c>StatusReporting</c> or <c>LivenessProbe</c>
    /// exceeded its (possibly caller-overridden) timeout. Embedded inside a
    /// <c>Result.Ok(observation)</c>, never a dispatch-level
    /// <c>Result.Fail</c>. Callers rendering "we tried to probe" surface
    /// the observation.</summary>
    public const string ProbeTimeout = "transport-probe-timeout";

    /// <summary>§5.1 — caller-supplied <c>timeoutMs</c> outside the
    /// <c>[100, 30000]</c> ms clamp range.</summary>
    public const string ProbeBudgetInvalid = "transport-probe-budget-invalid";

    /// <summary>§5.2 — the adapter's declared <c>StatusReporting</c> or
    /// <c>LivenessProbe</c> could not produce a bounded observation at all
    /// (adapter code threw / host command failed for a non-timeout reason).
    /// </summary>
    public const string ProbeAdapterFailed = "transport-probe-adapter-failed";

    /// <summary>§6.1 — the adapter's declared <c>Detach</c> returned a
    /// failure. Core still writes the detach tombstone (§8.2 idempotence).
    /// </summary>
    public const string DetachAdapterFailed = "transport-detach-adapter-failed";

    /// <summary>§3.2 / §6.2 — core dispatch: <c>Close</c> invoked on an
    /// adapter that did not declare it.</summary>
    public const string CloseNotSupported = "transport-close-not-supported";

    /// <summary>§6.2 — the adapter's declared <c>Close</c> returned a
    /// failure.</summary>
    public const string CloseAdapterFailed = "transport-close-adapter-failed";

    /// <summary>§3.2 / §6.3 — core dispatch: <c>PartialClose</c> invoked
    /// on an adapter that did not declare it.</summary>
    public const string PartialCloseNotSupported = "transport-partial-close-not-supported";

    /// <summary>§6.3 — the adapter's declared <c>PartialClose</c> could
    /// not produce a bounded outcome.</summary>
    public const string PartialCloseAdapterFailed = "transport-partial-close-adapter-failed";

    /// <summary>§8.3 — ADO client boundary: a transport type reached ADO
    /// serialization. Runtime backstop for the three §8.3 rails.</summary>
    public const string AdoProjectionForbidden = "transport-ado-projection-forbidden";

    /// <summary>§8.4 — temp write, <c>fsync</c>, or <c>rename</c> failed
    /// for <c>transport.json</c>. Single mapping for every atomic I/O
    /// failure; storage layers never surface an unnamed exception.</summary>
    public const string AtomicWriteFailed = "transport-atomic-write-failed";
}
