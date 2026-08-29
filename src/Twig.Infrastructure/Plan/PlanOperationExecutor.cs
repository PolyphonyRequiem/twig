using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// Applies one plan operation against ADO and returns a shape the lifecycle service maps
/// straight onto a journal transition.
/// <para>
/// The executor never touches the journal itself — that is the lifecycle's job — but it
/// does own the classification: what came back a determinate success, a
/// deterministic failure (412, 404 where the plan said the item existed, drifted seed,
/// bad relation), or an uncertain outcome that only a readback can settle. That keeps the
/// wire semantics in one place, so the lifecycle stays free to focus on state.
/// </para>
/// </summary>
/// <remarks>
/// Non-goals: retry, refetch-then-rebase, or any second write. The strict revision-bound
/// ADO surface is the whole point of the plan pipeline; a helper that eats a 412 would
/// invert the guarantee the plan digest carries.
/// </remarks>
internal sealed class PlanOperationExecutor
{
    private readonly IAdoWorkItemService _adoService;
    private readonly IRevisionBoundAdoWorkItemService _revisionBound;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly PlanSeedPublisher _seedPublisher;

    /// <summary>
    /// Production constructor: builds the seed publisher inline over the shared
    /// collaborators so callers keep passing the domain interfaces they already own.
    /// </summary>
    internal PlanOperationExecutor(
        IAdoWorkItemService adoService,
        IRevisionBoundAdoWorkItemService revisionBound,
        IFieldDefinitionStore fieldDefinitionStore,
        SeedPublishOrchestrator seedPublish,
        IWorkItemRepository workItemRepo,
        ISeedLinkRepository seedLinkRepo,
        IStagedIdentityRegistry stagedRegistry,
        IPublishIdMapRepository publishIdMap,
        IPublishIntentRepository publishIntent)
        : this(
            adoService,
            revisionBound,
            fieldDefinitionStore,
            new PlanSeedPublisher(
                adoService,
                workItemRepo,
                seedLinkRepo,
                stagedRegistry,
                publishIdMap,
                publishIntent,
                (seedId, ct) => seedPublish.PublishAsync(seedId, force: false, dryRun: false, ct)))
    {
    }

    /// <summary>
    /// Test seam: accepts a preconstructed <see cref="PlanSeedPublisher"/> so the
    /// orchestrator's PublishAsync can be stubbed without instantiating its full dependency
    /// graph. Callers outside the test project should prefer the production constructor.
    /// </summary>
    internal PlanOperationExecutor(
        IAdoWorkItemService adoService,
        IRevisionBoundAdoWorkItemService revisionBound,
        IFieldDefinitionStore fieldDefinitionStore,
        PlanSeedPublisher seedPublisher)
    {
        _adoService = adoService;
        _revisionBound = revisionBound;
        _fieldDefinitionStore = fieldDefinitionStore;
        _seedPublisher = seedPublisher;
    }

