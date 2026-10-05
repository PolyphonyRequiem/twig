using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Mutable single-slot handoff for the bootstrap identity seam (GH#466).
/// <para>
/// <see cref="Twig.Infrastructure.DependencyInjection.NetworkServiceModule.AddTwigNetworkServices"/>
/// builds the <c>init</c>-only <see cref="IAuthenticationProvider"/> from the
/// <see cref="TwigConfiguration"/> discovered at DI-container build time — before the CLI has
/// parsed <c>twig init</c>'s own <c>org</c>/<c>project</c> arguments. On a fresh checkout (no
/// tracked <c>twig.json</c>, no <c>.twig/</c>) that startup configuration is empty, so the
/// bootstrap identity lookup resolved a <c>connectionRef</c> for <c>""/""</c> instead of the
/// endpoint the user explicitly requested — <c>InitCommand</c> separately computes the
/// effective endpoint from its own arguments, but had no way to hand it back to the provider
/// that was already captured.
/// </para>
/// <para>
/// This holder closes that gap without mutating the shared startup <see cref="TwigConfiguration"/>
/// singleton (which other registrations still read as "the configuration discovered at
/// startup") and without widening <c>CreateBootstrapProviderAsync</c>'s contract: 
/// <c>InitCommand</c> publishes the effective coordinates as soon as it has computed them,
/// and the deferred provider's admission factory — itself lazy, see
/// <see cref="DeferredBoundAuthenticationProvider"/> — reads the published value (falling
/// back to the startup configuration when nothing was published) the first time identity
/// resolution actually runs.
/// </para>
/// </summary>
internal sealed class BootstrapEndpointSelection
{
    private readonly object _sync = new();
    private TwigConfiguration? _effective;

    /// <summary>The explicitly published effective configuration, or <c>null</c> if none was published.</summary>
    public TwigConfiguration? Effective
    {
        get { lock (_sync) { return _effective; } }
    }

    /// <summary>
    /// Publishes the effective (explicitly requested) configuration for the next bootstrap
    /// identity resolution. Safe to call before the deferred provider has been admitted;
    /// a no-op for every consumer that never reads <see cref="Effective"/>.
    /// </summary>
    public void Publish(TwigConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_sync) { _effective = configuration; }
    }
}
