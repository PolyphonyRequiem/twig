using Twig.Domain.ValueObjects;
using Twig.Domain.Services.Navigation;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Maps <see cref="SeedLinkTypes"/> constants to ADO relation type reference names.
/// </summary>
public static class SeedLinkTypeMapper
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        [SeedLinkTypes.ParentChild] = "System.LinkTypes.Hierarchy-Forward",
        [SeedLinkTypes.Blocks] = "System.LinkTypes.Dependency-Forward",
        [SeedLinkTypes.BlockedBy] = "System.LinkTypes.Dependency-Reverse",
        [SeedLinkTypes.DependsOn] = "System.LinkTypes.Dependency-Reverse",
        [SeedLinkTypes.DependedOnBy] = "System.LinkTypes.Dependency-Forward",
        [SeedLinkTypes.Related] = "System.LinkTypes.Related",
        [SeedLinkTypes.Successor] = "System.LinkTypes.Dependency-Forward",
        [SeedLinkTypes.Predecessor] = "System.LinkTypes.Dependency-Reverse",
    };

    /// <summary>
    /// Maps a seed link type constant to the corresponding ADO relation type reference name.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="seedLinkType"/> is not a recognized seed link type.</exception>
    public static string ToAdoRelationType(string seedLinkType)
    {
        if (Map.TryGetValue(seedLinkType, out var adoType))
            return adoType;

        throw new ArgumentException($"Unknown seed link type: '{seedLinkType}'.", nameof(seedLinkType));
    }

    /// <summary>
    /// Tries to map a seed link type constant to the corresponding ADO relation type reference name.
    /// Returns <c>false</c> if the link type is not recognized.
    /// </summary>
    public static bool TryToAdoRelationType(string seedLinkType, out string adoRelationType)
    {
        return Map.TryGetValue(seedLinkType, out adoRelationType!);
    }
}
