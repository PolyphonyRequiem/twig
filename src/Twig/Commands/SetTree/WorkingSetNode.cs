using Twig.Domain.Aggregates;

namespace Twig.Commands.SetTree;

/// <summary>
/// One node of an annotated working-set forest (twig#277).
/// </summary>
/// <param name="Id">Work item id. Always present, even for a placeholder.</param>
/// <param name="Item">
/// The cached work item, or <see langword="null"/> when the id is in the working set
/// but absent from the local cache. Rendered as a visible placeholder rather than
/// dropped — the rest of the tree is still valid consent surface.
/// </param>
/// <param name="InWorkingSet">
/// <see langword="true"/> when the caller named this id. <see langword="false"/> for a
/// <em>connector</em>: an ancestor pulled in solely to join two set members, and for a
/// descendant pulled in by <c>--depth</c>.
/// </param>
/// <param name="Annotation">The caller's annotation, when one was supplied.</param>
/// <param name="Children">Child nodes, in cache order.</param>
internal sealed record WorkingSetNode(
    int Id,
    WorkItem? Item,
    bool InWorkingSet,
    TreeAnnotation? Annotation,
    IReadOnlyList<WorkingSetNode> Children)
{
    /// <summary>True when the id was requested but is not in the local cache.</summary>
    internal bool IsPlaceholder => Item is null;
}

/// <summary>
/// The result of resolving a working set into a forest of trees.
/// </summary>
/// <param name="Roots">Top-level structures, in the caller's id order.</param>
/// <param name="MissingIds">Requested ids absent from the local cache.</param>
internal sealed record WorkingSetForest(
    IReadOnlyList<WorkingSetNode> Roots,
    IReadOnlyList<int> MissingIds);
