using Twig.Domain.Enums;

namespace Twig.Domain.Capabilities;

internal enum RelationshipRouteKind
{
    /// <summary>Write through the provider's native operation.</summary>
    Native,

    /// <summary>Write to Twig's local overlay, with the declared storage.</summary>
    Emulated,

    /// <summary>An explicitly requested local-only write for a provider-declared unsupported kind.</summary>
    LocalOnly,

    /// <summary>Do not write: the provider declares the kind unsupported.</summary>
    Refused,
}

/// <summary>The route a relationship mutation takes, decided from the declaration alone.</summary>
internal sealed record RelationshipRoute(RelationshipRouteKind Kind, EmulationStorage Storage, string? ReasonCode)
{
    public const string UnsupportedReason = "relationship-unsupported";
    public const string NotARelationshipReason = "relationship-kind-invalid";
    public const string EmulationUnavailableReason = "relationship-emulation-unavailable";
    public const string LocalOnlyRequiresUnsupportedReason = "relationship-local-only-requires-unsupported";
}

/// <summary>
/// Native-first routing. The route depends only on the static declaration, never on whether a
/// native call failed or on runtime availability: a native failure is reported, not silently
/// retried through emulation, and an unavailable capability is never rerouted locally.
/// </summary>
internal static class RelationshipRouting
{
    public static RelationshipRoute Decide(ProviderCapabilityDescriptor descriptor, LinkKind kind)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.Relationships.Any(r => r.Kind == kind))
            return new RelationshipRoute(RelationshipRouteKind.Refused, EmulationStorage.None, RelationshipRoute.NotARelationshipReason);

        var capability = descriptor.For(kind);
        return capability.Implementation switch
        {
            CapabilityImplementation.Native => new RelationshipRoute(RelationshipRouteKind.Native, EmulationStorage.None, null),
            CapabilityImplementation.Emulated => new RelationshipRoute(RelationshipRouteKind.Emulated, capability.Storage, null),
            _ => new RelationshipRoute(RelationshipRouteKind.Refused, EmulationStorage.None, RelationshipRoute.UnsupportedReason),
        };
    }

    /// <summary>
    /// Routes only an explicit local-only request, and only when the provider's static
    /// declaration says it does not implement the kind. Runtime unavailability is not
    /// considered here and never authorizes local storage.
    /// </summary>
    public static RelationshipRoute DecideExplicitLocalOnly(ProviderCapabilityDescriptor descriptor, LinkKind kind)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.Relationships.Any(r => r.Kind == kind))
            return new RelationshipRoute(RelationshipRouteKind.Refused, EmulationStorage.None, RelationshipRoute.NotARelationshipReason);

        return descriptor.For(kind).Implementation == CapabilityImplementation.Unsupported
            ? new RelationshipRoute(RelationshipRouteKind.LocalOnly, EmulationStorage.LocalOnly, null)
            : new RelationshipRoute(RelationshipRouteKind.Refused, EmulationStorage.None, RelationshipRoute.LocalOnlyRequiresUnsupportedReason);
    }

    /// <summary>
    /// The refusal every relationship-mutation surface reports when <paramref name="kind"/> has no
    /// native route, or <c>null</c> when the provider writes it natively. Emulated kinds are refused
    /// too until a Twig-local route is wired, so no surface can write the overlay ahead of the rest.
    /// </summary>
    public static string? RefusalMessage(ProviderCapabilityDescriptor descriptor, LinkKind kind)
    {
        var route = Decide(descriptor, kind);
        var kindToken = LinkKindTokens.ToToken(kind);
        return route.Kind switch
        {
            RelationshipRouteKind.Native => null,
            RelationshipRouteKind.Emulated =>
                $"{RelationshipRoute.EmulationUnavailableReason}: provider '{descriptor.ProviderKind}' emulates '{kindToken}' relationships, "
                + "but the Twig-local route is not available yet. Nothing was changed.",
            RelationshipRouteKind.LocalOnly =>
                $"The explicit Twig-local route is not a provider write for '{kindToken}'. Nothing was changed.",
            _ =>
                $"{route.ReasonCode}: provider '{descriptor.ProviderKind}' does not support '{kindToken}' relationships. "
                + "Nothing was changed.",
        };
    }
}
