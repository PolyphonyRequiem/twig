using Twig.Domain.Common;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Durable local registry for immutable provider target identities. It contains no
/// credentials or authorization state. Existing metadata changes only through the
/// explicit identity-confirmed revision CAS.
/// </summary>
internal interface IProviderTargetBindingStore
{
    Task<Result<ProviderTargetBinding?>> FindByIdAsync(
        ProviderTargetBindingId id,
        CancellationToken ct = default);

    Task<Result<ProviderTargetBinding?>> FindByIdentityAsync(
        ProviderTargetIdentity identity,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the existing row unchanged when the identity is already registered;
    /// locator and display metadata are used only when inserting a new row.
    /// </summary>
    Task<Result<ProviderTargetBinding>> GetOrCreateAsync(
        ProviderTargetIdentity identity,
        string? locator,
        string? displayName,
        CancellationToken ct = default);

    /// <summary>
    /// Refresh mutable metadata only after a provider observation has confirmed the
    /// same immutable identity and the caller's row revision still matches.
    /// </summary>
    Task<Result<ProviderTargetBinding>> UpdateMetadataAfterIdentityConfirmationAsync(
        ProviderTargetBindingId id,
        ProviderTargetIdentity confirmedIdentity,
        long expectedRevision,
        string? locator,
        string? displayName,
        CancellationToken ct = default);
}
