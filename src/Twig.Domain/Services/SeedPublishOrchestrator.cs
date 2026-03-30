using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Orchestrates the single-seed publish flow: validate → create in ADO → fetch back →
/// transactional local update (remap IDs, remap ParentId, delete+save) → link promotion → publish ID map.
/// Also supports batch publish via <see cref="PublishAllAsync"/> with topological ordering.
/// </summary>
public sealed class SeedPublishOrchestrator
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IPublishIdMapRepository _publishIdMapRepo;
    private readonly ISeedPublishRulesProvider _rulesProvider;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SeedLinkPromoter _linkPromoter;
    private readonly BacklogOrderer _backlogOrderer;

    public SeedPublishOrchestrator(
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        ISeedLinkRepository seedLinkRepo,
        IPublishIdMapRepository publishIdMapRepo,
        ISeedPublishRulesProvider rulesProvider,
        IUnitOfWork unitOfWork,
        BacklogOrderer backlogOrderer)
    {
        _workItemRepo = workItemRepo;
        _adoService = adoService;
        _seedLinkRepo = seedLinkRepo;
        _publishIdMapRepo = publishIdMapRepo;
        _rulesProvider = rulesProvider;
        _unitOfWork = unitOfWork;
        _linkPromoter = new SeedLinkPromoter(seedLinkRepo, adoService);
        _backlogOrderer = backlogOrderer;
    }

    /// <summary>
    /// Publishes a single seed to Azure DevOps.
    /// </summary>
    /// <param name="seedId">The negative seed ID to publish.</param>
    /// <param name="force">When true, bypasses validation.</param>
    /// <param name="dryRun">When true, returns a plan without making API calls.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SeedPublishResult> PublishAsync(
        int seedId,
        bool force = false,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        // Step 1: Guard — already published (positive ID)
        if (seedId > 0)
        {
            return new SeedPublishResult
            {
                OldId = seedId,
                NewId = seedId,
                Status = SeedPublishStatus.Skipped,
                Title = string.Empty,
            };
        }

        // Step 2: Load seed
        var seed = await _workItemRepo.GetByIdAsync(seedId, ct);

        // Step 3: Guard — seed not found or not a seed
        if (seed is null || !seed.IsSeed)
        {
            return new SeedPublishResult
            {
                OldId = seedId,
                Status = SeedPublishStatus.Error,
                ErrorMessage = seed is null
                    ? $"Seed {seedId} not found."
                    : $"Work item {seedId} is not a seed.",
            };
        }

        // Step 4: Guard — parent is an unpublished seed (negative ParentId)
        if (seed.ParentId.HasValue && seed.ParentId.Value < 0)
        {
            return new SeedPublishResult
            {
                OldId = seedId,
                Title = seed.Title,
                Status = SeedPublishStatus.Error,
                ErrorMessage = $"Parent seed {seed.ParentId.Value} must be published first.",
            };
        }

        // Step 5: Validate unless force
        if (!force)
        {
            var rules = await _rulesProvider.GetRulesAsync(ct);
            var validation = SeedValidator.Validate(seed, rules);
            if (!validation.Passed)
            {
                return new SeedPublishResult
                {
                    OldId = seedId,
                    Title = seed.Title,
                    Status = SeedPublishStatus.ValidationFailed,
                    ValidationFailures = validation.Failures,
                };
            }
        }

        // Step 6: Dry run — return plan without API calls
        if (dryRun)
        {
            return new SeedPublishResult
            {
                OldId = seedId,
                Title = seed.Title,
                Status = SeedPublishStatus.DryRun,
            };
        }

        // Step 7: Create in ADO
        var newId = await _adoService.CreateAsync(seed, ct);

        // Step 8: Fetch back the full ADO-populated item
        var fetchedItem = await _adoService.FetchAsync(newId, ct);

        // Step 9: Mark provenance
        fetchedItem = fetchedItem.WithIsSeed(true);

        // Step 10: Transactional local update
        var tx = await _unitOfWork.BeginAsync(ct);
        try
        {
            // 10a: Record publish mapping
            await _publishIdMapRepo.RecordMappingAsync(seedId, newId, ct);

            // 10b: Remap ID in seed_links
            await _seedLinkRepo.RemapIdAsync(seedId, newId, ct);

            // 10c: Remap ParentId in child seeds
            await _workItemRepo.RemapParentIdAsync(seedId, newId, ct);

            // 10d: Delete old seed row
            await _workItemRepo.DeleteByIdAsync(seedId, ct);

            // 10e: Save new item
            await _workItemRepo.SaveAsync(fetchedItem, ct);

            // 10f: Commit transaction
            await _unitOfWork.CommitAsync(tx, ct);
        }
        catch
        {
            await _unitOfWork.RollbackAsync(tx, ct);
            throw;
        }
        finally
        {
            await tx.DisposeAsync();
        }

        // Step 11: Promote seed links to ADO relations
        var linkWarnings = await _linkPromoter.PromoteLinksAsync(newId, ct);

        // Step 12: Best-effort backlog ordering
        await _backlogOrderer.TryOrderAsync(newId, seed.ParentId, ct);

        // Step 12b: Post-publish cache refresh — replace Rev 1 cached item with current server revision
        try
        {
            var refreshed = await _adoService.FetchAsync(newId, ct);
            await _workItemRepo.SaveAsync(refreshed, ct);
        }
        catch
        {
            // Non-fatal: the item was published successfully.
            // Stale cache is tolerable — the conflict retry helper covers it on next update.
        }

        // Step 13: Return success result
        return new SeedPublishResult
        {
            OldId = seedId,
            NewId = newId,
            Title = seed.Title,
            Status = SeedPublishStatus.Created,
            LinkWarnings = linkWarnings,
        };
    }

    /// <summary>
    /// Publishes all unpublished seeds in topological order.
    /// Re-loads each seed before publish to pick up remapped ParentId from prior publishes.
    /// </summary>
    /// <param name="force">When true, bypasses validation for all seeds.</param>
    /// <param name="dryRun">When true, returns a plan without making API calls.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SeedPublishBatchResult> PublishAllAsync(
        bool force = false,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        // Step 1: Load all seeds
        var seeds = await _workItemRepo.GetSeedsAsync(ct);
        if (seeds.Count == 0)
        {
            return new SeedPublishBatchResult { Results = [], CycleErrors = [] };
        }

        // Step 2: Load all seed_links
        var links = await _seedLinkRepo.GetAllSeedLinksAsync(ct);

        // Step 3: Build dependency graph and topological sort
        var (publishOrder, cyclicIds) = SeedDependencyGraph.Sort(seeds, links);

        // Detect cycles
        var cycleErrors = new List<string>();
        if (cyclicIds.Count > 0)
        {
            var cyclicList = string.Join(", ", cyclicIds.OrderBy(id => id));
            cycleErrors.Add($"Circular dependency detected among seeds: {cyclicList}. These seeds will not be published.");
        }

        // Step 4: Publish each seed in topological order
        var results = new List<SeedPublishResult>();
        foreach (var seedId in publishOrder)
        {
            // Re-load the seed to pick up any remapped ParentId from prior publishes
            var result = await PublishAsync(seedId, force, dryRun, ct);
            results.Add(result);
        }

        return new SeedPublishBatchResult
        {
            Results = results,
            CycleErrors = cycleErrors,
        };
    }
}