    /// <summary>
    /// Issues the operation. The returned <see cref="PlanExecutionResult"/> carries the
    /// classification only — the caller writes it into the journal.
    /// </summary>
    public async Task<PlanExecutionResult> ExecuteAsync(
        PlanOperationDefinition operation,
        CancellationToken ct)
    {
        try
        {
            return operation switch
            {
                BatchOperation batch => await ExecuteBatchAsync(batch, ct).ConfigureAwait(false),
                AddLinkOperation add => await ExecuteAddLinkAsync(add, ct).ConfigureAwait(false),
                RemoveLinkOperation remove => await ExecuteRemoveLinkAsync(remove, ct).ConfigureAwait(false),
                DeleteOperation delete => await ExecuteDeleteAsync(delete, ct).ConfigureAwait(false),
                PublishSeedOperation seed => await ExecutePublishSeedAsync(seed, ct).ConfigureAwait(false),
                _ => PlanExecutionResult.Failure($"Unsupported operation kind: {operation.Kind}"),
            };
        }
        catch (AdoConflictException ex)
        {
            // 412 is DETERMINATE: the server refused because the revision moved. No readback
            // will change that answer, and no retry is permitted — the whole point of the plan
            // shape is that a stale revision fails loudly.
            return PlanExecutionResult.Failure($"Revision conflict: server rev={ex.ServerRevision}.");
        }
        catch (AdoRelationNotFoundException ex)
        {
            // Strict-CAS relation lookup at the expected revision already proved the edge is
            // absent from the item's relations. That is deterministic: readback cannot
            // resurrect a link the server said did not exist. Unparent-of-nothing and
            // remove-of-a-non-existent relation both funnel through here, so a plan that
            // asked for an impossible edit fails loudly instead of drifting to Indeterminate.
            return PlanExecutionResult.Failure(ex.Message);
        }
        catch (AdoBadRequestException ex) when (operation is not PublishSeedOperation)
        {
            // ADO rejected this operation's sole request before applying it. Publish-seed
            // is multi-step and remains indeterminate so intent/map readback can reconcile
            // a remote item created before a later bad request.
            return PlanExecutionResult.Failure(ex.Message);
        }

        catch (AdoNotFoundException ex)
        {
            // For an operation whose plan named the item, 404 is a plan-level determinate
            // failure. The exception here reflects that ADO does not have the item at all.
            return PlanExecutionResult.Failure(ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any other failure is uncertain: it may have landed on the wire, and only a
            // readback can settle whether it did.
            return PlanExecutionResult.Indeterminate(ex.Message);
        }

    }

    private async Task<PlanExecutionResult> ExecuteBatchAsync(BatchOperation batch, CancellationToken ct)
    {
        var changes = new List<FieldChange>(batch.Fields.Count);
        foreach (var kv in batch.Fields)
            changes.Add(new FieldChange(kv.Key, OldValue: null, NewValue: kv.Value));

        var newRev = await _adoService.PatchAsync(batch.WorkItemId, changes, batch.ExpectedRevision, ct)
            .ConfigureAwait(false);
        return PlanExecutionResult.Success(SerializeRevision(newRev), newRev);
    }

    private async Task<PlanExecutionResult> ExecuteAddLinkAsync(AddLinkOperation add, CancellationToken ct)
    {
        if (!LinkTypeMapper.TryResolve(add.Relation, out var adoRelation))
            return PlanExecutionResult.Failure($"Unsupported relation '{add.Relation}'.");

        var newRev = await _revisionBound.AddLinkAtRevisionAsync(
            add.WorkItemId, adoRelation, add.OtherId, add.ExpectedRevision, ct).ConfigureAwait(false);
        return PlanExecutionResult.Success(SerializeRevision(newRev), newRev);
    }

    private async Task<PlanExecutionResult> ExecuteRemoveLinkAsync(RemoveLinkOperation remove, CancellationToken ct)
    {
        if (!LinkTypeMapper.TryResolve(remove.Relation, out var adoRelation))
            return PlanExecutionResult.Failure($"Unsupported relation '{remove.Relation}'.");

        var newRev = await _revisionBound.RemoveLinkAtRevisionAsync(
            remove.WorkItemId, adoRelation, remove.OtherId, remove.ExpectedRevision, ct).ConfigureAwait(false);
        return PlanExecutionResult.Success(SerializeRevision(newRev), newRev);
    }

    private async Task<PlanExecutionResult> ExecuteDeleteAsync(DeleteOperation delete, CancellationToken ct)
    {
        await _revisionBound.DeleteAtRevisionAsync(delete.WorkItemId, delete.ExpectedRevision, ct)
            .ConfigureAwait(false);
        return PlanExecutionResult.Success(SerializeDeleted(delete.WorkItemId));
    }

    private Task<PlanExecutionResult> ExecutePublishSeedAsync(PublishSeedOperation op, CancellationToken ct)
        => _seedPublisher.ExecuteAsync(op, ct);

    /// <summary>
    /// Distinguishes the two categories of <see cref="SeedPublishResult.LinkWarnings"/>:
    /// <list type="bullet">
    ///   <item>Cache-only refresh notes are cosmetic — the remote work item and its edges
    ///     already reflect the intent, only the local cache needs a follow-up sync. These
    ///     never block promotion to Verified.</item>
    ///   <item>Anything else is a link-promotion failure — an intended remote edge did not
    ///     land. Marking such a publish Applied would let the lifecycle Verified transition
    ///     succeed on the mere existence of the new work item id while a promised link is
    ///     silently missing. We surface Indeterminate so the readback (or a follow-up plan)
    ///     drives the reconciliation.</item>
    /// </list>
    /// </summary>
    internal static PlanExecutionResult ClassifySeedPublishSuccess(SeedPublishResult result, StagedIdentity identity)
    {
        foreach (var warning in result.LinkWarnings)
        {
            if (!IsCacheOnlyWarning(warning))
                return PlanExecutionResult.Indeterminate(
                    $"Seed publish for {identity} landed as #{result.NewId} but a remote link warning is present: {warning}");
        }
        return PlanExecutionResult.Success(SerializePublishedSeed(result.NewId, identity));
    }

    internal static bool IsCacheOnlyWarning(string warning)
        => warning.Contains("relationship cache refresh failed", StringComparison.Ordinal);

    /// <summary>
    /// Post-apply readback for a single operation. Called AFTER the journal has been
    /// advanced to <see cref="PlanOperationState.Applied"/>. Returns Verified when ADO
    /// reflects the intended state, Failed on a determinate contradiction, Indeterminate
    /// otherwise.
    /// </summary>
    public async Task<PlanReadbackOutcome> ReadbackAsync(
        PlanOperationDefinition operation,
        PlanExecutionResult applyResult,
        CancellationToken ct)
    {
        try
        {
            return operation switch
            {
                BatchOperation batch => await ReadbackBatchAsync(batch, ct).ConfigureAwait(false),
                AddLinkOperation add => await ReadbackAddLinkAsync(add, ct).ConfigureAwait(false),
                RemoveLinkOperation remove => await ReadbackRemoveLinkAsync(remove, ct).ConfigureAwait(false),
                DeleteOperation delete => await ReadbackDeleteAsync(delete, ct).ConfigureAwait(false),
                PublishSeedOperation seed => await ReadbackPublishSeedAsync(seed, applyResult, ct).ConfigureAwait(false),
                _ => PlanReadbackOutcome.Indeterminate($"Unsupported kind {operation.Kind} in readback."),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AdoNotFoundException) when (operation is DeleteOperation)
        {
            return PlanReadbackOutcome.VerifiedWith(SerializeReadbackDeleted);
        }
        catch (Exception ex)
        {
            return PlanReadbackOutcome.Indeterminate($"Readback failed: {ex.Message}");
        }
    }

    private async Task<PlanReadbackOutcome> ReadbackBatchAsync(BatchOperation batch, CancellationToken ct)
    {
        var item = await _adoService.FetchAsync(batch.WorkItemId, ct).ConfigureAwait(false);
        // Missing / stale / non-advanced readback stays Indeterminate and NEVER reaches the
        // server-generated warning policy — an unproven mutation cannot be warning-verified.
        if (item.Revision <= batch.ExpectedRevision)
            return PlanReadbackOutcome.Indeterminate("Server revision did not advance past the expected revision.");

        var normalizations = new List<PlanReadbackNormalization>();
        foreach (var kv in batch.Fields)
        {
            var actual = ResolveBatchField(item, kv.Key);
            if (kv.Value is null)
            {
                // Plan asked to clear the field; a genuine clear means absent OR empty in
                // both the canonical property (if any) and the arbitrary Fields dictionary.
                //
                // 🔴 A requested CLEAR is never warning-verified, not even on a
                // server-generated field. Spec #753 downgrades a difference only when the
                // refreshed read PROVES the intended mutation landed — and a clear that did
                // not take is precisely an unproven mutation, not ADO bookkeeping. Treating
                // it as normalization would report "cleared" for a field that still holds a
                // value, which is the false-green class this whole spec exists to abolish.
                if (string.IsNullOrEmpty(actual))
                    continue;
                return PlanReadbackOutcome.Indeterminate(
                    $"Field {kv.Key} was expected to be cleared but reflects '{actual}'.");
            }
            var match = await ClassifyReadbackFieldAsync(actual, kv.Key, kv.Value, ct).ConfigureAwait(false);
            if (match == FieldMatch.Exact)
                continue;
            if (match == FieldMatch.NormalizedHtml)
            {
                // AB#755: the field's own metadata says ADO owns this markup's serialization,
                // and the structural comparison proved the CONTENT landed. That is the same
                // class of fact as a server-generated stamp — a landed write plus a rewrite
                // Twig does not control — so it takes the identical warning-verified path
                // rather than a parallel one. Materially different HTML never reaches here:
                // HtmlStructuralComparer returns false and the ordinary strict branch below
                // fires.
                normalizations.Add(new PlanReadbackNormalization(
                    kv.Key, kv.Value, actual, NormalizationKind.CanonicalizedHtml));
                continue;
            }
            if (match == FieldMatch.NormalizedIdentity)
            {
                // AB#802: the field's own metadata says this is an identity, and the stable
                // key comparison proved the SAME account landed — ADO merely re-rendered it
                // from the staged form into `Display Name (unique name)`. Same class of fact
                // as the html case: a landed write plus a rewrite Twig does not control, so
                // it takes the identical warning-verified path. A genuinely different
                // identity never reaches here: IdentityValueComparer returns false and the
                // strict branch below fires.
                normalizations.Add(new PlanReadbackNormalization(
                    kv.Key, kv.Value, actual, NormalizationKind.CanonicalizedIdentity));
                continue;
            }

            // AB#754: a difference on a field ADO's own revision machinery owns is a
            // normalization, not a contradiction — but ONLY as warning detail riding
            // alongside a Verified outcome, and only once every user-authored field in this
            // same batch has already compared equal (a genuine scalar mismatch below returns
            // Indeterminate before we ever finish the loop).
            if (await IsServerGeneratedFieldAsync(kv.Key, ct).ConfigureAwait(false))
            {
                normalizations.Add(new PlanReadbackNormalization(
                    kv.Key, kv.Value, actual, NormalizationKind.ServerGenerated));
                continue;
            }

            return PlanReadbackOutcome.Indeterminate($"Field {kv.Key} did not reflect the expected value.");
        }

        var resultJson = SerializeReadbackRevision(item.Revision);
        if (normalizations.Count == 0)
            return PlanReadbackOutcome.VerifiedWith(resultJson);

        // Terminal-outcome coupling. System.State and Custom.TerminalOutcome are NOT in the
        // server-generated set, so a batch whose lifecycle transition did not land already
        // returned Indeterminate inside the loop above — that is where the strictness lives,
        // and a second runtime re-check here would be unreachable code masquerading as a
        // guard. What actually protects the coupling is that the generated set can never
        // acquire a lifecycle field; ServerGeneratedFieldPolicy asserts exactly that as a
        // static invariant (see TerminalContractFieldsAreNeverServerGenerated), so a future
        // addition breaks a test rather than silently downgrading a close.
        if (!ServerGeneratedFieldPolicy.OnlyExplainedDifferencesRemain(batch, normalizations))
            return PlanReadbackOutcome.Indeterminate(
                "Readback observed differences the normalization policy cannot explain.");

        return PlanReadbackOutcome.VerifiedWithWarning(
            resultJson, ServerGeneratedFieldPolicy.FormatWarning(normalizations));
    }

    /// <summary>
    /// Field-aware server-ownership evidence. The reference name must be in the justified
    /// server-generated set AND the process must actually declare the field (the same
    /// <see cref="IFieldDefinitionStore"/> the html path consults), so a plan naming a field
    /// this workspace does not have cannot be warning-verified into a false success.
    /// </summary>
    private async Task<bool> IsServerGeneratedFieldAsync(string referenceName, CancellationToken ct)
    {
        if (!ServerGeneratedFieldPolicy.IsServerGenerated(referenceName))
            return false;
        var definition = await _fieldDefinitionStore
            .GetByReferenceNameAsync(referenceName, ct)
            .ConfigureAwait(false);
        return definition is not null;
    }

    /// <summary>
    /// How a readback field comparison was satisfied. AB#755: an exact match and a match
    /// that only held after ADO's HTML canonicalization are both successes, but they are
    /// not the same fact — the latter is normalization the ledger must record as warning
    /// detail. Returning the distinction here keeps ONE comparator: the caller decides
    /// what to do with a normalized match, and no second comparison is performed anywhere.
    /// AB#802 adds the identity rendering as a third member of the same family.
    /// </summary>
    private enum FieldMatch
    {
        /// <summary>The refreshed value did not reflect the plan's intent at all.</summary>
        None,
        /// <summary>Byte-for-byte equal — the ordinary, warning-free path.</summary>
        Exact,
        /// <summary>Equal only after canonicalizing ADO-normalized HTML.</summary>
        NormalizedHtml,
        /// <summary>Equal only after reducing both renderings to a stable identity key.</summary>
        NormalizedIdentity,
    }

    private async Task<FieldMatch> ClassifyReadbackFieldAsync(
        string? actual,
        string referenceName,
        string expected,
        CancellationToken ct)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
            return FieldMatch.Exact;

        var fieldDefinition = await _fieldDefinitionStore
            .GetByReferenceNameAsync(referenceName, ct)
            .ConfigureAwait(false);
        if (fieldDefinition is null || actual is null)
            return FieldMatch.None;

        // Semantic comparison is opt-in by FIELD METADATA, never by value shape: only a
        // field ADO declares as html is compared structurally, and only a field ADO declares
        // with isIdentity is compared as an identity. An ordinary scalar that merely looks
        // like markup — or like an email address — stays on the ordinal path above
        // (AB#755, AB#802).
        if (string.Equals(fieldDefinition.DataType, "html", StringComparison.OrdinalIgnoreCase))
        {
            return HtmlStructuralComparer.AreEquivalent(expected, actual)
                ? FieldMatch.NormalizedHtml
                : FieldMatch.None;
        }

        // 🔴 Checked AFTER html and independently of DataType: ADO reports identity fields
        // as `string`, so the data type cannot carry this and the flag is the only witness.
        if (fieldDefinition.IsIdentity)
        {
            return IdentityValueComparer.AreEquivalent(expected, actual)
                ? FieldMatch.NormalizedIdentity
                : FieldMatch.None;
        }

        return FieldMatch.None;
    }

    /// <summary>
    /// Resolves a batch field for readback comparison. Canonical core fields
    /// (Title/State/AssignedTo/AreaPath/IterationPath) live on the <see cref="WorkItem"/>
    /// aggregate first — the ADO response mapper is authoritative for those five and the
    /// Fields dictionary is only a mirror. We consult the property first and fall through
    /// to <see cref="WorkItem.Fields"/> when the property is empty/default so a mapper that
    /// only populates one side still produces a comparable value.
    /// </summary>
    private static string? ResolveBatchField(WorkItem item, string field)
    {
        string? fromProperty = null;
        var isCanonical = true;
        if (string.Equals(field, "System.Title", StringComparison.OrdinalIgnoreCase))
            fromProperty = item.Title;
        else if (string.Equals(field, "System.State", StringComparison.OrdinalIgnoreCase))
            fromProperty = item.State;
        else if (string.Equals(field, "System.AssignedTo", StringComparison.OrdinalIgnoreCase))
            fromProperty = item.AssignedTo;
        else if (string.Equals(field, "System.AreaPath", StringComparison.OrdinalIgnoreCase))
            fromProperty = item.AreaPath.Value;
        else if (string.Equals(field, "System.IterationPath", StringComparison.OrdinalIgnoreCase))
            fromProperty = item.IterationPath.Value;
        else
            isCanonical = false;

        if (isCanonical && !string.IsNullOrEmpty(fromProperty))
            return fromProperty;
        return item.Fields.TryGetValue(field, out var v) ? v : null;
    }

    private async Task<PlanReadbackOutcome> ReadbackAddLinkAsync(AddLinkOperation add, CancellationToken ct)
    {
        if (string.Equals(add.Relation, "parent", StringComparison.OrdinalIgnoreCase))
        {
            var item = await _adoService.FetchAsync(add.WorkItemId, ct).ConfigureAwait(false);
            return item.ParentId == add.OtherId
                ? PlanReadbackOutcome.VerifiedWith(SerializeReadbackRevision(item.Revision))
                : PlanReadbackOutcome.Indeterminate("Parent link not reflected on the source item.");
        }

        if (!LinkTypeMapper.TryResolve(add.Relation, out _))
            return PlanReadbackOutcome.Indeterminate($"Unknown relation '{add.Relation}' in readback.");

        var (source, links) = await _adoService.FetchWithLinksAsync(add.WorkItemId, ct).ConfigureAwait(false);
        foreach (var link in links)
        {
            if (link.TargetId == add.OtherId && RelationMatches(add.Relation, link.LinkType))
                return PlanReadbackOutcome.VerifiedWith(SerializeReadbackRevision(source.Revision));
        }
        return PlanReadbackOutcome.Indeterminate("Relation not present after apply.");
    }

    private async Task<PlanReadbackOutcome> ReadbackRemoveLinkAsync(RemoveLinkOperation remove, CancellationToken ct)
    {
        if (string.Equals(remove.Relation, "parent", StringComparison.OrdinalIgnoreCase))
        {
            var item = await _adoService.FetchAsync(remove.WorkItemId, ct).ConfigureAwait(false);
            return item.ParentId is null || item.ParentId != remove.OtherId
                ? PlanReadbackOutcome.VerifiedWith(SerializeReadbackRevision(item.Revision))
                : PlanReadbackOutcome.Indeterminate("Parent link still present after remove.");
        }

        if (!LinkTypeMapper.TryResolve(remove.Relation, out _))
            return PlanReadbackOutcome.Indeterminate($"Unknown relation '{remove.Relation}' in readback.");

        var (source, links) = await _adoService.FetchWithLinksAsync(remove.WorkItemId, ct).ConfigureAwait(false);
        foreach (var link in links)
        {
            if (link.TargetId == remove.OtherId && RelationMatches(remove.Relation, link.LinkType))
                return PlanReadbackOutcome.Indeterminate("Relation still present after remove.");
        }
        return PlanReadbackOutcome.VerifiedWith(SerializeReadbackRevision(source.Revision));
    }

    /// <summary>
    /// True when the plan's friendly relation name identifies the same edge as the readback
    /// link type. The ADO response mapper normalises non-hierarchy relations to friendly
    /// short names ("Related", "Successor", "Predecessor"), but tests, mocked fixtures, and
    /// batch-fetch paths may still surface the raw ADO relation reference name. We accept
    /// either form and only report the edge absent when neither normalised comparison
    /// matches.
    /// </summary>
    private static bool RelationMatches(string planRelation, string linkType)
    {
        if (string.Equals(planRelation, linkType, StringComparison.OrdinalIgnoreCase))
            return true;
        if (LinkTypeMapper.TryToFriendlyName(linkType, out var friendly)
            && string.Equals(planRelation, friendly, StringComparison.OrdinalIgnoreCase))
            return true;
        if (LinkTypeMapper.TryResolve(planRelation, out var adoRelation)
            && string.Equals(adoRelation, linkType, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private async Task<PlanReadbackOutcome> ReadbackDeleteAsync(DeleteOperation delete, CancellationToken ct)
    {
        try
        {
            _ = await _adoService.FetchAsync(delete.WorkItemId, ct).ConfigureAwait(false);
            return PlanReadbackOutcome.Indeterminate("Item still present after delete.");
        }
        catch (AdoNotFoundException)
        {
            return PlanReadbackOutcome.VerifiedWith(SerializeReadbackDeleted);
        }
    }

    private Task<PlanReadbackOutcome> ReadbackPublishSeedAsync(
        PublishSeedOperation op,
        PlanExecutionResult applyResult,
        CancellationToken ct)
        => _seedPublisher.ReadbackAsync(op, applyResult, ct);

    private static string SerializeRevision(int newRevision)
        => $"{{\"rev\":{newRevision}}}";

    private static string SerializeDeleted(int workItemId)
        => $"{{\"deleted\":{workItemId}}}";

    private static string SerializePublishedSeed(int newId, StagedIdentity identity)
        => $"{{\"identity\":\"{identity}\",\"publishedId\":{newId}}}";

    internal static string SerializeMappedPublish(int newId)
        => $"{{\"publishedId\":{newId},\"source\":\"publish_id_map\"}}";

    // ── Canonical readback result shapes ────────────────────────────────────
    // Every readback that proves a recovered operation Verified must carry a canonical
    // ResultJson so the atomic Applied+result write during Applying-recovery lands a
    // non-null result_json — CLI/MCP status exposes ResultJson unchanged, and a NULL
    // there would misreport a successful recovery as a resultless row.

    internal static string SerializeReadbackRevision(int currentRevision)
        => $"{{\"revision\":{currentRevision}}}";

    internal const string SerializeReadbackDeleted = "{\"deleted\":true}";

    internal static string SerializeReadbackPublishedSeed(StagedIdentity identity, int publishedId)
        => $"{{\"identity\":\"{identity}\",\"publishedId\":{publishedId}}}";
}

/// <summary>
/// Result of a single execute pass. The lifecycle service maps this into the corresponding
/// journal transition.
/// </summary>
/// <remarks>
/// <see cref="NewRevision"/> carries the server revision produced by a determinate
/// revision-bumping success (batch, add-link, remove-link) so the lifecycle can project a
/// post-op authoritative snapshot into the same-item carry-forward map (AB#721) without
/// re-parsing <see cref="ResultJson"/>. It is <c>null</c> on failure, indeterminate
/// outcomes, delete, and MappedPublish paths — those never contribute to the carry.
/// </remarks>
internal readonly record struct PlanExecutionResult(
    PlanExecutionOutcome Outcome,
    string? ResultJson,
    string? Error,
    int? MappedPublishId,
    int? NewRevision)
{
    public static PlanExecutionResult Success(string resultJson, int? newRevision = null)
        => new(PlanExecutionOutcome.Applied, resultJson, null, null, newRevision);

    public static PlanExecutionResult Failure(string error)
        => new(PlanExecutionOutcome.Failed, null, error, null, null);

    public static PlanExecutionResult Indeterminate(string error)
        => new(PlanExecutionOutcome.Indeterminate, null, error, null, null);

    public static PlanExecutionResult MappedPublish(int newId)
        => new(
            PlanExecutionOutcome.MappedPublish,
            PlanOperationExecutor.SerializeMappedPublish(newId),
            null,
            newId,
            null);
}

internal enum PlanExecutionOutcome
{
    Applied,
    Failed,
    Indeterminate,
    /// <summary>Local map already carried a positive id — no ADO write attempted.</summary>
    MappedPublish,
}

/// <summary>
/// Classification a readback returns to the lifecycle. When Ok, <see cref="ResultJson"/>
/// carries the canonical shape the executor would have produced on the winning path
/// (batch/link → <c>{"revision":&lt;current&gt;}</c>, delete → <c>{"deleted":true}</c>,
/// publish-seed → <c>{"identity":&lt;planned&gt;,"publishedId":&lt;map&gt;}</c>). The
/// lifecycle threads that value into the atomic Applying → Applied record so a recovered
/// Verified row is never left with a NULL result_json — CLI/MCP status reads the raw
/// column and would otherwise misreport a proven-verified operation as resultless.
/// <para>
/// <see cref="Warning"/> (AB#754) is non-null ONLY on an <see cref="Ok"/> outcome and carries
/// the server-generated normalization detail. It is deliberately NOT a state: <c>Verified</c>
/// remains the sole landed-success state, and the warning rides alongside it into the journal
/// row so CLI/MCP can render it without a fourth terminal classification.
/// </para>
/// </summary>
internal readonly record struct PlanReadbackOutcome(
    bool Ok,
    bool Deterministic,
    string? Error,
    string? ResultJson,
    string? Warning)
{
    public static PlanReadbackOutcome VerifiedWith(string resultJson) =>
        new(true, true, null, resultJson, null);
    public static PlanReadbackOutcome VerifiedWithWarning(string resultJson, string warning) =>
        new(true, true, null, resultJson, warning);
    public static PlanReadbackOutcome Failed(string error) => new(false, true, error, null, null);
    public static PlanReadbackOutcome Indeterminate(string error) => new(false, false, error, null, null);
}
