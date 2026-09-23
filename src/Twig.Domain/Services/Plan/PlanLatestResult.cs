namespace Twig.Domain.Services.Plan;

/// <summary>
/// The latest unresolved proposal header in the current workspace store.
/// </summary>
public sealed record PlanLatestResult
{
    /// <summary>True when an unresolved proposal row was found.</summary>
    public required bool Found { get; init; }

    /// <summary>Absolute source path recorded by preview.</summary>
    public string? File { get; init; }

    /// <summary>Canonical digest of the proposal bytes.</summary>
    public string? Digest { get; init; }

    /// <summary>Top-level journal state of the proposal.</summary>
    public PlanOperationState? State { get; init; }

    /// <summary>Candidate file/digest validation failure; no older proposal is substituted.</summary>
    public string? Error { get; init; }
}
