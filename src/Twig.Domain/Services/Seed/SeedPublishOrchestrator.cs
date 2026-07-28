using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Executes transactional seed publishing with topological dependency ordering.
/// 8 dependencies, 1 consumer (<see cref="SeedPublishCommand"/>).
/// Retained as a separate orchestrator — the largest orchestrator by line count with complex transactional logic.
/// </summary>
public sealed class SeedPublishOrchestrator
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IWorkItemLinkRepository _workItemLinkRepo;
    private readonly IPublishIdMapRepository _publishIdMapRepo;
    private readonly ISeedPublishRulesProvider _rulesProvider;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SeedLinkPromoter _linkPromoter;
    private readonly BacklogOrderer _backlogOrderer;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IPublishIntentRepository? _publishIntentRepo;

    /// <summary>
    /// Creates an orchestrator that migrates staged pending changes onto the published ID and
    /// records the publish intent durably before the ADO call, closing the 7→10 window
    /// (wayfinder 0015, from 0001 §4).
    /// </summary>
    /// <remarks>
    /// <see cref="IPendingChangeStore"/> is required, not optional (wayfinder 0004 §4). The
    /// constructor overloads this replaced made correctness depend on every construction site
    /// picking the right one: without the pending store, publishing a seed carrying a staged
    /// note or field edit created the ADO work item and then failed locally, so every retry
    /// duplicated the remote item (PolyphonyRequiem/twig#270). A dependency correctness
    /// depends on is not optional.
    /// <para>
    /// <see cref="IPublishIntentRepository"/> deliberately stays nullable. 0004 §4 names only
    /// the <see cref="IPendingChangeStore"/> overloads, and requiring the intent ledger is a
    /// behavioural change — it forces every seed with a <c>StagedIdentity</c> down the
    /// intent-tracking path — which belongs to wayfinder 0015, not to this cleanup.
    /// </para>
    /// </remarks>
    public SeedPublishOrchestrator(
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        ISeedLinkRepository seedLinkRepo,
        IWorkItemLinkRepository workItemLinkRepo,
        IPublishIdMapRepository publishIdMapRepo,
        ISeedPublishRulesProvider rulesProvider,
        IUnitOfWork unitOfWork,
        BacklogOrderer backlogOrderer,
        IPendingChangeStore pendingChangeStore,
        IPublishIntentRepository? publishIntentRepo)
    {
        _workItemRepo = workItemRepo;
        _adoService = adoService;
        _seedLinkRepo = seedLinkRepo;
        _workItemLinkRepo = workItemLinkRepo;
        _publishIdMapRepo = publishIdMapRepo;
        _rulesProvider = rulesProvider;
        _unitOfWork = unitOfWork;
        _linkPromoter = new SeedLinkPromoter(seedLinkRepo, adoService);
        _backlogOrderer = backlogOrderer;
        _pendingChangeStore = pendingChangeStore;
        _publishIntentRepo = publishIntentRepo;
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

        var parentLinks = await _seedLinkRepo.GetLinksForItemAsync(seedId, ct);
        var resolvedSeed = ResolveParentLink(seed, parentLinks);
        if (!resolvedSeed.IsSuccess)
        {
            return new SeedPublishResult
            {
                OldId = seedId,
                Title = seed.Title,
                Status = SeedPublishStatus.Error,
                ErrorMessage = resolvedSeed.Error,
            };
        }

        if (resolvedSeed.Value.ParentId != seed.ParentId)
        {
            seed = resolvedSeed.Value;
            await _workItemRepo.SaveAsync(seed, ct);
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

        var canonicalFieldFailures = SeedValidator.ValidateCanonicalFields(seed);
        if (canonicalFieldFailures.Count > 0)
        {
            return new SeedPublishResult
            {
                OldId = seedId,
                Title = seed.Title,
                Status = SeedPublishStatus.ValidationFailed,
                ValidationFailures = canonicalFieldFailures,
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

        // Step 7: Record intent durably BEFORE the ADO call, then make the call (0001 §4).
        //
        // THIS IS THE 7->10 WINDOW. The create at this step produces remote state; the local
        // half is not committed until step 10. A crash in between used to orphan a real ADO
        // work item with no local trace at all, so every retry created another duplicate
        // (PolyphonyRequiem/twig#270). #270 fixed the FK ordering *inside* step 10; the window
        // itself stayed open until this ticket.
        //
        // The intent is written outside the step-10 transaction ON PURPOSE. A record that
        // rolled back with the local half would be erased by exactly the crash it exists to
        // survive — it must outlive the failure to be evidence of it.
        //
        // A seed with no StagedIdentity predates 0014 and cannot be keyed, so it takes the old
        // unprotected path rather than being silently given a fresh identity that would not
        // match anything already in ADO.
        int newId;
        var identity = seed.StagedIdentity;
        var intentIsTracked = false;

        if (_publishIntentRepo is not null && identity is { } intentIdentity)
        {
            var intent = await _publishIntentRepo.RecordIntentAsync(
                intentIdentity, seed.Title, seed.Type.Value, ct);

            // A prior attempt may have created the item before dying. Two places can hold that
            // evidence, and BOTH must be consulted before creating anything.
            //
            // 1. The ledger itself. If a previous attempt completed the intent and then died in
            //    step 10, the row already names the ADO id. Reading it back is cheaper and
            //    strictly more reliable than re-deriving it from ADO — and it is the read path
            //    this ledger was built to serve. (Before review, nothing read it: the ledger was
            //    write-only, which is precisely why the rollback path duplicated.)
            // 2. Failing that, ask ADO. The tag is a single constant, so it only NARROWS to what
            //    twig had in flight; title + type + the intent's own RecordedAt identify which
            //    item is this create.
            var alreadyLanded = intent.PublishedId
                ?? await _adoService.FindPublishedIntentAsync(intent, ct);

            newId = alreadyLanded
                ?? await _adoService.CreateAsync(
                    seed.ToCreateRequest() with { StampIntentTag = true },
                    ct);

            // Record the outcome immediately, still outside the transaction. From here on the
            // remote item is accounted for even if every step below fails.
            await _publishIntentRepo.CompleteIntentAsync(intentIdentity, newId, ct);

            // The tag is NOT stripped here. It marks in-flight state, and the publish is still
            // in flight until the step-10 transaction commits — which is ~30 lines below. An
            // earlier version cleared it at this point, which disarmed the guard for exactly the
            // window it exists to protect: a rollback at step 10 left an orphan with no tag, so
            // the recovery query could not narrow to it and the retry duplicated. See the
            // post-commit strip after step 10.
            intentIsTracked = true;
        }
        else
        {
            newId = await _adoService.CreateAsync(seed.ToCreateRequest(), ct);
        }

        // Step 8: Fetch back the full ADO-populated item
        var fetchedItem = await _adoService.FetchAsync(newId, ct);
        var parentPersistenceError = seed.ParentId.HasValue && fetchedItem.ParentId != seed.ParentId
            ? $"Work item #{newId} was created, but ADO did not persist intended parent #{seed.ParentId}."
            : null;

        // Step 9: Clear seed flag — item is now a published ADO work item.
        // Provenance is tracked via publish_id_map (old negative ID → new positive ID).
        fetchedItem = fetchedItem.WithIsSeed(false);

        // Step 10: Transactional local update
        var tx = await _unitOfWork.BeginAsync(ct);
        try
        {
            // 10a: Record publish mapping
            // 10a: Record publish mapping, keyed on the durable identity (wayfinder 0014).
            // The negative alias is no longer the key, so a cache rebuild cannot reissue it to
            // a different seed and make this lookup resolve to a previous owner (#280).
            if (seed.StagedIdentity is { } stagedIdentity)
                await _publishIdMapRepo.RecordMappingAsync(stagedIdentity, newId, ct);

            // 10b: Remap ID in seed_links
            await _seedLinkRepo.RemapIdAsync(seedId, newId, ct);

            // 10c: Remap ParentId in child seeds
            await _workItemRepo.RemapParentIdAsync(seedId, newId, ct);

            // 10d: Save new item.
            // Ordered BEFORE the remap/delete below, unlike the pre-#270 sequence. This was
            // mandatory while pending_changes carried an FK to work_items(id); wayfinder 0013
            // deleted that FK, so the order now expresses intent rather than a constraint.
            await _workItemRepo.SaveAsync(fetchedItem, ct);

            // 10e: Migrate staged notes / field edits from the seed ID onto the published ID.
            // pending_changes is the one referencing table the publish path used to forget: its
            // FK kept the seed row alive, so DeleteByIdAsync threw FOREIGN KEY constraint failed,
            // the local transaction rolled back, and the ADO item created in Step 7 — outside
            // this transaction — was orphaned. Every retry then made another duplicate
            // (PolyphonyRequiem/twig#270). The FK is gone, but the migration stays: clearing the
            // rows would fix the crash while silently destroying an unpushed note, and they
            // still need to flush onto the published ID on the next sync.
            await _pendingChangeStore.RemapWorkItemIdAsync(seedId, newId, ct);

            // 10f: Delete old seed row
            await _workItemRepo.DeleteByIdAsync(seedId, ct);

            // 10g: Commit transaction
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

        // Step 10h: the local half is COMMITTED, so the publish is no longer in flight — only
        // now is it safe to drop the in-flight tag.
        //
        // Ordering is the whole point. Stripping it before the transaction (as an earlier
        // version did) disarms the guard for precisely the window it exists to protect: a
        // rollback above would leave a real ADO item carrying no tag, `FindPublishedIntentAsync`
        // could not narrow to it, and the retry would create a duplicate — #270, reintroduced
        // through its own fix.
        //
        // Best-effort: the publish has succeeded by now, so a failure here must not fail it. A
        // leftover tag is cosmetic, and the ledger row (which survives independently) still
        // names the id, so recovery does not depend on this succeeding.
        if (intentIsTracked)
        {
            try
            {
                await _adoService.ClearIntentTagAsync(newId, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Intentionally swallowed — see above.
            }
        }

        // Step 11: Promote seed links to ADO relations
        var linkWarnings = await _linkPromoter.PromoteLinksAsync(newId, ct);

        if (parentPersistenceError is not null)
        {
            return new SeedPublishResult
            {
                OldId = seedId,
                NewId = newId,
                Title = seed.Title,
                Status = SeedPublishStatus.Error,
                ErrorMessage = parentPersistenceError,
                LinkWarnings = linkWarnings,
            };
        }

        // Step 12: Best-effort backlog ordering
        await _backlogOrderer.TryOrderAsync(newId, seed.ParentId, ct);

        // Step 12b: Post-publish cache refresh — replace Rev 1 cached item with current server revision
        try
        {
            var (refreshed, refreshedLinks) = await _adoService.FetchWithLinksAsync(newId, ct);
            refreshed = refreshed.WithIsSeed(false);
            await _workItemRepo.SaveAsync(refreshed, ct);
            await _workItemLinkRepo.SaveLinksAsync(newId, refreshedLinks, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            linkWarnings =
            [
                .. linkWarnings,
                $"Work item #{newId} was published, but relationship cache refresh failed: {ex.Message}",
            ];
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

        var resolvedSeeds = new List<WorkItem>(seeds.Count);
        var parentLinkErrors = new List<string>();
        foreach (var seed in seeds)
        {
            var resolvedSeed = ResolveParentLink(seed, links);
            if (!resolvedSeed.IsSuccess)
            {
                parentLinkErrors.Add(resolvedSeed.Error);
                continue;
            }

            if (resolvedSeed.Value.ParentId != seed.ParentId)
                await _workItemRepo.SaveAsync(resolvedSeed.Value, ct);

            resolvedSeeds.Add(resolvedSeed.Value);
        }

        if (parentLinkErrors.Count > 0)
        {
            return new SeedPublishBatchResult
            {
                Results = [],
                CycleErrors = [],
                PreFlightErrors = parentLinkErrors,
            };
        }

        seeds = resolvedSeeds;

        // Step 3: Build dependency graph and topological sort
        var (publishOrder, cyclicIds) = SeedDependencyGraph.Sort(seeds, links);

        // ═══════════════════════════════════════════════════════════
        //  Pre-flight validation — abort entire batch before any ADO calls
        // ═══════════════════════════════════════════════════════════

        // Check 1: Cycle detection (always runs, even with --force)
        if (cyclicIds.Count > 0)
        {
            var cyclicList = string.Join(", ", cyclicIds.OrderBy(id => id));
            return new SeedPublishBatchResult
            {
                Results = [],
                CycleErrors = [$"Circular dependency detected among seeds: {cyclicList}. These seeds will not be published."],
                PreFlightErrors = [],
            };
        }

        // Checks 2, 3 & 4 (skipped when force=true)
        if (!force)
        {
            var preFlightErrors = new List<string>();
            var validationResults = new List<SeedPublishResult>();
            var rules = await _rulesProvider.GetRulesAsync(ct);
            var seedIds = new HashSet<int>(seeds.Select(s => s.Id));

            foreach (var seed in seeds)
            {
                // Check 2: SeedValidator validation
                var validation = SeedValidator.Validate(seed, rules);
                if (!validation.Passed)
                {
                    validationResults.Add(new SeedPublishResult
                    {
                        OldId = seed.Id,
                        Title = seed.Title,
                        Status = SeedPublishStatus.ValidationFailed,
                        ValidationFailures = validation.Failures,
                    });
                }

                // Check 3: Parent reference resolution — negative ParentId must exist in batch
                if (seed.ParentId.HasValue && seed.ParentId.Value < 0 && !seedIds.Contains(seed.ParentId.Value))
                {
                    preFlightErrors.Add(
                        $"Seed {seed.Id} ('{seed.Title}') references parent seed {seed.ParentId.Value} which is not in the current batch. Remove the parent reference or include the parent seed.");
                }

                // Check 4: Negative ID escape guard (I-2)
                var escapeFailures = SeedIdEscapeValidator.Validate(seed, seedIds);
                foreach (var failure in escapeFailures)
                {
                    preFlightErrors.Add(
                        $"Seed {seed.Id} ('{seed.Title}'): {failure.Message}");
                }
            }

            if (validationResults.Count > 0 || preFlightErrors.Count > 0)
            {
                return new SeedPublishBatchResult
                {
                    Results = validationResults,
                    CycleErrors = [],
                    PreFlightErrors = preFlightErrors,
                };
            }
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
            CycleErrors = [],
            PreFlightErrors = [],
        };
    }

    // Delegates to the shared rule so publish and `seed validate` agree (see SeedParentResolver).
    private static Result<WorkItem> ResolveParentLink(
        WorkItem seed,
        IReadOnlyList<SeedLink> links) =>
        SeedParentResolver.Resolve(seed, links);
}
