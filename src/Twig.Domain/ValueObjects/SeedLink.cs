namespace Twig.Domain.ValueObjects;

/// <summary>
/// Immutable value object representing a virtual typed link between two seeds or seed/ADO items.
/// </summary>
public readonly record struct SeedLink(
    int SourceId,
    int TargetId,
    string LinkType,
    DateTimeOffset CreatedAt);

/// <summary>
/// Constants for supported seed link types and reverse-mapping logic.
/// </summary>
public static class SeedLinkTypes
{
    public const string ParentChild = "parent-child";
    public const string Blocks = "blocks";
    public const string BlockedBy = "blocked-by";
    public const string DependsOn = "depends-on";
    public const string DependedOnBy = "depended-on-by";
    public const string Related = "related";
    public const string Successor = "successor";
    public const string Predecessor = "predecessor";

    public static readonly IReadOnlyList<string> All = new[]
    {
        ParentChild, Blocks, BlockedBy, DependsOn, DependedOnBy, Related, Successor, Predecessor
    };

    /// <summary>
    /// Returns the reverse/inverse link type for directional types, or <c>null</c> for symmetric/non-reversible types.
    /// </summary>
    public static string? GetReverse(string linkType) => linkType switch
    {
        Blocks => BlockedBy,
        BlockedBy => Blocks,
        DependsOn => DependedOnBy,
        DependedOnBy => DependsOn,
        Successor => Predecessor,
        Predecessor => Successor,
        ParentChild => null,
        Related => null,
        _ => null,
    };
}
