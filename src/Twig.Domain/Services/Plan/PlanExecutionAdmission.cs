using Twig.Domain.Services.Policy;

namespace Twig.Domain.Services.Plan;

/// <summary>Mechanical execution evidence, distinct from historical workflow sign-off.</summary>
public sealed record PlanExecutionAdmission
{
    public int Version { get; init; } = 1;
    public required string Digest { get; init; }
    public required PlanOrigin Origin { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public IReadOnlyList<PolicyDecision> PolicyDecisions { get; init; } = [];
    public string? Rationale { get; init; }
}

/// <summary>Outcome of a conditional, write-once admission record.</summary>
public enum PlanAdmissionRecordResult
{
    Recorded,
    Existing,
    Conflict,
}
