using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The durable register of staged seed identities (wayfinder 0014).
/// <para>
/// This lives in the durable store, not the disposable mirror: a <c>SchemaVersion</c> bump or
/// <c>twig init --force</c> must not be able to reach it. It replaces
/// <c>ISeedIdCounter</c> — there is no <c>Initialize</c> to forget, because
/// <see cref="StagedIdentity.New"/> needs no floor. The only thing that still needs a floor is
/// the decorative alias, and this register keeps that floor durably and monotonically.
/// </para>
/// </summary>
public interface IStagedIdentityRegistry
{
    /// <summary>
    /// Mints and durably records a fresh identity together with the next display alias.
    /// Self-contained — the caller supplies nothing and can forget nothing.
    /// </summary>
    Task<StagedSeedIdentity> MintAsync(CancellationToken ct = default);

    /// <summary>
    /// Retires an alias so it is never reissued (0003 §5a). The row is marked, never deleted:
    /// deleting it would let the alias floor walk backwards over an already-issued number.
    /// </summary>
    Task RetireAsync(StagedIdentity identity, CancellationToken ct = default);

    /// <summary>Looks up a registered identity by its display alias, or null if unknown.</summary>
    Task<StagedIdentity?> FindByAliasAsync(StagedAlias alias, CancellationToken ct = default);

    /// <summary>Looks up the display alias for an identity, or null if unregistered.</summary>
    Task<StagedAlias?> FindAliasAsync(StagedIdentity identity, CancellationToken ct = default);
}
