using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
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

    internal static (int Done, int Total)? ComputeChildProgress(
        this IProcessConfigurationProvider? provider, IReadOnlyList<WorkItem> children)
    {
        if (children.Count == 0)
            return null;

        var done = 0;
        foreach (var child in children)
        {
            var typeConfig = provider.SafeGetConfiguration(child.Type.Value);
            var cat = StateCategoryResolver.Resolve(child.State, typeConfig?.StateEntries);
            if (cat == StateCategory.Resolved || cat == StateCategory.Completed)
                done++;
        }
        return (done, children.Count);
    }
}
