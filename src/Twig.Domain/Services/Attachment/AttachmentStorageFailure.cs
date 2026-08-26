namespace Twig.Domain.Services.Attachment;

/// <summary>
/// Named storage identifiers from AB#736 §8. These are the exact string codes
/// the storage layer raises across every fail-closed path so downstream verbs
/// can route on them without parsing prose; §8 fixes them as stable across
/// releases (adding one is a schema change).
/// <para>
/// Kept as a static class of string constants rather than an enum so the wire
/// value survives verbatim through <see cref="Twig.Domain.Common.Result"/> and
/// so the surface tests can assert the literal a human-readable status line
/// contains.
/// </para>
/// </summary>
internal static class AttachmentStorageFailure
{
    public const string NotAGitWorktree = "not-a-git-worktree";
    public const string BareRepositoryNotSupported = "bare-repository-not-supported";
    public const string LayoutMarkerMissing = "layout-marker-missing";
    public const string WorktreeFingerprintDrift = "worktree-fingerprint-drift";
    public const string AttachmentConnectionMismatch = "attachment-connection-mismatch";
    public const string CheckedInConfigInvalid = "checked-in-config-invalid";
    public const string LegacyLayoutPresent = "legacy-layout-present";
    public const string SystemStoreLocked = "system-store-locked";
    public const string SystemStoreSchemaMismatch = "system-store-schema-mismatch";
    public const string WorktreeNotRegistered = "worktree-not-registered";
    public const string WorktreeRetired = "worktree-retired";
    public const string AtomicWriteFailed = "atomic-write-failed";

    /// <summary>Selected profile could not supply a primary-scope allow-set
    /// yet. Deliberately not §8: the eligibility layer sits above storage.
    /// Named to prevent a permissive-by-default silent widening.</summary>
    public const string ClaimDuplicateReserved = "claim-duplicate-reserved";
    public const string ClaimCasMismatch = "claim-cas-mismatch";
    public const string SelectedProfileUnavailable = "selected-profile-unavailable";
    public const string EligibilityUnavailable = "eligibility-unavailable";

    /// <summary>Attachment link/unlink observed the primary scope block
    /// changed between the caller's read and the write — a lost-update
    /// coordination race. AB#737 §Attachment link ordering requires the
    /// mint/reclaim caller to abort rather than silently link into a
    /// switched or detached scope.</summary>
    public const string AttachmentScopeMismatch = "attachment-scope-mismatch";

    /// <summary>Attachment link/unlink observed the attachment document's
    /// version counter changed between the caller's read-verify snapshot
    /// and its write — another writer landed. The caller retries with a
    /// fresh read.</summary>
    public const string AttachmentVersionMismatch = "attachment-version-mismatch";

    /// <summary>Tuple-epoch CAS lost: a later reserver raised the
    /// monotonic per-tuple epoch between our reserve and our commit.
    /// AB#739 durable epoch protocol \u2014 the losing operation runs
    /// compensation against the winner recorded in <c>tuple_epochs</c>.</summary>
    public const string ClaimTupleEpochMismatch = "claim-tuple-epoch-mismatch";
}
