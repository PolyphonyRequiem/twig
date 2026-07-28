using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Repairs orphaned and stale seed links after partial publishes or external changes.
/// 3 dependencies, 1 consumer (<see cref="SeedLinkRepairCommand"/>).
/// Retained as a separate orchestrator — focused scope with no overlap with other services.
/// </summary>
/// <remarks>
/// Renamed from <c>SeedReconcileOrchestrator</c> per wayfinder 0004 §4: this type is a
/// seed-ID garbage collector, not local/remote reconciliation, and the old name squatted
/// the concept 0004 names. It resolves stale negative aliases through the publish_id_map;
/// it never observes a remote revision and never consults <c>ConflictResolver</c>.
/// The <c>twig seed reconcile</c> CLI verb and the <c>twig_seed_reconcile</c> MCP tool
/// keep their public names — MCP surface is frozen by 0012 and the rename is type-level only.
/// </remarks>
public sealed class SeedLinkRepair
{
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPublishIdMapRepository _publishIdMapRepo;

    public SeedLinkRepair(
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
    public async Task<SeedLinkRepairResult> RepairAsync(CancellationToken ct = default)
    {
        var linksRepaired = 0;
        var linksRemoved = 0;
        var parentIdsFixed = 0;
        var warnings = new List<string>();

        // Step 1: Load all seed_links and publish ID mappings
        var links = await _seedLinkRepo.GetAllSeedLinksAsync(ct);
        var mappings = await _publishIdMapRepo.GetAllMappingsAsync(ct);
        // seed_links stores the negative display alias, so the lookup is built from Alias.
        // This is a *display-side* resolution, not a join: the mapping rows are keyed on
        // StagedIdentity, and a row whose alias predates the register is simply skipped rather
        // than matched to a plausible neighbour (0003 §4, §5a).
        var mapDict = mappings
            .Where(m => m.Alias is not null)
            .ToDictionary(m => m.Alias!.Value.Value, m => m.NewId);

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

        return new SeedLinkRepairResult
        {
            LinksRepaired = linksRepaired,
            LinksRemoved = linksRemoved,
            ParentIdsFixed = parentIdsFixed,
            Warnings = warnings,
        };
    }
}
