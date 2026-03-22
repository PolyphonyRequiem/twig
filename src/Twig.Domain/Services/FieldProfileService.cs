using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Pure computation service that scans cached work items to discover which fields
/// are actually populated, grouped by work item type. No API calls — operates
/// entirely on data already in the local cache.
/// </summary>
public static class FieldProfileService
{
    /// <summary>
    /// Fields that are always shown as core columns and should be excluded from
    /// dynamic column discovery.
    /// </summary>
    private static readonly HashSet<string> CoreFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id",
        "System.Title",
        "System.State",
        "System.WorkItemType",
        "System.AssignedTo",
        "System.IterationPath",
        "System.AreaPath",
    };

    /// <summary>
    /// Computes field profiles across all provided work items (ignoring type grouping
    /// for column selection purposes). Returns profiles sorted by fill rate descending.
    /// </summary>
    public static IReadOnlyList<FieldProfile> ComputeProfiles(IReadOnlyList<WorkItem> items)
    {
        if (items.Count == 0)
            return Array.Empty<FieldProfile>();

        var fieldStats = new Dictionary<string, FieldAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            foreach (var kvp in item.Fields)
            {
                if (CoreFields.Contains(kvp.Key))
                    continue;

                if (!fieldStats.TryGetValue(kvp.Key, out var acc))
                {
                    acc = new FieldAccumulator();
                    fieldStats[kvp.Key] = acc;
                }

                if (!string.IsNullOrWhiteSpace(kvp.Value))
                {
                    acc.NonNullCount++;
                    if (acc.SampleValues.Count < 3)
                        acc.SampleValues.Add(kvp.Value);
                }
            }
        }

        var totalItems = items.Count;
        var profiles = new List<FieldProfile>(fieldStats.Count);
        foreach (var kvp in fieldStats)
        {
            var fillRate = (double)kvp.Value.NonNullCount / totalItems;
            profiles.Add(new FieldProfile(kvp.Key, fillRate, kvp.Value.SampleValues));
        }

        profiles.Sort((a, b) => b.FillRate.CompareTo(a.FillRate));
        return profiles;
    }

    private sealed class FieldAccumulator
    {
        public int NonNullCount;
        public readonly List<string> SampleValues = new(3);
    }
}
