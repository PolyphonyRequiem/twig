using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// Test-friendly seam for the seed publish delegate — takes the seed's negative id and
/// returns the orchestrator's classified outcome. Kept as a delegate rather than an
/// interface so production wires directly to
/// <see cref="SeedPublishOrchestrator.PublishAsync(int, bool, bool, CancellationToken)"/>
/// with no shim class in the way.
/// </summary>
internal delegate Task<SeedPublishResult> SeedPublishInvoker(int seedId, CancellationToken ct);

/// <summary>
/// Owns the plan pipeline's publish-seed operation end-to-end: pre-apply fingerprint
/// attestation, orchestrator delegation, applying-state recovery, and remote verification.
/// Extracted from <see cref="PlanOperationExecutor"/> so the invariants that keep a plan
/// bound to the exact seed shape the author saw can be tested without instantiating the
/// full orchestrator dependency graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order matters on a fresh Confirmed publish-seed.</b> A pre-existing map row keyed on
/// the plan's staged identity is not by itself proof this plan is what produced it — an
/// external publisher may have raced ahead against a since-edited seed. Recomputing the
/// fingerprint over the current cache BEFORE consulting the map catches that drift; a plan
/// that no longer describes the seed it named fails closed instead of ratifying whatever
/// somebody else wrote.
/// </para>
/// <para>
/// <b>Recovery from Applying is authoritative on both ledgers.</b> The id map is the source
/// of truth for a committed publish; the intent is the source of truth for what the wire
/// was told. Disagreement is a determinate corruption. An intent with no matching map means
/// the crash landed between step 7 (wire) and step 10 (local UoW); we re-drive the
/// orchestrator, which detects the completed intent and skips the create rather than
/// duplicating it. Absent both records, nothing local can prove the wire was touched and
/// the outcome stays Indeterminate.
/// </para>
/// <para>
/// <b>Verified requires more than the item's existence.</b> An unpromoted parent or
/// non-hierarchy relation would be a silent broken-graph if we ratified on the fetched id
/// alone. Verification walks every seed link the local record still names, compares each
/// against the remote item's parent or non-hierarchy edges, and downgrades to Indeterminate
/// on the first missing intended edge — cache-only warnings (a stale local mirror) do not
/// block Verified because the cache is not authoritative.
/// </para>
/// </remarks>
internal sealed class PlanSeedPublisher
{
    private readonly IAdoWorkItemService _ado;
    private readonly IWorkItemRepository _workItems;
    private readonly ISeedLinkRepository _seedLinks;
    private readonly IStagedIdentityRegistry _stagedRegistry;
    private readonly IPublishIdMapRepository _publishIdMap;
    private readonly IPublishIntentRepository _publishIntent;
    private readonly SeedPublishInvoker _invokePublish;

    internal PlanSeedPublisher(
        IAdoWorkItemService ado,
        IWorkItemRepository workItems,
        ISeedLinkRepository seedLinks,
        IStagedIdentityRegistry stagedRegistry,
        IPublishIdMapRepository publishIdMap,
        IPublishIntentRepository publishIntent,
        SeedPublishInvoker invokePublish)
    {
        _ado = ado;
        _workItems = workItems;
        _seedLinks = seedLinks;
        _stagedRegistry = stagedRegistry;
        _publishIdMap = publishIdMap;
        _publishIntent = publishIntent;
        _invokePublish = invokePublish;
    }

