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
    public const string EligibilityUnavailable = "eligibility-unavailable";
}
