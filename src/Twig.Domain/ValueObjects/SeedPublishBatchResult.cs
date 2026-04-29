namespace Twig.Domain.ValueObjects;

/// <summary>
/// Outcome of publishing all seeds via <c>PublishAllAsync</c>.
/// Aggregates individual <see cref="SeedPublishResult"/> entries, cycle errors, and pre-flight validation errors.
/// </summary>
public sealed class SeedPublishBatchResult
{
    public IReadOnlyList<SeedPublishResult> Results { get; init; } = [];
    public IReadOnlyList<string> CycleErrors { get; init; } = [];

    /// <summary>
    /// Errors discovered during pre-flight validation (e.g., orphaned parent references).
    /// When non-empty, no ADO API calls were made.
    /// </summary>
    public IReadOnlyList<string> PreFlightErrors { get; init; } = [];

    public bool HasErrors => CycleErrors.Count > 0 || PreFlightErrors.Count > 0
        || Results.Any(r => r.Status is SeedPublishStatus.Error or SeedPublishStatus.ValidationFailed);
    public int CreatedCount => Results.Count(r => r.Status == SeedPublishStatus.Created);
    public int SkippedCount => Results.Count(r => r.Status == SeedPublishStatus.Skipped);
}
