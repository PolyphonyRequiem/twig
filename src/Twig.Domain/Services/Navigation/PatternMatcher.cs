namespace Twig.Domain.Services.Navigation;

/// <summary>Exactly one candidate matched.</summary>
public sealed record SingleMatch(int Id);

/// <summary>Multiple candidates matched.</summary>
public sealed record MultipleMatches(IReadOnlyList<(int Id, string Title)> Candidates);

/// <summary>No candidate matched.</summary>
public sealed record NoMatch;

/// <summary>
/// Discriminated result of a pattern match against a list of candidates.
/// </summary>
public union MatchResult(SingleMatch, MultipleMatches, NoMatch);

/// <summary>
/// Matches a user-supplied pattern (numeric ID or substring) against a list of candidates.
/// </summary>
public static class PatternMatcher
{
    /// <summary>
    /// Matches <paramref name="pattern"/> against <paramref name="candidates"/>.
    /// If <paramref name="pattern"/> is a numeric string, matches by exact ID.
    /// Otherwise, performs case-insensitive substring matching on titles.
    /// </summary>
    public static MatchResult Match(string? pattern, IReadOnlyList<(int Id, string Title)> candidates)
    {
        if (string.IsNullOrWhiteSpace(pattern) || candidates.Count == 0)
            return new NoMatch();

        // Numeric ID passthrough
        if (int.TryParse(pattern, out var id))
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Id == id)
                    return new SingleMatch(id);
            }

            return new NoMatch();
        }

        // Case-insensitive substring match
        var matches = new List<(int Id, string Title)>();
        foreach (var candidate in candidates)
        {
            if (candidate.Title.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                matches.Add(candidate);
        }

        return matches.Count switch
        {
            0 => new NoMatch(),
            1 => new SingleMatch(matches[0].Id),
            _ => new MultipleMatches(matches),
        };
    }
}
