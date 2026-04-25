namespace Twig.Domain.ValueObjects;

/// <summary>
/// Outcome of publishing all seeds via <c>PublishAllAsync</c>.
/// Aggregates individual <see cref="SeedPublishResult"/> entries and cycle errors.
/// </summary>
public sealed class SeedPublishBatchResult
{
    public IReadOnlyList<SeedPublishResult> Results { get; init; } = [];
    public IReadOnlyList<string> CycleErrors { get; init; } = [];

    public bool HasErrors => CycleErrors.Count > 0 || Results.Any(r => r.Status == SeedPublishStatus.Error);
    public int CreatedCount => Results.Count(r => r.Status == SeedPublishStatus.Created);
    public int SkippedCount => Results.Count(r => r.Status == SeedPublishStatus.Skipped);
}
