namespace Twig.Domain.ValueObjects;

/// <summary>
/// Discriminated union representing the outcome of linking a branch to a work item.
/// Makes invalid states unrepresentable via exhaustive subtypes.
/// </summary>
public abstract record BranchLinkResult
{
    private BranchLinkResult() { }

    /// <summary>Branch was successfully linked to the work item.</summary>
    public sealed record Linked(
        int WorkItemId,
        string BranchName,
        string ArtifactUri) : BranchLinkResult;

    /// <summary>Branch was already linked to the work item (idempotent).</summary>
    public sealed record AlreadyLinked(
        int WorkItemId,
        string BranchName,
        string ArtifactUri) : BranchLinkResult;

    /// <summary>Git project or repository ID could not be resolved.</summary>
    public sealed record GitContextUnavailable(
        int WorkItemId,
        string BranchName,
        string ErrorMessage) : BranchLinkResult;

    /// <summary>The link operation failed due to an API or network error.</summary>
    public sealed record Failed(
        int WorkItemId,
        string BranchName,
        string ArtifactUri,
        string ErrorMessage) : BranchLinkResult;
}
