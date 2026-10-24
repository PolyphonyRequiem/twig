using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>The local identity a provider key resolved to.</summary>
/// <param name="Id">The provider-neutral local identity.</param>
/// <param name="Created">True when this call minted the identity and its mapping.</param>
public sealed record ExternalIdentityResolution(LocalWorkItemId Id, bool Created);

/// <summary>The last provider token observed for a mapped key.</summary>
/// <param name="Token">The opaque token; <see langword="null"/> when the provider supplied none.</param>
/// <param name="ObservedAt">When it was recorded.</param>
public sealed record ObservedProviderToken(ProviderRevisionToken? Token, DateTimeOffset ObservedAt);

/// <summary>
/// Durable map from provider identities (Epic #1167) to Twig-local identities (Epic #1168).
/// </summary>
/// <remarks>
/// <para>
/// The key is the full <see cref="ExternalWorkItemIdentity"/>, compared ordinally. A mapping is
/// never re-pointed or inferred: a provider identity that changes is a new key with a new local
/// identity unless something maps it explicitly.
/// </para>
/// <para>
/// Observed provider tokens are stored opaquely and are never compared to the local
/// compare-and-swap revision. Rows survive a mirror rebuild.
/// </para>
/// </remarks>
public interface IExternalIdentityMap
{
    /// <summary>
    /// Returns the local identity for <paramref name="identity"/>, atomically minting an empty
    /// local item and the mapping when none exists. Idempotent. An existing mapping's token is
    /// left unchanged; use <see cref="RecordObservedTokenAsync"/> to advance it.
    /// </summary>
    Task<ExternalIdentityResolution> ResolveAsync(ExternalWorkItemIdentity identity, ProviderRevisionToken? observedToken, CancellationToken ct = default);

    /// <summary>Returns the mapped local identity, or <see langword="null"/>; never mints.</summary>
    Task<LocalWorkItemId?> FindAsync(ExternalWorkItemIdentity identity, CancellationToken ct = default);

    /// <summary>Returns every provider identity mapped to <paramref name="id"/>, ordered ordinally.</summary>
    Task<IReadOnlyList<ExternalWorkItemIdentity>> GetExternalIdentitiesAsync(LocalWorkItemId id, CancellationToken ct = default);

    /// <summary>
    /// Records the latest observed token for a mapped key. Returns <see langword="false"/> when the
    /// key is unmapped; nothing is minted.
    /// </summary>
    Task<bool> RecordObservedTokenAsync(ExternalWorkItemIdentity identity, ProviderRevisionToken? token, CancellationToken ct = default);

    /// <summary>Returns the last observed token, or <see langword="null"/> when the key is unmapped.</summary>
    Task<ObservedProviderToken?> GetObservedTokenAsync(ExternalWorkItemIdentity identity, CancellationToken ct = default);
}
