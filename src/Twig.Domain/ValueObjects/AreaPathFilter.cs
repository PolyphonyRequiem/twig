namespace Twig.Domain.ValueObjects;

/// <summary>
/// Value object that encapsulates an area-path match specification
/// supporting both exact (=) and subtree (UNDER) semantics.
/// </summary>
public readonly record struct AreaPathFilter(string Path, bool IncludeChildren)
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="candidate"/> matches this filter.
    /// Exact mode: only the exact path matches.
    /// Under mode: the exact path or any child path matches.
    /// Delegates subtree logic to <see cref="AreaPath.IsUnder"/> to avoid duplication.
    /// </summary>
    public bool Matches(AreaPath candidate)
    {
        if (!IncludeChildren)
            return string.Equals(candidate.Value, Path, StringComparison.OrdinalIgnoreCase);

        var filterPath = AreaPath.Parse(Path);
        return filterPath.IsSuccess && candidate.IsUnder(filterPath.Value);
    }

    /// <summary>
    /// Returns the display label for the match semantics.
    /// </summary>
    public string SemanticsLabel => IncludeChildren ? "under" : "exact";

    public override string ToString() => $"{Path} ({SemanticsLabel})";
}
