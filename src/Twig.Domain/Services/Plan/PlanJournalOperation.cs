namespace Twig.Domain.Services.Plan;

/// <summary>
/// One row of the per-operation journal. Every declared operation gets exactly one row,
/// numbered by its position in the plan; that ordinal, together with the plan digest,
/// is what the lifecycle guards on.
/// </summary>
public sealed record PlanJournalOperation
{
    /// <summary>Zero-based position in the plan's operations array; preserves order.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Plan-unique operation id from the source file.</summary>
    public required string OpId { get; init; }

    /// <summary>The operation kind, mirrored from the source.</summary>
    public required PlanOperationKind Kind { get; init; }

    /// <summary>Current lifecycle state for this operation.</summary>
    public required PlanOperationState State { get; init; }

    /// <summary>Canonical per-operation JSON captured at import.</summary>
    public required string RequestJson { get; init; }

    /// <summary>When apply moved this row into <see cref="PlanOperationState.Applying"/>; null before then.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the operation reached <see cref="PlanOperationState.Applied"/>.</summary>
    public DateTimeOffset? AppliedAt { get; init; }

    /// <summary>When the operation reached <see cref="PlanOperationState.Verified"/>.</summary>
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>Success payload captured on Applied/Verified (e.g. new revision).</summary>
    public string? ResultJson { get; init; }

    /// <summary>Failure message captured on Failed/Indeterminate.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Non-fatal detail captured alongside a <see cref="PlanOperationState.Verified"/>
    /// outcome (AB#754). Today this names the server-generated fields ADO rewrote after the
    /// PATCH — the intended mutation is proven landed, but a generated timestamp or identity
    /// field does not equal the authored value.
    /// <para>
    /// It is deliberately a separate column from <see cref="Error"/>: a warning never implies
    /// a non-Verified state, and overloading <c>Error</c> would make a successful operation
    /// read as a failed one to every consumer that tests <c>Error is not null</c>.
    /// </para>
    /// </summary>
    public string? Warning { get; init; }
}
