using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Reads provider snapshots through Twig's durable, provider-neutral local identities.
/// </summary>
/// <remarks>
/// This is a backend read seam only. Existing integer-keyed CLI, cache, and display routes are
/// unchanged and do not call this service.
/// </remarks>
public interface IProviderWorkItemReadService
{
    /// <summary>
    /// Reads a provider-qualified identity, creating its durable local identity mapping when
    /// necessary.
    /// </summary>
    Task<ProviderWorkItemReadResult> ReadByIdentityAsync(
        ExternalWorkItemIdentity identity,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the active provider's identity for a local item, or returns null when it has no
    /// identity in the active provider scope.
    /// </summary>
    Task<ProviderWorkItemReadResult?> ReadByLocalIdAsync(
        LocalWorkItemId id,
        CancellationToken ct = default);
}

/// <summary>A provider observation composed with the durable local field overlay.</summary>
/// <param name="Identity">The exact provider-qualified identity that was read.</param>
/// <param name="Local">The durable local record; its revision is only a local CAS counter.</param>
/// <param name="RevisionToken">
/// The provider-owned token returned by this observation, or null when the provider supplied
/// none. It is never inferred from <see cref="LocalWorkItemRecord.Revision"/>.
/// </param>
/// <param name="ProviderFields">The fields observed from the provider, without local overrides.</param>
/// <param name="Fields">Provider snapshot fields overlaid by locally held values.</param>
/// <param name="Relationships">The provider-qualified relationships from the snapshot.</param>
public sealed record ProviderWorkItemReadResult(
    ExternalWorkItemIdentity Identity,
    LocalWorkItemRecord Local,
    ProviderRevisionToken? RevisionToken,
    FieldValues ProviderFields,
    FieldValues Fields,
    IReadOnlyList<ProviderWorkItemRelationship> Relationships);
