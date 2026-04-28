using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Repairs orphaned and stale seed_links and parent_id references using the publish_id_map.
/// Called by <c>twig seed reconcile</c>.
/// </summary>
public sealed class SeedReconcileOrchestrator
{
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPublishIdMapRepository _publishIdMapRepo;

    public SeedReconcileOrchestrator(
        ISeedLinkRepository seedLinkRepo,
        IWorkItemRepository workItemRepo,
        IPublishIdMapRepository publishIdMapRepo)
    {
        _seedLinkRepo = seedLinkRepo;
        _workItemRepo = workItemRepo;
        _publishIdMapRepo = publishIdMapRepo;
    }

    /// <summary>
    /// Scans all seed_links and work_item parent references, repairing stale negative-ID
    /// references via the publish_id_map and removing orphaned links.
    /// </summary>
    public async Task<SeedReconcileResult> ReconcileAsync(CancellationToken ct = default)
    {
        var linksRepaired = 0;
        var linksRemoved = 0;
        var parentIdsFixed = 0;
        var warnings = new List<string>();

        // Step 1: Load all seed_links and publish ID mappings
        var links = await _seedLinkRepo.GetAllSeedLinksAsync(ct);
        var mappings = await _publishIdMapRepo.GetAllMappingsAsync(ct);
        var mapDict = mappings.ToDictionary(m => m.OldId, m => m.NewId);

        // Step 2: Process each link for stale / orphaned references
        // Track IDs we've already remapped to avoid double-remapping
        var remappedIds = new HashSet<int>();

        foreach (var link in links)
        {
            var sourceStale = link.SourceId < 0 && !await _workItemRepo.ExistsByIdAsync(link.SourceId, ct);
            var targetStale = link.TargetId < 0 && !await _workItemRepo.ExistsByIdAsync(link.TargetId, ct);

            if (!sourceStale && !targetStale)
                continue;

            // Check if stale IDs can be remapped
            var sourceCanRemap = sourceStale && mapDict.ContainsKey(link.SourceId);
            var targetCanRemap = targetStale && mapDict.ContainsKey(link.TargetId);

            if (sourceStale && !sourceCanRemap || targetStale && !targetCanRemap)
            {
                // At least one endpoint is orphaned with no mapping → remove link
                await _seedLinkRepo.RemoveLinkAsync(link.SourceId, link.TargetId, link.LinkType, ct);
                linksRemoved++;
                continue;
            }

            // Remap stale IDs that have mappings
            if (sourceStale && sourceCanRemap && remappedIds.Add(link.SourceId))
            {
                await _seedLinkRepo.RemapIdAsync(link.SourceId, mapDict[link.SourceId], ct);
                linksRepaired++;
            }

            if (targetStale && targetCanRemap && remappedIds.Add(link.TargetId))
            {
                await _seedLinkRepo.RemapIdAsync(link.TargetId, mapDict[link.TargetId], ct);
                linksRepaired++;
            }
        }

        // Step 3: Fix stale parent_id references on work items
        var seeds = await _workItemRepo.GetSeedsAsync(ct);
        foreach (var seed in seeds)
        {
            if (seed.ParentId is null || seed.ParentId.Value >= 0)
                continue;

            var parentId = seed.ParentId.Value;
            if (await _workItemRepo.ExistsByIdAsync(parentId, ct))
                continue;

            if (mapDict.TryGetValue(parentId, out var newParentId))
            {
                await _workItemRepo.RemapParentIdAsync(parentId, newParentId, ct);
                parentIdsFixed++;
            }
            else
            {
                warnings.Add($"Seed #{seed.Id} references parent #{parentId} which was discarded without publishing.");
            }
        }

        return new SeedReconcileResult
        {
            LinksRepaired = linksRepaired,
            LinksRemoved = linksRemoved,
            ParentIdsFixed = parentIdsFixed,
            Warnings = warnings,
        };
    }
}
