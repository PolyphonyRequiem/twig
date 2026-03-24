using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed view</c>: shows a dashboard of all local seeds
/// grouped by their parent work item.
/// </summary>
public sealed class SeedViewCommand(
    IWorkItemRepository workItemRepo,
    IFieldDefinitionStore fieldDefStore,
    ISeedLinkRepository seedLinkRepo,
    TwigConfiguration config,
    RenderingPipelineFactory renderingPipelineFactory)
{
    /// <summary>Display the seed dashboard.</summary>
    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var (fmt, renderer) = renderingPipelineFactory.Resolve(outputFormat);

        // Count writable fields for completeness calculation
        var fieldDefs = await fieldDefStore.GetAllAsync(ct);
        var totalWritableFields = 0;
        foreach (var def in fieldDefs)
        {
            if (!def.IsReadOnly)
                totalWritableFields++;
        }

        var staleDays = config.Seed.StaleDays;

        // Build link map: seed ID → list of links touching that seed
        var linkMap = await BuildLinkMapAsync(ct);

        if (renderer is not null)
        {
            await renderer.RenderSeedViewAsync(
                () => BuildGroupsAsync(ct),
                totalWritableFields,
                staleDays,
                ct,
                linkMap);
            return 0;
        }

        var groups = await BuildGroupsAsync(ct);
        Console.WriteLine(fmt.FormatSeedView(groups, totalWritableFields, staleDays, linkMap));
        return 0;
    }

    private async Task<IReadOnlyDictionary<int, IReadOnlyList<SeedLink>>?> BuildLinkMapAsync(CancellationToken ct)
    {
        var allLinks = await seedLinkRepo.GetAllSeedLinksAsync(ct);
        if (allLinks.Count == 0)
            return null;

        var map = new Dictionary<int, List<SeedLink>>();

        foreach (var link in allLinks)
        {
            GetOrAdd(link.SourceId).Add(link);
            GetOrAdd(link.TargetId).Add(link);
        }

        var result = new Dictionary<int, IReadOnlyList<SeedLink>>(map.Count);
        foreach (var kvp in map)
            result[kvp.Key] = kvp.Value;
        return result;

        List<SeedLink> GetOrAdd(int id)
        {
            if (!map.TryGetValue(id, out var list))
                map[id] = list = [];
            return list;
        }
    }

    private async Task<IReadOnlyList<SeedViewGroup>> BuildGroupsAsync(CancellationToken ct)
    {
        var seeds = await workItemRepo.GetSeedsAsync(ct);
        if (seeds.Count == 0)
            return Array.Empty<SeedViewGroup>();

        // Group seeds by ParentId — use string key to avoid nullable TKey constraint
        var parentedGroups = new Dictionary<int, List<WorkItem>>();
        List<WorkItem>? orphans = null;

        foreach (var seed in seeds)
        {
            if (seed.ParentId is null)
            {
                orphans ??= new List<WorkItem>();
                orphans.Add(seed);
            }
            else
            {
                if (!parentedGroups.TryGetValue(seed.ParentId.Value, out var list))
                {
                    list = new List<WorkItem>();
                    parentedGroups[seed.ParentId.Value] = list;
                }
                list.Add(seed);
            }
        }

        var result = new List<SeedViewGroup>();

        // Parented groups first
        foreach (var kvp in parentedGroups)
        {
            WorkItem? parent = null;
            try
            {
                parent = await workItemRepo.GetByIdAsync(kvp.Key, ct);
            }
            catch (OperationCanceledException)
            {
                throw; // Propagate cancellation
            }
            catch (Exception)
            {
                // Parent not in cache — proceed without metadata
            }

            result.Add(new SeedViewGroup(parent, kvp.Value));
        }

        // Orphan seeds last
        if (orphans is not null)
        {
            result.Add(new SeedViewGroup(null, orphans));
        }

        return result;
    }
}
