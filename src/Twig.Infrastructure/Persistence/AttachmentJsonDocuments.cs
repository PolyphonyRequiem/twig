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
/// nullable. AB#738 writes <see cref="PrimaryScope"/>; AB#739 will write
/// <see cref="ActiveClaim"/>. The consumer only inspects <c>claimId</c> — every
/// other claim-record field lives in the system store.
/// </summary>
internal sealed record AttachmentDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int Version,
    string ConnectionRef,
    AttachmentPrimaryScope? PrimaryScope,
    AttachmentActiveClaim? ActiveClaim)
{
    public const string CurrentSchema = "twig-attachment/v1";
    public const int CurrentVersion = 1;

    public static AttachmentDocument Empty(string connectionRef) =>
        new(CurrentSchema, CurrentVersion, connectionRef, PrimaryScope: null, ActiveClaim: null);
}

internal sealed record AttachmentPrimaryScope(int WorkItemId, string WorkItemUrl, string AttachedAt);
internal sealed record AttachmentActiveClaim(string ClaimId, string MintedAt);
