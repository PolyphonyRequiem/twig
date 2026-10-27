namespace Twig.Domain.Services.Plan;

/// <summary>
/// The outcome of an apply pass. <see cref="Failed"/> is true iff at least one operation
/// ended in <see cref="PlanOperationState.Failed"/> or
/// <see cref="PlanOperationState.Indeterminate"/>.
/// </summary>
public sealed record PlanApplyResult
{
    /// <summary>The plan digest this run was scoped to.</summary>
    public required string Digest { get; init; }

    /// <summary>Per-operation journal rows after the pass.</summary>
    public required IReadOnlyList<PlanJournalOperation> Operations { get; init; }

    /// <summary>True when any operation is in a terminal-failure state.</summary>
    public required bool Failed { get; init; }

    /// <summary>
    /// Remote acknowledgments returned by this attempt whose local journal fence was lost,
    /// keyed by the existing operation id. These are transient evidence, not replacement
    /// journal rows or settlement receipts. Inspect the native write history before reconciliation;
    /// never retry the mutation because this evidence could not be added to the winning row.
    /// </summary>
    public IReadOnlyDictionary<string, string> UnsettledAcknowledgments { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Top-level error captured when apply refused to run at all — invalid file, digest
    /// mismatch, pending rows present, workspace drift — OR when a live Applying lease held
    /// by another actor was observed and the apply short-circuited without touching the row.
    /// Also reports an acknowledged remote effect whose local recording fence was lost;
    /// that outcome is unsettled, not a retryable pre-send refusal.
    /// Null when apply reached the per-operation loop and settled every row on its own;
    /// per-operation failures live on <see cref="PlanJournalOperation.Error"/>.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>Native capture settlement, distinct from the verified remote operation.</summary>
    internal string? DraftSettlement { get; init; }

    /// <summary>Why local draft intent was retained after a verified remote operation.</summary>
    internal string? DraftSettlementError { get; init; }
}
