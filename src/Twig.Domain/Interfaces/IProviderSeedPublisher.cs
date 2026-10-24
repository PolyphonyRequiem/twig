using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The provider mutation port a proposal's publish-seed operation dispatches through
/// (Feature #1178 AC (b)).
/// </summary>
/// <remarks>
/// <para>
/// Publishing is multi-step: create, links, then a local commit. A failure part-way is
/// therefore not a pre-transport refusal, and the proposal executor keeps classifying it as
/// indeterminate so intent and map readback can reconcile it.
/// </para>
/// <para>
/// Composition resolves this port from the workspace's selected provider and builds the
/// provider's publisher only when a seed is actually published. Only ADO implements it until
/// GitHub authoring (#1183); every other provider is refused with <c>provider-unavailable</c>.
/// Direct seed publish and create commands stay ADO-only.
/// </para>
/// </remarks>
public interface IProviderSeedPublisher
{
    /// <summary>Publishes the seed with local id <paramref name="seedId"/> (a negative id).</summary>
    Task<SeedPublishResult> PublishAsync(int seedId, CancellationToken ct = default);
}
