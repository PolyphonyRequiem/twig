using Twig.Domain.Capabilities;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Provider adapter capability seam. The active adapter owns its static declaration and may
/// probe runtime availability without changing the declared implementation.
/// </summary>
internal interface IWorkItemProvider
{
    ProviderCapabilityDescriptor Capabilities { get; }

    /// <summary>
    /// Returns runtime availability evidence. The default empty evidence set is reported as
    /// Unknown / not-probed by <see cref="CapabilityReport.Create"/>.
    /// Implementations must represent authorization and transport failures as evidence rather
    /// than changing the provider's declared implementation.
    /// </summary>
    Task<IReadOnlyList<CapabilityAvailabilityEvidence>> ProbeCapabilitiesAsync(
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CapabilityAvailabilityEvidence>>([]);
}
