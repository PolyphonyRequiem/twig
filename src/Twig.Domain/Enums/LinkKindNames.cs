namespace Twig.Domain.Enums;

/// <summary>
/// Converts <see cref="LinkKind"/> values to and from their canonical stable names.
/// </summary>
public static class LinkKindNames
{
    internal static readonly (LinkKind Value, string Token)[] Mapping =
    [
        (LinkKind.ParentChild, "parent-child"),
        (LinkKind.PredecessorSuccessor, "predecessor-successor"),
        (LinkKind.Related, "related"),
        (LinkKind.Artifact, "artifact"),
    ];

    /// <summary>Gets the canonical string name used by JSON and persisted boundaries.</summary>
    public static string ToStableName(LinkKind kind)
    {
        foreach (var (value, token) in Mapping)
        {
            if (value == kind)
                return token;
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Link kind has no canonical name.");
    }

    /// <summary>Resolves a canonical string name using ordinal, exact-spelling comparison.</summary>
    public static bool TryParseStableName(string? token, out LinkKind kind)
    {
        foreach (var (value, canonical) in Mapping)
        {
            if (string.Equals(token, canonical, StringComparison.Ordinal))
            {
                kind = value;
                return true;
            }
        }

        kind = default;
        return false;
    }
}
