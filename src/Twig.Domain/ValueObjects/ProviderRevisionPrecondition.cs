namespace Twig.Domain.ValueObjects;

/// <summary>
/// The optimistic-concurrency precondition a provider mutation is bound to: the provider that
/// minted <see cref="Expected"/> and the opaque token itself (Feature #1178).
/// </summary>
/// <remarks>
/// <para>
/// Neutral code never parses <see cref="Expected"/>. Only the adapter for
/// <see cref="ProviderKind"/> may decode it, and an adapter must refuse a precondition minted
/// by another provider, or a token it does not consider canonical, before any transport call.
/// </para>
/// <para>
/// A missing precondition is not representable here: there is no default or zero token. A
/// caller with no authoritative revision must not construct one, and an adapter receiving
/// <see langword="null"/> refuses it as missing.
/// </para>
/// </remarks>
public sealed record ProviderRevisionPrecondition
{
    public ProviderRevisionPrecondition(string providerKind, ProviderRevisionToken expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        ArgumentNullException.ThrowIfNull(expected);
        ProviderKind = providerKind;
        Expected = expected;
    }

    /// <summary>The provider kind that minted <see cref="Expected"/>; compared ordinally.</summary>
    public string ProviderKind { get; }

    /// <summary>The opaque revision the target must still be at for the mutation to apply.</summary>
    public ProviderRevisionToken Expected { get; }
}
