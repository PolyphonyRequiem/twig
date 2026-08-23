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
    /// Top-level error captured when apply refused to run at all — invalid file, digest
    /// mismatch, pending rows present, workspace drift — OR when a live Applying lease held
    /// by another actor was observed and the apply short-circuited without touching the row.
    /// Null when apply reached the per-operation loop and settled every row on its own;
    /// per-operation failures live on <see cref="PlanJournalOperation.Error"/>.
    /// </summary>
    public string? Error { get; init; }
}
