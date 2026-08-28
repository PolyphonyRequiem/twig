using Twig.Domain.Services.ChangeProposals;

namespace Twig.Domain.Services.Plan;

/// <summary>
/// A journalled plan row plus its operation rows. This is the authoritative execution
/// state — the plan file supplies bytes, the digest binds them here, the journal owns
/// the lifecycle.
/// </summary>
public sealed record PlanJournal
{
    /// <summary>Canonical digest of the plan bytes; the primary key.</summary>
    public required string Digest { get; init; }

    /// <summary>Original filesystem path the plan was imported from.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The canonical JSON stored at import so recovery does not need the source file.</summary>
    public required string CanonicalJson { get; init; }

    /// <summary>Workspace the plan targeted.</summary>
    public required PlanWorkspace Workspace { get; init; }

    /// <summary>Top-level lifecycle state.</summary>
    public required PlanOperationState State { get; init; }

    /// <summary>When the plan was imported (previewed).</summary>
    public required DateTimeOffset PreviewedAt { get; init; }

    /// <summary>When the plan was confirmed; null while still Planned.</summary>
    public DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>When the plan reached a terminal top-level state; null while in progress.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Top-level failure message, when present.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Whether a human or a model authorized this proposal, or <c>null</c> when the row
    /// predates authorization recording (design record T2 §5.3).
    /// <para>
    /// 🔴 <c>null</c> means "predates authorization recording", NEVER "unauthorized". The
    /// durable store is never dropped, so rows written before AB#743 are real audit history
    /// with no authorization columns to read; reporting them as unauthorized would invent a
    /// policy violation out of a schema change.
    /// </para>
    /// </summary>
    public ProposalAuthorizationMode? AuthorizationMode { get; init; }

    /// <summary>The identity that authorized the apply; null when the row predates recording.</summary>
    public string? AuthorizerIdentity { get; init; }

    /// <summary>The authorizer's rationale; null when none was supplied or the row predates recording.</summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// The canonical semantic review model exactly as serialized at authorization time — what
    /// the authorizer was shown, as distinct from <see cref="CanonicalJson"/>, which is what
    /// they authorized. Null when the row predates recording.
    /// </summary>
    public string? ReviewModelJson { get; init; }

    /// <summary>When the authorization was recorded; null when the row predates recording.</summary>
    public DateTimeOffset? AuthorizedAt { get; init; }

    /// <summary>Per-operation rows, in declaration order.</summary>
    public required IReadOnlyList<PlanJournalOperation> Operations { get; init; }
}
