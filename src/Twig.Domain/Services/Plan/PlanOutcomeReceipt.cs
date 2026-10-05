using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Plan;

/// <summary>Established outcome, separate from the immutable execution lifecycle.</summary>
public enum PlanOutcomeKind
{
    Retired,
    Readback,
    Superseded,
}

/// <summary>Append-only settlement bound to the original effect and native authorizing authority.</summary>
public sealed record PlanOutcomeReceipt
{
    public required string ReceiptId { get; init; }
    public required string Digest { get; init; }
    public required string OpId { get; init; }
    public required PlanOutcomeKind Kind { get; init; }
    public required string RequestJson { get; init; }
    public PlanOrigin? Origin { get; init; }
    public required PlanOrigin AuthorizingOrigin { get; init; }
    public required ProposalAuthorization Authorization { get; init; }
    public required string EvidenceJson { get; init; }
    public string? ReplacementDigest { get; init; }
    public string? ReplacementOpId { get; init; }
    public StagedIdentity? PublishIdentity { get; init; }
    public string? PublishIntentRecordedAt { get; init; }
}

/// <summary>Result of an explicit native settlement; never reports retirement as application.</summary>
public sealed record PlanReconciliationResult
{
    public bool Settled { get; init; }
    public PlanOutcomeReceipt? Receipt { get; init; }
    public string? Error { get; init; }
}
