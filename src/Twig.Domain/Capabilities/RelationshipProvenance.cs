using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Capabilities;

/// <summary>
/// Derives the origin Twig reports for a relationship from the active provider's capability
/// descriptor, never from a constant. A link read back from the provider is
/// <see cref="RelationshipOrigin.ProviderNative"/> only when the descriptor declares that kind
/// native; anything else (emulated, unsupported, unmapped, or no descriptor) is unknown, because
/// a provider observation can never prove a Twig-local edge.
/// </summary>
internal static class RelationshipProvenance
{
    /// <summary>Maps a friendly non-hierarchy link type (<see cref="LinkTypes"/>) to its relationship kind.</summary>
    public static bool TryGetKind(string? friendlyLinkType, out LinkKind kind)
    {
        switch (friendlyLinkType)
        {
            case LinkTypes.Related:
                kind = LinkKind.Related;
                return true;
            case LinkTypes.Predecessor:
            case LinkTypes.Successor:
                kind = LinkKind.PredecessorSuccessor;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>Origin of a relationship of <paramref name="kind"/> observed from the provider.</summary>
    public static RelationshipOrigin? OfProviderObservation(ProviderCapabilityDescriptor? descriptor, LinkKind kind)
    {
        if (descriptor is null || !descriptor.Relationships.Any(r => r.Kind == kind))
            return null;

        return descriptor.For(kind).Implementation == CapabilityImplementation.Native
            ? RelationshipOrigin.ProviderNative
            : null;
    }

    /// <summary>Origin of a provider-observed link identified by its friendly link type.</summary>
    public static RelationshipOrigin? OfProviderObservation(ProviderCapabilityDescriptor? descriptor, string? friendlyLinkType)
        => TryGetKind(friendlyLinkType, out var kind) ? OfProviderObservation(descriptor, kind) : null;

    /// <summary>Canonical origin token, or <c>null</c> when the origin is unknown.</summary>
    public static string? ToToken(RelationshipOrigin? origin)
        => origin is { } value ? CapabilityTokens.ToToken(CapabilityTokens.Origin, value) : null;
}
