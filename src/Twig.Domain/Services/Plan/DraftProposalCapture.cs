using System.Text.Json.Serialization;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Plan;

/// <summary>
/// Immutable native precondition retained beside, never inside, canonical proposal bytes.
/// A capture is local intent, not authorization or permission to apply.
/// </summary>
internal sealed class DraftProposalCapture
{
    [JsonConstructor]
    public DraftProposalCapture(
        int version,
        RecoverableWorkItemDraft draft,
        int currentRevision,
        PlanOrigin origin,
        string operationId,
        string proposalDigest,
        string? evidenceFingerprint = null)
    {
        if (version is not (1 or 2) || (version == 2 && string.IsNullOrWhiteSpace(evidenceFingerprint))
            || (version == 1 && evidenceFingerprint is not null))
            throw new ArgumentOutOfRangeException(nameof(version));
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalDigest);
        if (currentRevision <= 0 || draft.HasConflicts || draft.BaselineRevision != currentRevision)
            throw new ArgumentException("Capture requires a conflict-free draft at its exact native baseline.");
        Version = version;
        Draft = draft;
        CurrentRevision = currentRevision;
        Origin = origin;
        OperationId = operationId;
        ProposalDigest = proposalDigest;
        EvidenceFingerprint = evidenceFingerprint;
    }

    public int Version { get; }
    public RecoverableWorkItemDraft Draft { get; }
    public int CurrentRevision { get; }
    public PlanOrigin Origin { get; }
    public string OperationId { get; }
    public string ProposalDigest { get; }
    public string? EvidenceFingerprint { get; }
}
