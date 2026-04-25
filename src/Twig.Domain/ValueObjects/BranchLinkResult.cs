namespace Twig.Domain.ValueObjects;

/// <summary>
/// Outcome of linking a branch to a work item as an ADO artifact link.
/// </summary>
public sealed record BranchLinkResult
{
    public required BranchLinkStatus Status { get; init; }
    public required int WorkItemId { get; init; }
    public required string BranchName { get; init; }
    public string ArtifactUri { get; init; } = "";
    public string ErrorMessage { get; init; } = "";

    public bool IsSuccess => Status is BranchLinkStatus.Linked or BranchLinkStatus.AlreadyLinked;
}

/// <summary>
/// Status of a branch link operation.
/// </summary>
public enum BranchLinkStatus
{
    /// <summary>Branch was successfully linked to the work item.</summary>
    Linked,

    /// <summary>Branch was already linked to the work item (idempotent).</summary>
    AlreadyLinked,

    /// <summary>Git project or repository ID could not be resolved.</summary>
    GitContextUnavailable,

    /// <summary>The link operation failed due to an API or network error.</summary>
    Failed,
}
