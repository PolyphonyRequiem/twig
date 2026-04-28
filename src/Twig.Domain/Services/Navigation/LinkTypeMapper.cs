using Twig.Domain.Services.Seed;

namespace Twig.Domain.Services.Navigation;

/// <summary>
/// Bidirectional mapper between user-facing friendly link type names
/// (e.g. "parent", "child") and ADO relation type reference names
/// (e.g. "System.LinkTypes.Hierarchy-Reverse").
/// </summary>
/// <remarks>
/// Unlike <see cref="SeedLinkTypeMapper"/> which maps seed-specific link constants,
/// this mapper is designed for published work item link operations where users
/// provide case-insensitive friendly names.
/// </remarks>
public static class LinkTypeMapper
{
    private static readonly Dictionary<string, string> FriendlyToAdo =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["parent"] = "System.LinkTypes.Hierarchy-Reverse",
            ["child"] = "System.LinkTypes.Hierarchy-Forward",
            ["related"] = "System.LinkTypes.Related",
            ["predecessor"] = "System.LinkTypes.Dependency-Reverse",
            ["successor"] = "System.LinkTypes.Dependency-Forward",
        };

    private static readonly Dictionary<string, string> AdoToFriendly =
        FriendlyToAdo.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// All supported user-facing friendly link type names.
    /// </summary>
    public static IReadOnlyList<string> SupportedTypes { get; } =
        FriendlyToAdo.Keys.ToArray();

    /// <summary>
    /// Tries to resolve a user-facing friendly name to the corresponding ADO relation type.
    /// Lookup is case-insensitive (OrdinalIgnoreCase).
    /// </summary>
    /// <returns><c>true</c> if the friendly name was recognized; otherwise <c>false</c>.</returns>
    public static bool TryResolve(string friendlyName, out string adoRelationType)
    {
        return FriendlyToAdo.TryGetValue(friendlyName, out adoRelationType!);
    }

    /// <summary>
    /// Resolves a user-facing friendly name to the corresponding ADO relation type.
    /// Lookup is case-insensitive (OrdinalIgnoreCase).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="friendlyName"/> is not a recognized link type.
    /// </exception>
    public static string Resolve(string friendlyName)
    {
        if (FriendlyToAdo.TryGetValue(friendlyName, out var adoType))
            return adoType;

        throw new ArgumentException(
            $"Unknown link type: '{friendlyName}'. Supported types: {string.Join(", ", SupportedTypes)}.",
            nameof(friendlyName));
    }

    /// <summary>
    /// Maps an ADO relation type reference name back to its user-facing friendly name.
    /// Lookup is case-insensitive (OrdinalIgnoreCase).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="adoRelationType"/> is not a recognized ADO relation type.
    /// </exception>
    public static string ToFriendlyName(string adoRelationType)
    {
        if (AdoToFriendly.TryGetValue(adoRelationType, out var friendly))
            return friendly;

        throw new ArgumentException(
            $"Unknown ADO relation type: '{adoRelationType}'.",
            nameof(adoRelationType));
    }

    /// <summary>
    /// Tries to map an ADO relation type reference name back to its user-facing friendly name.
    /// Lookup is case-insensitive (OrdinalIgnoreCase).
    /// </summary>
    /// <returns><c>true</c> if the ADO relation type was recognized; otherwise <c>false</c>.</returns>
    public static bool TryToFriendlyName(string adoRelationType, out string friendlyName)
    {
        return AdoToFriendly.TryGetValue(adoRelationType, out friendlyName!);
    }
}
