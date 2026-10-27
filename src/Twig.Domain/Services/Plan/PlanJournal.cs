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

    /// <summary>
    /// When this digest was most recently previewed. Re-previewing refreshes this ordering
    /// timestamp without changing <see cref="PreviewedAt"/> or the journal state.
    /// </summary>
    public DateTimeOffset LastPreviewedAt { get; init; }

    /// <summary>When the plan was confirmed; null while still Planned.</summary>
    public DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>When the plan reached a terminal top-level state; null while in progress.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Top-level failure message, when present.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Historic authorization mode, hydrated only to preserve old journal reads. New applies
    /// leave this and the adjacent legacy audit fields unset.
    /// </summary>
    public ProposalAuthorizationMode? AuthorizationMode { get; init; }

    /// <summary>Historic authorizer identity, when recorded.</summary>
    public string? AuthorizerIdentity { get; init; }

    /// <summary>Historic authorization rationale, when recorded.</summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// Historic review-model snapshot, when recorded. New preview models remain transient.
    /// </summary>
    public string? ReviewModelJson { get; init; }

    /// <summary>When the historic authorization was recorded, when available.</summary>
    public DateTimeOffset? AuthorizedAt { get; init; }

    /// <summary>Authority captured on the first preview; null means unknown legacy origin.</summary>
    public PlanOrigin? Origin { get; init; }

    /// <summary>Write-once mechanical admission; legacy workflow audit fields remain unchanged.</summary>
    public PlanExecutionAdmission? ExecutionAdmission { get; init; }

    internal DraftProposalCapture? DraftCapture { get; init; }

    /// <summary>Per-operation rows, in declaration order.</summary>
    public required IReadOnlyList<PlanJournalOperation> Operations { get; init; }
}
