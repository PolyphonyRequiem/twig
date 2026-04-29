namespace Twig.Domain.ValueObjects;

/// <summary>
/// Represents a node in the project area path classification tree.
/// Hierarchical — each node may have child nodes.
/// </summary>
public sealed record AreaTreeNode(
    string Name,
    string Path,
    IReadOnlyList<AreaTreeNode> Children);
