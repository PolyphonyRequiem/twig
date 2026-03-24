namespace Twig.Domain.ValueObjects;

/// <summary>
/// Outcome of publishing a single seed to Azure DevOps.
/// </summary>
public sealed class SeedPublishResult
{
    public int OldId { get; init; }
    public int NewId { get; init; }
    public string Title { get; init; } = string.Empty;
    public SeedPublishStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> LinkWarnings { get; init; } = [];
    public IReadOnlyList<SeedValidationFailure> ValidationFailures { get; init; } = [];

    public bool IsSuccess => Status is SeedPublishStatus.Created or SeedPublishStatus.Skipped or SeedPublishStatus.DryRun;
}

/// <summary>
/// Status of a single seed publish operation.
/// </summary>
public enum SeedPublishStatus
{
    Created,
    Skipped,
    DryRun,
    ValidationFailed,
    Error,
}
