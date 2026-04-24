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
    /// </summary>
    public bool Matches(AreaPath candidate)
    {
        if (string.Equals(candidate.Value, Path, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IncludeChildren)
            return false;

        // Under semantics: candidate is a child if it starts with Path + backslash
        return candidate.Value.StartsWith(Path + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the display label for the match semantics.
    /// </summary>
    public string SemanticsLabel => IncludeChildren ? "under" : "exact";

    public override string ToString() => $"{Path} ({SemanticsLabel})";
}