    /// <summary>
    /// Executes the operation from a fresh Confirmed row. Resolves the staged seed, verifies
    /// its cached identity and fingerprint against the plan BEFORE looking at the id map;
    /// only then does a pre-existing map row become attributable to this plan.
    /// </summary>
    public async Task<PlanExecutionResult> ExecuteAsync(PublishSeedOperation op, CancellationToken ct)
    {
        // 1. Seed identity + fingerprint FIRST. Failing here is a plan-shape refusal — the
        //    plan named a seed the current cache no longer describes, and no id map row can
        //    reattach the plan to a drifted seed.
        var alias = await _stagedRegistry.FindAliasAsync(op.StagedIdentity, ct).ConfigureAwait(false);
        WorkItem? seed = null;
        string? actualFingerprint = null;
        if (alias is { } aliasValue)
        {
            seed = await _workItems.GetByIdAsync(aliasValue.Value, ct).ConfigureAwait(false);
            if (seed is not null && seed.IsSeed)
            {
                // Alias-to-identity stability guard: a cache rebuild can reissue an alias to a
                // different staged identity. Refuse before fingerprinting so a coincidental hash
                // collision on the wrong seed cannot slip through.
                if (seed.StagedIdentity is null || !seed.StagedIdentity.Value.Equals(op.StagedIdentity))
                    return PlanExecutionResult.Failure(
                        $"Cached seed staged identity {seed.StagedIdentity?.ToString() ?? "<none>"} does not match planned identity {op.StagedIdentity}.");

                var links = await _seedLinks.GetLinksForItemAsync(aliasValue.Value, ct).ConfigureAwait(false);
                actualFingerprint = await SeedFingerprintCalculator
                    .ComputeAsync(seed, links, _stagedRegistry, _publishIdMap, ct)
                    .ConfigureAwait(false);
                if (!string.Equals(actualFingerprint, op.ExpectedFingerprint, StringComparison.Ordinal))
                    return PlanExecutionResult.Failure(
                        $"Seed fingerprint drift: expected={op.ExpectedFingerprint} actual={actualFingerprint}.");
            }
        }

        // 2. Now consult the map and intent. Disagreement between the two ledgers is a
        //    determinate corruption regardless of what the seed looks like.
        var mappedId = await _publishIdMap.GetNewIdAsync(op.StagedIdentity, ct).ConfigureAwait(false);
        var priorIntent = await _publishIntent.GetIntentAsync(op.StagedIdentity, ct).ConfigureAwait(false);

        if (mappedId.HasValue && priorIntent is { PublishedId: { } intentId } && intentId != mappedId.Value)
            return PlanExecutionResult.Failure(
                $"Publish intent/id map disagree for seed {op.StagedIdentity}: intent={intentId} map={mappedId.Value}.");

        if (mappedId.HasValue)
        {
            // A resolvable seed with a matching fingerprint is positive proof this map row
            // describes the plan's own view. Absence of a seed row is not by itself
            // evidence of drift — a prior run of THIS plan (or an idempotent re-run) will
            // have deleted the seed row in step 10f of the publish orchestrator, and the
            // map is exactly the record that survives. Drift is only fail-closed when we
            // could compute a fingerprint and it did not match, which the branch above
            // already returned on.
            return PlanExecutionResult.MappedPublish(mappedId.Value);
        }

        // 3. No map: the seed must be present locally for the orchestrator to publish it.
        if (alias is null)
            return PlanExecutionResult.Failure($"Staged identity {op.StagedIdentity} is not registered.");
        if (seed is null || !seed.IsSeed)
            return PlanExecutionResult.Failure($"Seed for identity {op.StagedIdentity} not found in the cache.");

        // 4. Delegate to the shared publish orchestrator. It is the ONE seam that touches
        //    ADO plus the local transaction; a second inline copy of that logic would drift.
        var publishResult = await _invokePublish(seed.Id, ct).ConfigureAwait(false);
        return ClassifyPublishResult(publishResult, op.StagedIdentity);
    }

