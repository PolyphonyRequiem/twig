using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Extensions;

internal static class ProcessConfigExtensions
{
    // Returns null when provider is null, GetConfiguration() throws, or type is not found.
    internal static TypeConfig? SafeGetConfiguration(
        this IProcessConfigurationProvider? provider, string workItemType)
    {
        if (provider is null)
            return null;

        try
        {
            var config = provider.GetConfiguration();
            var parseResult = WorkItemType.Parse(workItemType);
            if (!parseResult.IsSuccess)
                return null;

            return config.TypeConfigs.TryGetValue(parseResult.Value, out var typeConfig)
                ? typeConfig
                : null;
        }
        catch
        {
            return null;
        }
    }
}
