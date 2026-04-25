namespace Twig.Domain.ReadModels;

/// <summary>
/// Read model carrying the result of verifying all descendants of a work item
/// are in terminal states (Completed, Resolved, or Removed).
/// </summary>
public sealed record DescendantVerificationResult(
    int RootId,
    bool Verified,
    int TotalChecked,
    IReadOnlyList<IncompleteItem> Incomplete);

/// <summary>
/// A descendant work item that is not yet in a terminal state.
/// </summary>
public sealed record IncompleteItem(
    int Id,
    string Title,
    string Type,
    string State,
    int? ParentId,
    int Depth);