    /// <summary>
    /// Reads back a publish-seed operation. Runs at recovery of an Applying row (with a
    /// default <paramref name="applyResult"/>) and at the happy-path Applied→Verified step
    /// (with the executor's classification). Both paths verify the remote item AND every
    /// intended promoted relation before returning Verified.
    /// </summary>
    public async Task<PlanReadbackOutcome> ReadbackAsync(
        PublishSeedOperation op,
        PlanExecutionResult applyResult,
        CancellationToken ct)
    {
        var mapped = await _publishIdMap.GetNewIdAsync(op.StagedIdentity, ct).ConfigureAwait(false);
        var intent = await _publishIntent.GetIntentAsync(op.StagedIdentity, ct).ConfigureAwait(false);
        var intentId = intent?.PublishedId;

        if (mapped.HasValue && intentId.HasValue && mapped.Value != intentId.Value)
            return PlanReadbackOutcome.Failed(
                $"Publish intent/id map disagree for seed {op.StagedIdentity}: intent={intentId.Value} map={mapped.Value}.");

        if (mapped.HasValue)
            return await VerifyRemoteAsync(op.StagedIdentity, mapped.Value, ct).ConfigureAwait(false);

        if (intent is not null)
            return await RecoverIntentOnlyAsync(op, intent, ct).ConfigureAwait(false);

        // Neither ledger records evidence: apply may have carried a mapped id forward from a
        // MappedPublish classification. Otherwise there is nothing to read back against.
        var applyId = applyResult.MappedPublishId ?? 0;
        if (applyId <= 0)
            return PlanReadbackOutcome.Indeterminate(
                $"No publish evidence for seed {op.StagedIdentity}: neither an id map row nor an intent record exists.");
        return await VerifyRemoteAsync(op.StagedIdentity, applyId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applying recovery when only the intent record survives. The wire may or may not have
    /// landed the create; the orchestrator's step-7 idempotency (existing intent detects the
    /// prior create instead of reissuing it) plus step-10 map recording is the single seam
    /// that can close the window. Re-drive it here, then verify the remote once the map
    /// row is present.
    /// </summary>
    private async Task<PlanReadbackOutcome> RecoverIntentOnlyAsync(
        PublishSeedOperation op,
        PublishIntent intent,
        CancellationToken ct)
    {
        var alias = await _stagedRegistry.FindAliasAsync(op.StagedIdentity, ct).ConfigureAwait(false);
        if (alias is not { } aliasValue)
            return PlanReadbackOutcome.Indeterminate(
                $"Publish intent for {op.StagedIdentity} names id {intent.PublishedId?.ToString() ?? "<none>"}, but the staged identity is no longer registered locally.");

        var seed = await _workItems.GetByIdAsync(aliasValue.Value, ct).ConfigureAwait(false);
        if (seed is null || !seed.IsSeed)
            return PlanReadbackOutcome.Indeterminate(
                $"Publish intent for {op.StagedIdentity} names id {intent.PublishedId?.ToString() ?? "<none>"}, but the seed row is no longer present locally to reconcile.");

        SeedPublishResult publishResult;
        try
        {
            publishResult = await _invokePublish(seed.Id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PlanReadbackOutcome.Indeterminate(
                $"Publish intent for {op.StagedIdentity} could not be reconciled: {ex.Message}");
        }

        if (publishResult.Status is SeedPublishStatus.Error or SeedPublishStatus.ValidationFailed)
            return PlanReadbackOutcome.Indeterminate(
                $"Publish intent for {op.StagedIdentity} named id {intent.PublishedId?.ToString() ?? "<none>"}; recovery reported {publishResult.Status}: {publishResult.ErrorMessage ?? "no detail"}.");

        var reconciledMap = await _publishIdMap.GetNewIdAsync(op.StagedIdentity, ct).ConfigureAwait(false);
        if (!reconciledMap.HasValue)
            return PlanReadbackOutcome.Indeterminate(
                $"Publish intent for {op.StagedIdentity} ran to completion but no id map row was recorded — the local unit of work did not commit.");

        return await VerifyRemoteAsync(op.StagedIdentity, reconciledMap.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches the remote item together with its non-hierarchy edges, then verifies every
    /// promoted relation the local seed record still names — parent (via
    /// <see cref="WorkItem.ParentId"/>) and every non-hierarchy edge (via
    /// <see cref="WorkItemLink"/>). A missing intended edge downgrades the outcome; a
    /// cache-only mirror gap is tolerated because the cache is not authoritative.
    /// </summary>
    private async Task<PlanReadbackOutcome> VerifyRemoteAsync(
        StagedIdentity identity,
        int newId,
        CancellationToken ct)
    {
        WorkItem remoteItem;
        IReadOnlyList<WorkItemLink> remoteLinks;
        try
        {
            (remoteItem, remoteLinks) = await _ado
                .FetchWithLinksAsync(newId, ct)
                .ConfigureAwait(false);
        }
        catch (AdoNotFoundException)
        {
            return PlanReadbackOutcome.Indeterminate($"Mapped seed id {newId} is not present in ADO.");
        }

        // A remote item that did not come back as a real work item id is the same
        // 404-shaped outcome — treat it as Indeterminate rather than Verified.
        if (remoteItem.Id != newId)
            return PlanReadbackOutcome.Indeterminate(
                $"ADO returned id {remoteItem.Id} for a fetch of seed {identity} → {newId}; the ids do not match.");

        var seedLinks = await _seedLinks.GetLinksForItemAsync(newId, ct).ConfigureAwait(false)
            ?? Array.Empty<SeedLink>();

        foreach (var link in seedLinks)
        {
            // The promoter places outgoing edges only. A link that names this item as target
            // is another seed's outgoing edge and is that other seed's readback to verify.
            if (link.SourceId != newId)
                continue;

            if (string.Equals(link.LinkType, SeedLinkTypes.ParentChild, StringComparison.Ordinal))
            {
                // The parent link is set at CREATE time (System.LinkTypes.Hierarchy-Reverse),
                // not by the promoter, so the remote proof is the fetched item's ParentId —
                // NOT a non-hierarchy edge in the relations list.
                if (remoteItem.ParentId != link.TargetId)
                    return PlanReadbackOutcome.Indeterminate(
                        $"Seed {identity} published as #{newId} but the remote parent link to #{link.TargetId} is missing.");
                continue;
            }

            // Non-hierarchy: mirrors the promoter's own skip rules so a missing edge means
            // "promoter would have placed it and ADO does not reflect it", not "promoter
            // skipped it for a reason the readback can also observe".
            if (link.SourceId <= 0 || link.TargetId <= 0)
                continue;
            if (!SeedLinkTypeMapper.TryToAdoRelationType(link.LinkType, out var adoRelation))
                continue;

            if (!RemoteHasEdge(remoteLinks, newId, link.TargetId, adoRelation))
                return PlanReadbackOutcome.Indeterminate(
                    $"Seed {identity} published as #{newId} but the remote {link.LinkType} link to #{link.TargetId} is missing.");
        }

        return PlanReadbackOutcome.VerifiedWith(
            PlanOperationExecutor.SerializeReadbackPublishedSeed(identity, newId));
    }

    /// <summary>
    /// True when <paramref name="remoteLinks"/> contains an edge from <paramref name="sourceId"/>
    /// to <paramref name="targetId"/> at the specified relation. The response mapper normalises
    /// non-hierarchy relations to friendly short names ("Related", "Successor"); some paths
    /// still surface the raw reference name. Accept either form so a happy-path publish is
    /// not misreported as an incomplete graph.
    /// </summary>
    private static bool RemoteHasEdge(
        IReadOnlyList<WorkItemLink> remoteLinks,
        int sourceId,
        int targetId,
        string adoRelation)
    {
        foreach (var edge in remoteLinks)
        {
            if (edge.SourceId != sourceId || edge.TargetId != targetId)
                continue;
            if (string.Equals(edge.LinkType, adoRelation, StringComparison.OrdinalIgnoreCase))
                return true;
            if (LinkTypeMapper.TryToFriendlyName(adoRelation, out var friendly)
                && string.Equals(edge.LinkType, friendly, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Maps a <see cref="SeedPublishResult"/> onto the corresponding
    /// <see cref="PlanExecutionResult"/>. Delegates to
    /// <see cref="PlanOperationExecutor.ClassifySeedPublishSuccess"/> so the two paths cannot
    /// drift on link-warning classification.
    /// </summary>
    internal static PlanExecutionResult ClassifyPublishResult(SeedPublishResult result, StagedIdentity identity)
    {
        return result.Status switch
        {
            SeedPublishStatus.Created or SeedPublishStatus.Skipped when result.NewId > 0
                => PlanOperationExecutor.ClassifySeedPublishSuccess(result, identity),
            SeedPublishStatus.Error => PlanExecutionResult.Failure(result.ErrorMessage ?? "Seed publish failed."),
            SeedPublishStatus.ValidationFailed => PlanExecutionResult.Failure(
                "Seed publish rejected by validation."),
            _ => PlanExecutionResult.Indeterminate(result.ErrorMessage ?? "Seed publish outcome unknown."),
        };
    }
}
