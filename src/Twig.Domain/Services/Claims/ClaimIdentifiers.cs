namespace Twig.Domain.Services.Claims;

/// <summary>
/// Named identifier constants for local claim lifecycle states and terminal
/// reasons (AB#737 §Record shape). Kept as string constants — not enums — so
/// the wire-level value survives verbatim through the source-generated JSON
/// document (<c>ClaimRecordDocument</c>) and every registry row without a
/// migration.
/// </summary>
internal static class ClaimStates
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Released = "released";
    public const string Superseded = "superseded";
}

/// <summary>
/// Terminal <c>releaseReason</c> discriminators (AB#737 §Record shape). Only
/// set when <see cref="ClaimStates.Released"/> or
/// <see cref="ClaimStates.Superseded"/>; <c>null</c> while pending or active.
/// </summary>
internal static class ClaimReleaseReasons
{
    public const string ExplicitRelease = "explicit-release";
    public const string ExplicitReclaim = "explicit-reclaim";
    public const string MintAbort = "mint-abort";
}

/// <summary>
/// Fixed <c>origin</c> discriminator. AB#737 §Deferred, not ambiguous fixes
/// version 1 records to exactly <c>local</c>; a future coordinator extension
/// may write <c>coordinator</c> but never retroactively activates any
/// reaper behavior.
/// </summary>
internal static class ClaimOrigins
{
    public const string Local = "local";
}

/// <summary>
/// Fixed <c>primaryScopeKind</c> discriminator for the version 1 schema. The
/// registry keeps the field opaque so a future non-ADO scope can land without
/// a migration, but readers accept only values the profile declares — every
/// other kind fails loud (<see cref="ClaimFailureCodes.SchemaDrift"/>).
/// </summary>
internal static class PrimaryScopeKinds
{
    public const string AdoWorkItem = "ado-workitem";
}

/// <summary>
/// Fully enumerated set of named claim lifecycle failure identifiers. Each
/// maps 1:1 to a variant in an <c>abstract record</c> outcome type so callers
/// can pattern-match instead of parsing strings; the codes below are the
/// stable wire values surfaced in
/// <see cref="Twig.Domain.Common.Result"/> error payloads (which storage /
/// projection seams still use) and in AB#736 §8-style storage identifiers.
/// </summary>
internal static class ClaimFailureCodes
{
    public const string PrimaryScopeAlreadyClaimed = "primary-scope-already-claimed";
    public const string AdoProjectionFailed = "ado-projection-failed";
    public const string ConcurrentClaimWrite = "concurrent-claim-write";
    public const string AttachmentLinkFailed = "attachment-link-failed";
    public const string ReleaseAdoProjectionFailed = "release-ado-projection-failed";
    public const string AttachmentUnlinkFailed = "attachment-unlink-failed";
    public const string ClaimNotFound = "claim-not-found";
    public const string ClaimNotActive = "claim-not-active";
    public const string SchemaDrift = "schema-drift";
    public const string TupleMismatch = "tuple-mismatch";
    public const string HolderUnavailable = "holder-unavailable";
    public const string StorageUnavailable = "storage-unavailable";
    public const string InvalidRequest = "invalid-request";
}
