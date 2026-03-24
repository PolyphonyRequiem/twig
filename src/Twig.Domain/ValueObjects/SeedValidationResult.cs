namespace Twig.Domain.ValueObjects;

/// <summary>
/// Result of validating a single seed against <see cref="SeedPublishRules"/>.
/// </summary>
public sealed class SeedValidationResult
{
    public int SeedId { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool Passed => Failures.Count == 0;
    public IReadOnlyList<SeedValidationFailure> Failures { get; init; } = [];
}

/// <summary>
/// A single validation failure: which rule was violated and a human-readable message.
/// </summary>
public readonly record struct SeedValidationFailure(string Rule, string Message);
