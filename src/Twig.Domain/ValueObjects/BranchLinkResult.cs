namespace Twig.Domain.ValueObjects;

/// <summary>Branch was successfully linked to the work item.</summary>
public sealed record Linked(
    int WorkItemId,
    string BranchName,
    string ArtifactUri);

/// <summary>Branch was already linked to the work item (idempotent).</summary>
public sealed record AlreadyLinked(
    int WorkItemId,
    string BranchName,
    string ArtifactUri);

/// <summary>Git project or repository ID could not be resolved.</summary>
public sealed record GitContextUnavailable(
    int WorkItemId,
    string BranchName,
    string ErrorMessage);

/// <summary>The link operation failed due to an API or network error.</summary>
public sealed record LinkFailed(
    int WorkItemId,
    string BranchName,
    string ArtifactUri,
    string ErrorMessage);

/// <summary>
/// Discriminated union representing the outcome of linking a branch to a work item.
/// Makes invalid states unrepresentable via exhaustive subtypes.
/// </summary>
public union BranchLinkResult(Linked, AlreadyLinked, GitContextUnavailable, LinkFailed);
