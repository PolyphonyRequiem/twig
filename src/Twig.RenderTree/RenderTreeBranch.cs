namespace Twig.RenderTree;

/// <summary>
/// One node of a <see cref="TreeView"/>. Carries a payload row and a (possibly
/// empty) list of child branches. Hierarchy is significant: human renderers
/// render box-drawing characters and indentation; JSON renderers emit nested
/// arrays; minimal renderers may flatten depth-first.
/// </summary>
public sealed record RenderTreeBranch(
    RenderRow Row,
    IReadOnlyList<RenderTreeBranch> Children);
