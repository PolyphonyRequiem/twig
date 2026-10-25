using System.Globalization;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Twig.Infrastructure.Serialization;

namespace Twig.Browser;

/// <summary>Explicit native OPEN and atomic local-draft STAGE. No publication or pending-change sink.</summary>
internal sealed class RecoverableDraftEditingModule(
    BrowserOperationModule operations, BrowserEditingModule editing, SqliteCacheStore cache,
    IRecoverableWorkItemDraftStore drafts, IWorkItemRepository repository,
    IPendingChangeStore pending, IPlanOriginProvider origins,
    IWorkItemProvider provider, IProcessRuleProvider rules)
{
    internal async Task<DraftEditingResult> OpenAsync(int id, string bench,
        string? expectedBinding = null, string? expectedIdentity = null, CancellationToken ct = default)
    {
        if (id <= 0 || string.IsNullOrWhiteSpace(bench))
            return Refused("usage", "A positive native ADO item ID and explicit saved Bench are required.");
        var committed = false;
        var result = await operations.ExecuteExplicitAsync(bench, async (capture, token) =>
        {
            RequireAdo();
            var key = Key(capture, id);
            var evidenceStore = new SqliteDraftCaptureEvidenceStore(cache);
            var fingerprint = evidenceStore.ProcessFingerprint();
            var inspected = await editing.InspectAsync(id, rules, token);
            var opened = await editing.OpenAsync(id, capture, token);
            if (opened.Failure is { } failure)
                throw new InvalidOperationException("open-unavailable: " + failure.Message);
            var session = opened.Value ?? throw Error("open-unavailable", "No native form was returned.");
            if (session.HasLocalChanges)
                throw Error("pending-work", "Native pending work must be reconciled before opening this draft.");
            var layout = session.ServedLayout
                ?? throw Error("metadata-unavailable", "The native served layout is unavailable; display fallback cannot authorize editing.");
            if (inspected.TypeName != session.Snapshot.TypeName)
                throw Error("metadata-stale", "The native item type changed during inspection.");
            var scope = await provider.ResolveResourceScopeAsync(token)
                ?? throw Error("provider-unavailable", "The native ADO adapter did not resolve its resource scope.");
            var external = new ExternalWorkItemIdentity(provider.Capabilities.ProviderKind, scope, Decimal(id));
            var origin = await origins.GetOriginAsync(token);
            await operations.EnsureCurrentAsync(capture, token);
            if (!await editing.MatchesBaselineAsync(session, token))
                throw Error("baseline-changed", "The native item or pending work changed during inspection.");
            var savedBench = (await new SqliteBenchRepository(cache).GetAllAsync(token))
                .SingleOrDefault(candidate => Decimal(candidate.Id) == capture.BenchId);
            if (savedBench is null || !capture.Matches(savedBench))
                throw Error("bench-changed", "The explicit saved Bench changed.");
            var evidence = new DraftCaptureEvidence(1, key, origin, capture.StorageGeneration,
                inspected.TypeName, session.Revision, fingerprint,
                SqliteDraftCaptureEvidenceStore.SnapshotFingerprint(session.Snapshot), layout, inspected.Rules,
                savedBench, SqliteDraftCaptureEvidenceStore.RequiresNormalization(savedBench)
                    ? capture.Origin.Identity.AccountName : null) { ExternalIdentity = external };
            if (cache.ActiveTransaction is not null)
                throw Error("storage-busy", "Draft editing must own its native transaction.");
            await operations.EnsureCurrentAsync(capture, token);
            using var transaction = cache.GetConnection().BeginTransaction(deferred: false);
            cache.ActiveTransaction = transaction;
            try
            {
                if (!await editing.MatchesBaselineAsync(session, token))
                    throw Error("baseline-changed", "The native item changed before the draft observation was saved.");
                await evidenceStore.RecordAsync(evidence, session.Snapshot, token, joinTransaction: true);
                string? eligibilityIssue = null;
                try
                {
                    await evidenceStore.ValidateAsync(RecoverableWorkItemDraft.Create(key, session.Revision),
                        session.Snapshot, origin, SqliteDraftCaptureEvidenceStore.Fingerprint(evidence), token);
                }
                catch (InvalidOperationException ex)
                {
                    // Unavailable metadata is a visible read-only observation, never positive eligibility.
                    eligibilityIssue = ex.Message;
                }
                var prior = await drafts.GetAsync(key, token);
                var draft = prior is null ? RecoverableWorkItemDraft.Create(key, session.Revision) : Observe(prior, session.Snapshot);
                var changed = prior is null || draft.Generation != prior.Generation;
                if (changed && !await drafts.TrySaveAsync(draft, prior?.Generation, token))
                    throw Error("draft-cas-conflict", "Another surface changed this draft. No retry was attempted.");
                var document = Document(draft, session.Snapshot, session.Document, evidence, capture, eligibilityIssue);
                token.ThrowIfCancellationRequested();
                transaction.Commit();
                committed = true;
                return document;
            }
            finally { cache.ActiveTransaction = null; }
        }, ct, expectedBinding: expectedBinding, expectedIdentity: expectedIdentity);
        return Finish(result, committed, "opened");
    }

    internal async Task<DraftEditingResult> StageAsync(DraftStageRequest request, CancellationToken ct = default)
    {
        if (!IsValidRequest(request))
            return Refused("usage", "Version 1, exact native context, canonical decimal tokens and 1-128 ordered changes are required.");
        // Freeze caller-owned collections before any asynchronous native admission.
        request = request with { Changes = request.Changes.ToArray() };
        var committed = false;
        DraftEditingDocument? committedDocument = null;
        var result = await operations.ExecuteExplicitAsync(request.NativeContext.BenchId, async (capture, token) =>
        {
            RequireAdo();
            if (cache.ActiveTransaction is not null)
                throw Error("storage-busy", "Draft editing must own its native transaction.");
            var origin = await origins.GetOriginAsync(token);
            await operations.EnsureCurrentAsync(capture, token);
            using var transaction = cache.GetConnection().BeginTransaction(deferred: false);
            cache.ActiveTransaction = transaction;
            try
            {
                var key = Key(capture, request.NativeContext.ItemKey.WorkItemId);
                if (key != request.NativeContext.ItemKey)
                    throw Error("authority-invalidated", "The qualified native item identity changed.");
                var evidenceStore = new SqliteDraftCaptureEvidenceStore(cache);
                var evidence = evidenceStore.Read(key);
                if (Context(evidence, capture) != request.NativeContext)
                    throw Error("authority-invalidated", "The Bench, origin or native served observation changed.");
                var item = await repository.GetByIdAsync(key.WorkItemId, token)
                    ?? throw Error("item-unavailable", "The native item is no longer cached.");
                if (item.IsDirty || item.IsSeed || item.PendingNotes.Count > 0 ||
                    (await pending.GetChangesAsync(key.WorkItemId, token)).Count > 0)
                    throw Error("pending-work", "Native pending work changed; draft intent was retained.");
                var snapshot = new WorkItemMapper().ToSnapshot(item);
                var draft = await drafts.GetAsync(key, token)
                    ?? throw Error("draft-unavailable", "Explicitly open the native draft before staging.");
                if (Decimal(draft.Generation) != request.Generation)
                    throw Error("draft-cas-conflict", "The draft generation changed. No retry was attempted.");
                if (draft.Generation > long.MaxValue - request.Changes.Count)
                    throw Error("generation-exhausted", "The draft generation cannot advance without overflow.");
                if (draft.HasConflicts || Decimal(draft.BaselineRevision) != request.BaselineRevision ||
                    Decimal(draft.ObservedRevision) != request.ObservedRevision ||
                    draft.BaselineRevision != snapshot.Revision || draft.ObservedRevision != snapshot.Revision)
                    throw Error("revision-conflict", "The exact baseline or observation changed; reconcile without rebasing intent.");
                var document = WorkItemDetailProjector.Project(evidence.Layout, snapshot);
                var fields = Fields(document, evidence);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var candidate = draft;
                foreach (var change in request.Changes)
                {
                    if (!seen.Add(change.FieldRef))
                        throw Error("duplicate-field", "A field was specified more than once.");
                    var field = fields.SingleOrDefault(field =>
                        field.FieldRef.Equals(change.FieldRef, StringComparison.OrdinalIgnoreCase));
                    if (field is null || !field.Editable)
                        throw Error("field-unavailable", field?.Reason ?? "The field is absent from the native served form.");
                    if (!WorkItemDetailProjector.TryGetRawValue(snapshot, field.FieldRef, out var baseline))
                        throw Error("field-not-carried", "The exact native baseline value is not carried.");
                    candidate = candidate.SetField(field.FieldRef, baseline, change.ProposedValue);
                }
                // Validate the whole resulting intent, including carried state gates, before any write.
                await evidenceStore.ValidateAsync(candidate, snapshot, origin,
                    request.NativeContext.EvidenceDigest, token);
                if (!await drafts.TrySaveAsync(candidate, draft.Generation, token))
                    throw Error("draft-cas-conflict", "Another surface changed the draft. No retry was attempted.");
                var next = Document(candidate, snapshot, document, evidence, capture);
                token.ThrowIfCancellationRequested();
                transaction.Commit();
                committed = true;
                committedDocument = next;
                return next;
            }
            finally { cache.ActiveTransaction = null; }
        }, ct, expectedBinding: request.NativeContext.ItemKey.BindingId);
        // A failed post-commit origin fence must not falsely claim that nothing was written.
        if (result.Failure is not null && committed)
            return new(1, "indeterminate", true, committedDocument,
                new("post-commit-fence", result.Failure.Message, "Reopen and inspect the retained draft; do not retry automatically."));
        return Finish(result, false, "staged", committed);
    }

    private void RequireAdo()
    {
        if (provider.Capabilities.ProviderKind != "ado")
            throw Error("provider-unsupported", "This native form editor supports numeric ADO items only; cross-provider editing is unavailable.");
    }

    internal static bool IsValidRequest(DraftStageRequest? request)
        => request is { Version: 1, NativeContext.ItemKey.WorkItemId: > 0, Changes.Count: > 0 and <= 128 }
            && request.NativeContext.ExternalIdentity is not null
            && Token(request.NativeContext.BenchId, true)
            && !string.IsNullOrWhiteSpace(request.NativeContext.BenchName)
            && !string.IsNullOrWhiteSpace(request.NativeContext.IdentityId)
            && !string.IsNullOrWhiteSpace(request.NativeContext.SelectionSource)
            && !string.IsNullOrWhiteSpace(request.NativeContext.BenchContentsDigest)
            && !string.IsNullOrWhiteSpace(request.NativeContext.OriginDigest)
            && !string.IsNullOrWhiteSpace(request.NativeContext.EvidenceDigest)
            && Token(request.NativeContext.BindingRevision, false)
            && Token(request.NativeContext.SelectionRevision, false)
            && Token(request.NativeContext.AttachmentRevision, false)
            && request.Changes.All(change => change is not null && !string.IsNullOrWhiteSpace(change.FieldRef))
            && Token(request.Generation, false) && Token(request.BaselineRevision, true) && Token(request.ObservedRevision, true)
            && (request.NativeContext.StorageGeneration is null ||
                !string.IsNullOrWhiteSpace(request.NativeContext.StorageGeneration));

    private static bool Token(string? value, bool positive)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= (positive ? 1 : 0) && Decimal(parsed) == value;
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static QualifiedWorkItemKey Key(BrowserCapture capture, int id)
        => new(capture.Origin.WorktreeRoot, capture.Origin.Binding.ConnectionRef, capture.BindingId, id);
    private static InvalidOperationException Error(string code, string message) => new(code + ": " + message);

    private static DraftEditingContext Context(DraftCaptureEvidence evidence, BrowserCapture capture)
        => new(evidence.ItemKey, evidence.ExternalIdentity
                ?? throw Error("metadata-unavailable", "Explicitly open this item using the native editing contract."),
            capture.BenchId, capture.BenchName, capture.BenchContentsDigest,
            evidence.Origin.IdentityId, Decimal(evidence.Origin.BindingRevision), evidence.Origin.SelectionSource,
            Decimal(evidence.Origin.SelectionRevision), Decimal(evidence.Origin.AttachmentRevision), evidence.StorageGeneration,
            PlanCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(evidence.Origin,
                TwigJsonContext.Default.PlanOrigin)).Digest, SqliteDraftCaptureEvidenceStore.Fingerprint(evidence));

    private DraftEditingDocument Document(RecoverableWorkItemDraft draft, WorkItemSnapshot snapshot,
        WorkItemDetailDocument document, DraftCaptureEvidence evidence, BrowserCapture capture, string? eligibilityIssue = null)
        => new(Context(evidence, capture), Decimal(draft.Generation), Decimal(draft.BaselineRevision),
            Decimal(draft.ObservedRevision), Decimal(snapshot.Revision), draft.HasConflicts,
            draft.Changes.Select(change => new DraftEditingEffect(change.FieldRef, change.BaselineValue, change.ProposedValue)).ToArray(),
            draft.Conflicts.Select(conflict => new DraftEditingConflict(conflict.FieldRef, conflict.BaselineValue,
                conflict.ObservedValue, conflict.ProposedValue)).ToArray(),
            document.SystemControls, document.Pages, Fields(document, evidence, eligibilityIssue), eligibilityIssue);

    private DraftEditingField[] Fields(WorkItemDetailDocument document, DraftCaptureEvidence evidence, string? eligibilityIssue = null)
    {
        var persistable = new PendingChangeStoreSink(pending).PersistableFieldRefs;
        return document.SystemControls.Concat(document.Pages.SelectMany(page => page.AllGroups)
                .SelectMany(group => group.Controls))
            .GroupBy(control => control.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var control = group.First();
                var carried = WorkItemDetailProjector.CarriedControls(document)
                    .Any(candidate => candidate.Id.Equals(control.Id, StringComparison.OrdinalIgnoreCase));
                var reason = eligibilityIssue ?? (group.Count() > 1 ? "The served form contains ambiguous repeated controls."
                    : !carried ? "The native form does not carry an editable field value."
                    : control.ReadOnly ? "The native served control is read-only."
                    : !persistable.Contains(control.Id) ? "The native publication path does not persist this field."
                    : evidence.Rules is null ? "Native process rules were not observed."
                    : null);
                return new DraftEditingField(control.Id, reason is null, reason);
            }).ToArray();
    }

    private static RecoverableWorkItemDraft Observe(RecoverableWorkItemDraft draft, WorkItemSnapshot snapshot)
    {
        var conflicts = draft.Changes.Select(change =>
        {
            if (!WorkItemDetailProjector.TryGetRawValue(snapshot, change.FieldRef, out var value))
                throw Error("field-not-carried", "A retained draft field is no longer carried. Its prior observation and intent were retained.");
            return (change, value);
        }).Where(pair => pair.value != pair.change.BaselineValue)
            .Select(pair => new DraftFieldConflict(pair.change.FieldRef, pair.change.BaselineValue,
                pair.value, pair.change.ProposedValue)).ToArray();
        return draft.Observe(snapshot.Revision, conflicts);
    }

    private static DraftEditingResult Finish(BrowserOperationResult<DraftEditingDocument> result,
        bool observationCommitted, string status, bool staged = false)
    {
        if (result.Failure is not { } failure)
            return new(1, status, observationCommitted || staged, result.Value);
        var split = failure.Message.IndexOf(": ", StringComparison.Ordinal);
        var code = failure.Kind == BrowserReadFailureKind.AuthorityInvalidated ? "authority-invalidated"
            : failure.Kind == BrowserReadFailureKind.Canceled ? "canceled"
            : split > 0 ? failure.Message[..split] : "unavailable";
        return observationCommitted
            ? new(1, "indeterminate", true, Error: new("post-commit-fence", failure.Message,
                "Reopen and inspect the retained draft; do not retry automatically."))
            : Refused(code, failure.Message);
    }

    internal static DraftEditingResult Refused(string code, string message, bool committed = false)
        => new(1, "refused", committed, Error: new(code, message,
            "Retain intent and explicitly reopen or reconcile; never retry or rebase automatically."));
}
