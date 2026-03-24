namespace Twig.Domain.ValueObjects;

/// <summary>
/// Outcome of reconciling stale seed links and parent references using the publish ID map.
/// </summary>
public sealed class SeedReconcileResult
{
    public int LinksRepaired { get; init; }
    public int LinksRemoved { get; init; }
    public int ParentIdsFixed { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool NothingToDo => LinksRepaired == 0 && LinksRemoved == 0 && ParentIdsFixed == 0 && Warnings.Count == 0;
}
