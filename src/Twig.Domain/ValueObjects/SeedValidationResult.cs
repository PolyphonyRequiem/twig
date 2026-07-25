namespace Twig.Domain.ValueObjects;

/// <summary>
/// Result of validating a single seed against <see cref="SeedPublishRules"/>.
/// </summary>
public sealed class SeedValidationResult
{
    public int SeedId { get; init; }
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// True when no rule was violated. Warnings are advisory and deliberately excluded:
    /// a warned-but-passing seed must still publish and must not fail <c>seed validate</c>.
    /// </summary>
    public bool Passed => Failures.Count == 0;

    public IReadOnlyList<SeedValidationFailure> Failures { get; init; } = [];

    /// <summary>
    /// Advisory findings that do not block publish and do not affect <see cref="Passed"/>.
    /// </summary>
    public IReadOnlyList<SeedValidationFailure> Warnings { get; init; } = [];
}

/// <summary>
/// A single validation failure: which rule was violated and a human-readable message.
/// </summary>
public readonly record struct SeedValidationFailure(string Rule, string Message);
