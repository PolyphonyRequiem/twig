namespace Twig.Domain.Services.Plan;

/// <summary>
/// The parser's output. <see cref="Plan"/>, <see cref="CanonicalJson"/> and
/// <see cref="Digest"/> are populated only when <see cref="IsValid"/> is true —
/// a partial plan is never surfaced to callers.
/// </summary>
public sealed record PlanValidationResult
{
    /// <summary>Every structured problem found, in document order.</summary>
    public required IReadOnlyList<PlanValidationIssue> Issues { get; init; }

    /// <summary>The parsed plan; null when any issue was raised.</summary>
    public PlanDefinition? Plan { get; init; }

    /// <summary>Canonical JSON form (sorted property names, compact UTF-8); null when invalid.</summary>
    public string? CanonicalJson { get; init; }

    /// <summary>Lowercase-hex SHA-256 of <see cref="CanonicalJson"/>'s UTF-8 bytes; null when invalid.</summary>
    public string? Digest { get; init; }

    /// <summary>True iff no issues were raised.</summary>
    public bool IsValid => Issues.Count == 0;
}
