using System.Text.Json.Serialization;
using Twig.Domain.Enums;

namespace Twig.Domain.Capabilities;

/// <summary>
/// How a provider adapter implements a Twig relationship capability. Static adapter data:
/// it never changes at runtime and never reflects whether a call succeeded.
/// </summary>
[JsonConverter(typeof(CapabilityImplementationJsonConverter))]
internal enum CapabilityImplementation
{
    Native,
    Emulated,
    Unsupported,
}

/// <summary>
/// Whether a declared capability can be used on the current connection right now.
/// Separate from <see cref="CapabilityImplementation"/> so that denied, missing or failed
/// discovery is never read as the provider lacking the capability.
/// </summary>
[JsonConverter(typeof(CapabilityAvailabilityJsonConverter))]
internal enum CapabilityAvailability
{
    Available,
    Unavailable,
    Unknown,
}

/// <summary>
/// Where Twig keeps the data for an emulated capability. Each provider adapter declares
/// this per capability; there is no global emulation-storage policy.
/// </summary>
[JsonConverter(typeof(EmulationStorageJsonConverter))]
internal enum EmulationStorage
{
    /// <summary>Not emulated (native or unsupported).</summary>
    None,

    /// <summary>Authoritative in Twig's durable local store; never projected to the provider.</summary>
    LocalOnly,

    /// <summary>Authoritative locally and also projected through the provider's own metadata.</summary>
    LocalWithProviderProjection,
}

/// <summary>Who owns a relationship Twig shows.</summary>
[JsonConverter(typeof(RelationshipOriginJsonConverter))]
internal enum RelationshipOrigin
{
    ProviderNative,
    TwigLocal,
}

/// <summary>
/// Publication status of a Twig-local relationship. Only a provider-projected capability
/// moves beyond <see cref="NotApplicable"/>; a local commit is never reported as shared.
/// </summary>
[JsonConverter(typeof(PublicationStateJsonConverter))]
internal enum PublicationState
{
    NotApplicable,
    Pending,
    Published,
    Verified,
    Conflict,
}

/// <summary>
/// One canonical token per member, used both by JSON and by persistence. Ordinal matching only.
/// </summary>
internal static class CapabilityTokens
{
    internal static readonly (CapabilityImplementation, string)[] Implementation =
    [
        (CapabilityImplementation.Native, "native"),
        (CapabilityImplementation.Emulated, "emulated"),
        (CapabilityImplementation.Unsupported, "unsupported"),
    ];

    internal static readonly (CapabilityAvailability, string)[] Availability =
    [
        (CapabilityAvailability.Available, "available"),
        (CapabilityAvailability.Unavailable, "unavailable"),
        (CapabilityAvailability.Unknown, "unknown"),
    ];

    internal static readonly (EmulationStorage, string)[] Storage =
    [
        (EmulationStorage.None, "none"),
        (EmulationStorage.LocalOnly, "local-only"),
        (EmulationStorage.LocalWithProviderProjection, "local-with-provider-projection"),
    ];

    internal static readonly (RelationshipOrigin, string)[] Origin =
    [
        (RelationshipOrigin.ProviderNative, "provider-native"),
        (RelationshipOrigin.TwigLocal, "twig-local"),
    ];

    internal static readonly (PublicationState, string)[] Publication =
    [
        (PublicationState.NotApplicable, "not-applicable"),
        (PublicationState.Pending, "pending"),
        (PublicationState.Published, "published"),
        (PublicationState.Verified, "verified"),
        (PublicationState.Conflict, "conflict"),
    ];

    public static string ToToken<TEnum>((TEnum Value, string Token)[] map, TEnum value)
        where TEnum : struct, Enum
    {
        foreach (var (candidate, token) in map)
        {
            if (EqualityComparer<TEnum>.Default.Equals(candidate, value))
                return token;
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, $"{typeof(TEnum).Name} has no canonical token.");
    }

    public static bool TryResolve<TEnum>((TEnum Value, string Token)[] map, string? token, out TEnum value)
        where TEnum : struct, Enum
    {
        foreach (var (candidate, canonical) in map)
        {
            if (string.Equals(token, canonical, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}

internal sealed class CapabilityImplementationJsonConverter : CanonicalEnumConverter<CapabilityImplementation>
{
    protected override (CapabilityImplementation, string)[] Mapping => CapabilityTokens.Implementation;
}

internal sealed class CapabilityAvailabilityJsonConverter : CanonicalEnumConverter<CapabilityAvailability>
{
    protected override (CapabilityAvailability, string)[] Mapping => CapabilityTokens.Availability;
}

internal sealed class EmulationStorageJsonConverter : CanonicalEnumConverter<EmulationStorage>
{
    protected override (EmulationStorage, string)[] Mapping => CapabilityTokens.Storage;
}

internal sealed class RelationshipOriginJsonConverter : CanonicalEnumConverter<RelationshipOrigin>
{
    protected override (RelationshipOrigin, string)[] Mapping => CapabilityTokens.Origin;
}

internal sealed class PublicationStateJsonConverter : CanonicalEnumConverter<PublicationState>
{
    protected override (PublicationState, string)[] Mapping => CapabilityTokens.Publication;
}
