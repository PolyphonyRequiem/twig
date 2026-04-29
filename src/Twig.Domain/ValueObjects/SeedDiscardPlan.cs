namespace Twig.Domain.ValueObjects;

/// <summary>
/// Immutable plan describing which seeds will be deleted during a cascade discard.
/// Contains the target seed plus all descendant seeds reachable via ParentId chains.
/// </summary>
public sealed record SeedDiscardPlan
{
    /// <summary>The seed the user asked to discard.</summary>
    public required int TargetId { get; init; }

    /// <summary>Display title of the target seed (for confirmation prompts).</summary>
    public required string TargetTitle { get; init; }

    /// <summary>Target + all descendant IDs (includes <see cref="TargetId"/>).</summary>
    public required IReadOnlyList<int> AllIds { get; init; }

    /// <summary>Number of descendant seeds (excludes the target itself).</summary>
    public int DescendantCount => AllIds.Count - 1;

    /// <summary>Whether the target seed has any descendant seeds.</summary>
    public bool HasDescendants => DescendantCount > 0;
}
