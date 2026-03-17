using Twig.Domain.Aggregates;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Provides process configuration built from dynamic process type data.
/// Throws <see cref="InvalidOperationException"/> if no configuration is available.
/// </summary>
public interface IProcessConfigurationProvider
{
    ProcessConfiguration GetConfiguration();
}
