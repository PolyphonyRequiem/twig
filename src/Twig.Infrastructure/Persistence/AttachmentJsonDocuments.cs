using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// On-disk shape for <c>.twig/layout.json</c> — the marker AB#736 §4.2.1 fixes.
/// Its presence distinguishes the new worktree-local layout from a stray or
/// legacy <c>.twig/</c> tree; missing marker triggers <c>layout-marker-missing</c>
/// on every managed read.
/// </summary>
internal sealed record LayoutMarkerDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int Version,
    string InitializedAt,
    string CreatedBy)
{
    public const string CurrentSchema = "twig-layout/v1";
    public const int CurrentVersion = 1;
}

/// <summary>
/// On-disk shape for <c>.twig/worktree.json</c> — the captured worktree
/// fingerprint (§4.2.3) that drift-check compares against a fresh git rev-parse.
/// </summary>
internal sealed record WorktreeFingerprintDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int Version,
    WorktreeFingerprintTuple WorktreeFingerprint)
{
    public const string CurrentSchema = "twig-worktree/v1";
    public const int CurrentVersion = 1;
}

/// <summary>
/// The three fields §3.2 fixes as identity. Stored in canonical order so a
/// re-serialize is byte-stable against the live git rev-parse tuple.
/// </summary>
internal sealed record WorktreeFingerprintTuple(
    string GitCommonDir,
    string WorktreeGitDir,
    string WorktreeRoot);

/// <summary>
/// On-disk shape for <c>.twig/attachment.json</c> — the primary scope attachment
/// plus the opaque claim reference (§4.2.2). Both blocks are independently
/// nullable. AB#738 writes <see cref="PrimaryScope"/>; AB#739 writes
/// <see cref="ActiveClaim"/>. The consumer only inspects <c>claimId</c> — every
/// other claim-record field lives in the system store.
/// <para>
/// <see cref="Revision"/> is a monotonic counter incremented on every write.
/// AB#737 §Attachment link ordering requires mint/reclaim/release to read the
/// document, capture the observed revision, and refuse the write when the
/// on-disk revision has advanced — a lost-update coordination signal that
/// surfaces as <c>attachment-version-mismatch</c>. The counter is opaque:
/// callers compare byte-equal, not by ordering, so a wrapped counter still
/// fails-loud.
/// </para>
/// </summary>
internal sealed record AttachmentDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int Version,
    long Revision,
    string ConnectionRef,
    AttachmentPrimaryScope? PrimaryScope,
    AttachmentActiveClaim? ActiveClaim)
{
    public const string CurrentSchema = "twig-attachment/v1";
    public const int CurrentVersion = 1;

    public static AttachmentDocument Empty(string connectionRef) =>
        new(CurrentSchema, CurrentVersion, Revision: 0, connectionRef, PrimaryScope: null, ActiveClaim: null);
}

/// <summary>
/// Primary-scope block for the attachment document. Carries the primary
/// scope kind explicitly so a future non-ADO scope round-trips through
/// AB#737 §Interface's link/unlink surface without collapsing onto the ADO
/// work-item id space.
/// </summary>
internal sealed record AttachmentPrimaryScope(
    string Kind,
    int WorkItemId,
    string WorkItemUrl,
    string AttachedAt);

internal sealed record AttachmentActiveClaim(string ClaimId, string MintedAt);
