using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Store.Sqlite;
using Model = Twig.Core.Core;

namespace Twig.Infrastructure.Services;

public sealed class NativePlanOutcome
{
    public string Status { get; }
    public int VerifiedScopes { get; }
    public int DeliveredScopes { get; }
    public int RemainingScopes { get; }
    public int VerifiedOperations { get; }
    public int RemainingOperations { get; }
    public Error? Error { get; }
    internal NativePlanOutcome(string status, int verified, int delivered, int remaining,
        int verifiedOperations, int remainingOperations, Error? error)
        => (Status, VerifiedScopes, DeliveredScopes, RemainingScopes, VerifiedOperations, RemainingOperations, Error)
            = (status, verified, delivered, remaining, verifiedOperations, remainingOperations, error);
}

/// <summary>Whole-Plan authorization references. Core remains the sole dispatcher and remote outcome authority.</summary>
public sealed class NativePlanReview
{
    private readonly string root;
    private readonly Action<IServiceCollection>? configure;
    private readonly UserBenchStore user;
    private NativePlanReviewReference reference;
    public string Digest => reference.Digest;
    public Guid BenchId => reference.Document.BenchId;
    public ImmutableArray<Op> Operations => reference.Document.Scopes.SelectMany(scope =>
        scope.Proposal is null ? ImmutableArray<Op>.Empty : Parse(scope.Proposal).Operations).ToImmutableArray();
    public ImmutableArray<string> CoreDigests => reference.Document.Scopes.Where(scope => scope.Proposal is not null)
        .Select(scope => Parse(scope.Proposal!).Digest).ToImmutableArray();
    public ImmutableArray<Proposal> Proposals => reference.Document.Scopes.Where(scope => scope.Proposal is not null)
        .Select(scope => Parse(scope.Proposal!)).ToImmutableArray();
    public ImmutableArray<NativePlanSource> Sources => reference.Document.Scopes.Select(value =>
        new NativePlanSource(value.Scope.Id, value.Scope.StoreInstance, value.Scope.OriginRoot,
            value.Scope.Source, value.Scope.State.ToString())).ToImmutableArray();

    private NativePlanReview(string root, UserBenchStore user, NativePlanReviewReference reference, Action<IServiceCollection>? configure)
        => (this.root, this.user, this.reference, this.configure) = (root, user, reference, configure);

    public static Task<Result<NativePlanReview>> ReviewAsync(string root, Guid benchId,
        ImmutableArray<NativePlanSource> admittedSources, CancellationToken ct = default)
        => ReviewAsync(root, benchId, admittedSources, null, ct);

    internal static async Task<Result<NativePlanReview>> ReviewAsync(string root, Guid benchId,
        ImmutableArray<NativePlanSource> admittedSources, Action<IServiceCollection>? configure, CancellationToken ct)
    {
        try
        {
            using var runtime = await Runtime(root, configure, ct).ConfigureAwait(false);
            var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
            var user = UserBenchStore.ForRegistry(factory.ExecutionRegistryPath);
            using var benchLease = user.AcquireAuthoringLease(benchId);
            var library = await user.ReadAsync(ct).ConfigureAwait(false);
            if (!library.IsSuccess) return Result<NativePlanReview>.Fail(library.Error);
            var plan = await user.ReadPlanAsync(benchId, ct).ConfigureAwait(false);
            if (!plan.IsSuccess) return Result<NativePlanReview>.Fail(plan.Error);
            var bench = library.Value.Benches.SingleOrDefault(value => value.Id == benchId);
            if (bench is null || !plan.Value.PlanMode)
                return Result<NativePlanReview>.Fail(ErrorCode.Conflict, "Select the named Bench and enter Plan mode before review.");
            var scopes = plan.Value.Scopes.Where(scope => scope.State != NativePlanScopeState.Superseded).OrderBy(scope => scope.Id).ToArray();
            if (scopes.Any(scope => scope.State != NativePlanScopeState.Ready || !admittedSources.Any(source =>
                source.Id == scope.Id && source.StoreInstance == scope.StoreInstance && source.OriginRoot == scope.OriginRoot
                && source.Source == scope.Source)))
                return Result<NativePlanReview>.Fail(ErrorCode.Conflict, "Choose every exact Plan connection. Missing or replaced scopes remain protected.");
            var roots = scopes.Select(scope => scope.OriginRoot).Distinct(NativePathComparer.Instance).Order(StringComparer.Ordinal).ToArray();
            var leases = new List<IDisposable>();
            try
            {
                foreach (var origin in roots) leases.Add(ConnectionOperationGate.Acquire(origin, exclusive: true));
                var captures = await user.ReadEditorCapturesAsync(ct).ConfigureAwait(false);
                if (!captures.IsSuccess) return Result<NativePlanReview>.Fail(captures.Error);
                var results = ImmutableArray.CreateBuilder<NativePlanReviewScope>();
                foreach (var scope in scopes)
                {
                    using var sourceRuntime = await Runtime(scope.OriginRoot, configure, ct).ConfigureAwait(false);
                    var sourceFactory = sourceRuntime.GetRequiredService<CoreWorkspaceFactory>();
                    if (!NativePathComparer.Instance.Equals(sourceFactory.ExecutionRegistryPath, factory.ExecutionRegistryPath))
                        return Result<NativePlanReview>.Fail(ErrorCode.Conflict, "The Plan origins belong to different native user registries.");
                    var opened = await sourceFactory.OpenScopedAsync(scope, scope.Source, ct).ConfigureAwait(false);
                    if (!opened.IsSuccess) return Result<NativePlanReview>.Fail(opened.Error);
                    using var handle = opened.Value;
                    var snapshot = await handle.Workspace.SnapshotAsync(ct).ConfigureAwait(false);
                    if (!snapshot.IsSuccess) return Result<NativePlanReview>.Fail(snapshot.Error);
                    if (snapshot.Value.Journal.Any(row => row.Status != OpStatus.Planned && row.Status != OpStatus.Verified))
                        return Result<NativePlanReview>.Fail(ErrorCode.Indeterminate, "An original Core attempt remains unresolved. Preserve its receipts; no replay is available.");
                    var owned = captures.Value.Where(capture => capture.PlanId == plan.Value.Id && capture.ScopeId == scope.Id
                        && capture.PlanMode && !capture.Cancelled && !capture.Settled).ToImmutableArray();
                    if (snapshot.Value.Drafts.IsEmpty && owned.IsEmpty) continue;
                    if (owned.Length != snapshot.Value.Drafts.Length || owned.Any(capture => !capture.Ready
                        || capture.Origin != scope.Source || capture.StoreInstance != scope.StoreInstance
                        || capture.Root != scope.OriginRoot || !snapshot.Value.Drafts.Any(draft =>
                            draft.Key == Parse(capture.IntentJson).Operations.Single().Item
                            && handle.Workspace.DraftFingerprint(draft) == capture.DraftFingerprint)))
                        return Result<NativePlanReview>.Fail(ErrorCode.Conflict, "Protected Plan captures and drafts disagree; no debt was adopted or omitted.");
                    var ordered = new List<NativeEditorCapture>();
                    var remaining = owned.OrderBy(capture => capture.Id).ToList();
                    while (remaining.Count != 0)
                    {
                        var next = remaining.FirstOrDefault(capture =>
                        {
                            var op = Parse(capture.IntentJson).Operations.Single();
                            return !remaining.Any(other => other != capture && Parse(other.IntentJson).Operations.Single().Item is { } key
                                && (key == op.Other || op.Links.Any(link => link.Other == key)));
                        });
                        if (next is null) return Result<NativePlanReview>.Fail(ErrorCode.Conflict, "Creation-bound Plan dependencies contain a cycle.");
                        ordered.Add(next); remaining.Remove(next);
                    }
                    var operations = new List<Op>();
                    var previews = ImmutableArray.CreateBuilder<string>();
                    foreach (var capture in ordered)
                    {
                        var intent = Parse(capture.IntentJson).Operations.Single();
                        var preview = intent.Item.IsLocal
                            ? await handle.Workspace.PreviewDraftAsync(intent.Item, ct, operations.Where(op => op.Kind == OpKind.Create)).ConfigureAwait(false)
                            : await handle.Workspace.PreviewEditorDraftAsync(intent.Item, intent.Links, intent.ExpectedRevision, ct, capture.Endpoints).ConfigureAwait(false);
                        if (!preview.IsSuccess) return Result<NativePlanReview>.Fail(preview.Error);
                        previews.Add(preview.Value.Proposal is null ? "" : Bytes(preview.Value.Proposal));
                        if (preview.Value.Proposal is { } proposal)
                        {
                            var effect = proposal.Operations.Single(op => op.Item == intent.Item);
                            operations.Add(new("plan-" + operations.Count, effect.Kind, effect.Item, effect.ExpectedRevision,
                                effect.Fields, effect.Type, effect.Other, effect.Relation, effect.Origin, effect.Body,
                                effect.Concurrency, effect.ExpectedLocalVersion, effect.Resource, effect.Links));
                        }
                    }
                    Proposal? combined = null;
                    if (operations.Count != 0)
                    {
                        var prepared = await handle.Workspace.PrepareAsync(operations, ct).ConfigureAwait(false);
                        if (!prepared.IsSuccess) return Result<NativePlanReview>.Fail(prepared.Error);
                        combined = prepared.Value;
                    }
                    snapshot = await handle.Workspace.SnapshotAsync(ct).ConfigureAwait(false);
                    if (!snapshot.IsSuccess) return Result<NativePlanReview>.Fail(snapshot.Error);
                    results.Add(new(scope, snapshot.Value.Version, ordered.ToImmutableArray(), previews.ToImmutable(),
                        combined is null ? null : Bytes(combined)));
                }
                if (results.Count == 0)
                    return Result<NativePlanReview>.Fail(ErrorCode.NotFound, "This Plan has no staged intent to review.");
                var document = new NativePlanReviewDocument(1, Guid.CreateVersion7(), library.Value.InstanceId, library.Value.Revision,
                    benchId, bench.Revision, plan.Value.Id, plan.Value.Revision, results.ToImmutable());
                var retained = await user.RetainPlanReviewAsync(document, ct).ConfigureAwait(false);
                return retained.IsSuccess ? Result<NativePlanReview>.Ok(new(root, user, retained.Value, configure))
                    : Result<NativePlanReview>.Fail(retained.Error);
            }
            finally { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
        }
        catch (ConnectionBindingResolutionException)
        { return Result<NativePlanReview>.Fail(ErrorCode.Conflict, "An active native operation protects the Plan."); }
    }

    public static Task<Result<NativePlanReview>> OpenAsync(string root, string digest, CancellationToken ct = default)
        => OpenAsync(root, digest, null, ct);
    internal static async Task<Result<NativePlanReview>> OpenAsync(string root, string digest,
        Action<IServiceCollection>? configure, CancellationToken ct)
    {
        using var runtime = await Runtime(root, configure, ct).ConfigureAwait(false);
        var user = UserBenchStore.ForRegistry(runtime.GetRequiredService<CoreWorkspaceFactory>().ExecutionRegistryPath);
        var retained = await user.ReadPlanReviewAsync(digest, ct).ConfigureAwait(false);
        return retained.IsSuccess ? Result<NativePlanReview>.Ok(new(root, user, retained.Value, configure))
            : Result<NativePlanReview>.Fail(retained.Error);
    }

    public Task<Result<NativePlanOutcome>> ApplyAsync(string exactDigest, CancellationToken ct = default)
        => Execute(exactDigest, false, ct);
    public Task<Result<NativePlanOutcome>> ContinueDeliveryAsync(string exactDigest, CancellationToken ct = default)
        => Execute(exactDigest, true, ct);

    public async Task<Result<NativePlanOutcome>> ObserveAsync(CancellationToken ct = default)
    {
        var retained = await user.ReadPlanReviewAsync(Digest, ct).ConfigureAwait(false);
        if (!retained.IsSuccess) return Result<NativePlanOutcome>.Fail(retained.Error);
        reference = retained.Value;
        return Result<NativePlanOutcome>.Ok(await Outcome(null, ct).ConfigureAwait(false));
    }

    private async Task<Result<NativePlanOutcome>> Execute(string exactDigest, bool deliveryOnly, CancellationToken ct)
    {
        if (exactDigest != Digest) return Result<NativePlanOutcome>.Fail(ErrorCode.DigestMismatch, "Apply only the exact native whole-Plan digest reviewed.");
        try
        {
            using var benchLease = user.AcquireAuthoringLease(BenchId);
            var retained = await user.ReadPlanReviewAsync(Digest, ct).ConfigureAwait(false);
            if (!retained.IsSuccess) return Result<NativePlanOutcome>.Fail(retained.Error);
            reference = retained.Value;
            if (reference.Delivered == reference.Document.Scopes.Length)
                return Result<NativePlanOutcome>.Ok(await Outcome(null, ct).ConfigureAwait(false));
            if (reference.Started && !deliveryOnly)
                return Result<NativePlanOutcome>.Ok(await Outcome(new(ErrorCode.Indeterminate,
                    "This original Apply was already admitted. Inspect its receipts or continue delivery only; never replay it."), ct).ConfigureAwait(false));
            if (!reference.Started && deliveryOnly)
                return Result<NativePlanOutcome>.Fail(ErrorCode.Conflict, "No original Apply exists to deliver.");
            var leases = new List<IDisposable>();
            try
            {
                foreach (var origin in reference.Document.Scopes.Select(scope => scope.Scope.OriginRoot)
                    .Distinct(NativePathComparer.Instance).Order(StringComparer.Ordinal))
                    leases.Add(ConnectionOperationGate.Acquire(origin, exclusive: true));
                var library = await user.ReadAsync(ct).ConfigureAwait(false);
                if (!library.IsSuccess) return Result<NativePlanOutcome>.Fail(library.Error);
                if (!deliveryOnly)
                {
                    var current = await ValidateWhole(library.Value, ct).ConfigureAwait(false);
                    if (!current.IsSuccess) return Result<NativePlanOutcome>.Fail(current.Error);
                    foreach (var scope in reference.Document.Scopes)
                    {
                        var valid = await ValidateScope(scope, ct).ConfigureAwait(false);
                        if (!valid.IsSuccess) return Result<NativePlanOutcome>.Fail(valid.Error);
                    }
                    var admitted = await user.AdvancePlanReviewAsync(reference, true, 0, 0, ct).ConfigureAwait(false);
                    if (!admitted.IsSuccess) return Result<NativePlanOutcome>.Fail(admitted.Error);
                    reference = reference with { Started = true };
                }
                for (var index = reference.Delivered; index < reference.Document.Scopes.Length; index++)
                {
                    var scope = reference.Document.Scopes[index];
                    using var runtime = await Runtime(scope.Scope.OriginRoot, configure, ct).ConfigureAwait(false);
                    var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
                    var opened = await factory.OpenScopedAsync(scope.Scope, scope.Scope.Source, ct).ConfigureAwait(false);
                    if (!opened.IsSuccess) return Result<NativePlanOutcome>.Ok(await Outcome(opened.Error, ct).ConfigureAwait(false));
                    using var handle = opened.Value;
                    if (!deliveryOnly)
                    {
                        var currentLibrary = await user.ReadAsync(ct).ConfigureAwait(false);
                        if (!currentLibrary.IsSuccess) return Result<NativePlanOutcome>.Ok(await Outcome(currentLibrary.Error, ct).ConfigureAwait(false));
                        if (currentLibrary.Value.InstanceId != library.Value.InstanceId || currentLibrary.Value.Revision != library.Value.Revision)
                            return Result<NativePlanOutcome>.Ok(await Outcome(new(ErrorCode.Conflict, "The shared Bench changed after Apply began; remaining effects are protected."), ct).ConfigureAwait(false));
                        var valid = await ValidateScope(scope, ct).ConfigureAwait(false);
                        if (!valid.IsSuccess) return Result<NativePlanOutcome>.Ok(await Outcome(valid.Error, ct).ConfigureAwait(false));
                        var snapshot = await handle.Workspace.SnapshotAsync(ct).ConfigureAwait(false);
                        if (!snapshot.IsSuccess) return Result<NativePlanOutcome>.Ok(await Outcome(snapshot.Error, ct).ConfigureAwait(false));
                        var previews = scope.Captures.Select((capture, ordinal) => new CoreDraftPreview(
                            snapshot.Value.Drafts.Single(draft => handle.Workspace.DraftFingerprint(draft) == capture.DraftFingerprint
                                && draft.Key == Parse(capture.IntentJson).Operations.Single().Item),
                            scope.Previews[ordinal].Length == 0 ? null : Parse(scope.Previews[ordinal]))).ToImmutableArray();
                        var proposal = scope.Proposal is null ? null : Parse(scope.Proposal);
                        var admitted = await user.AdvancePlanReviewAsync(reference, true, reference.Delivered, index + 1, ct).ConfigureAwait(false);
                        if (!admitted.IsSuccess) return Result<NativePlanOutcome>.Ok(await Outcome(admitted.Error, ct).ConfigureAwait(false));
                        reference = reference with { Admitted = index + 1 };
                        var saved = await handle.Workspace.SaveDraftSetAsync(previews, proposal, proposal?.Digest, ct).ConfigureAwait(false);
                        if (!saved.IsSuccess || !saved.Value.EffectsVerified || saved.Value.DraftRetained || saved.Value.Error is not null)
                            return Result<NativePlanOutcome>.Ok(await Outcome(saved.IsSuccess ? saved.Value.Error
                                ?? new(ErrorCode.Indeterminate, "Core effects or draft settlement remain unconfirmed.") : saved.Error, ct).ConfigureAwait(false));
                    }
                    var delivered = await Deliver(scope, factory, ct).ConfigureAwait(false);
                    if (!delivered.IsSuccess) return Result<NativePlanOutcome>.Ok(await Outcome(delivered.Error, ct).ConfigureAwait(false));
                    var advanced = await user.AdvancePlanReviewAsync(reference, true, index + 1, reference.Admitted, ct).ConfigureAwait(false);
                    if (!advanced.IsSuccess) return Result<NativePlanOutcome>.Ok(await Outcome(advanced.Error, ct).ConfigureAwait(false));
                    reference = reference with { Delivered = index + 1 };
                    library = await user.ReadAsync(ct).ConfigureAwait(false);
                    if (!library.IsSuccess) return Result<NativePlanOutcome>.Ok(await Outcome(library.Error, ct).ConfigureAwait(false));
                }
                return Result<NativePlanOutcome>.Ok(await Outcome(null, ct).ConfigureAwait(false));
            }
            finally { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
        }
        catch (ConnectionBindingResolutionException)
        { return Result<NativePlanOutcome>.Fail(ErrorCode.Conflict, "An active native operation protects the original Plan attempt."); }
    }

    private async Task<Result<bool>> ValidateWhole(UserBenchSnapshot library, CancellationToken ct)
    {
        var document = reference.Document;
        var plan = await user.ReadPlanAsync(BenchId, ct).ConfigureAwait(false);
        if (!plan.IsSuccess) return Result<bool>.Fail(plan.Error);
        return library.InstanceId == document.UserInstance && library.Revision == document.UserRevision
            && library.Benches.Any(bench => bench.Id == BenchId && bench.Revision == document.BenchRevision)
            && plan.Value.PlanMode && plan.Value.Id == document.PlanId && plan.Value.Revision == document.PlanRevision
            && document.Scopes.All(scope => plan.Value.Scopes.Contains(scope.Scope))
            ? Result<bool>.Ok(true) : Result<bool>.Fail(ErrorCode.Conflict, "The reviewed user store, Bench, Plan mode or scope changed. Review fresh intent.");
    }

    private async Task<Result<bool>> ValidateScope(NativePlanReviewScope scope, CancellationToken ct)
    {
        using var runtime = await Runtime(scope.Scope.OriginRoot, configure, ct).ConfigureAwait(false);
        var opened = await runtime.GetRequiredService<CoreWorkspaceFactory>().OpenScopedAsync(scope.Scope, scope.Scope.Source, ct).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<bool>.Fail(opened.Error);
        using var handle = opened.Value;
        var snapshot = await handle.Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<bool>.Fail(snapshot.Error);
        var captures = await user.ReadEditorCapturesAsync(ct).ConfigureAwait(false);
        if (!captures.IsSuccess) return Result<bool>.Fail(captures.Error);
        if (snapshot.Value.Version != scope.StoreVersion || scope.Captures.Any(capture => !captures.Value.Any(current => current.SameAs(capture))))
            return Result<bool>.Fail(ErrorCode.Conflict, "The exact reviewed Plan draft, capture or store revision changed.");
        if (scope.Proposal is not null)
        {
            var checkedProposal = await handle.Workspace.CheckProposalAsync(Parse(scope.Proposal), ct).ConfigureAwait(false);
            if (!checkedProposal.IsSuccess) return Result<bool>.Fail(checkedProposal.Error);
            if (!checkedProposal.Value.IsEmpty) return Result<bool>.Fail(checkedProposal.Value[0]);
        }
        return Result<bool>.Ok(true);
    }

    private async Task<Result<bool>> Deliver(NativePlanReviewScope scope, CoreWorkspaceFactory factory, CancellationToken ct)
    {
        var snapshot = await Snapshot(scope.Scope, ct).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<bool>.Fail(snapshot.Error);
        var proposal = scope.Proposal is null ? null : Parse(scope.Proposal);
        if (proposal is not null && !Verified(snapshot.Value, proposal))
            return Result<bool>.Fail(ErrorCode.Indeterminate, "The original Core receipts are not all Verified. Delivery never dispatches or guesses an outcome.");
        if (proposal is null)
        {
            var pending = ImmutableArray.CreateBuilder<CoreDraftPreview>();
            foreach (var capture in scope.Captures)
            {
                var key = Parse(capture.IntentJson).Operations.Single().Item;
                var draft = snapshot.Value.Drafts.SingleOrDefault(value => value.Key == key);
                if (draft is null) continue;
                if (CoreWorkspace.Fingerprint(scope.Scope.Source.Connection, draft) != capture.DraftFingerprint)
                    return Result<bool>.Fail(ErrorCode.Conflict, "Converged local settlement cannot consume changed Plan intent.");
                pending.Add(new(draft, null));
            }
            if (pending.Count != 0)
            {
                var openedScope = await factory.OpenScopedAsync(scope.Scope, scope.Scope.Source, ct).ConfigureAwait(false);
                if (!openedScope.IsSuccess) return Result<bool>.Fail(openedScope.Error);
                using var handle = openedScope.Value;
                // The immutable whole review contains no Core proposal: this path can only settle a freshly converged local draft set.
                var settled = await handle.Workspace.SaveDraftSetAsync(pending, null, null, ct).ConfigureAwait(false);
                if (!settled.IsSuccess) return Result<bool>.Fail(settled.Error);
                if (settled.Value.DraftRetained || settled.Value.Error is not null)
                    return Result<bool>.Fail(settled.Value.Error ?? new(ErrorCode.Conflict, "Local no-effect settlement remains pending."));
            }
        }
        else if (scope.Captures.Any(capture => snapshot.Value.Drafts.Any(draft =>
            draft.Key == Parse(capture.IntentJson).Operations.Single().Item)))
            return Result<bool>.Fail(ErrorCode.Conflict, "Remote effects verified; retained Plan drafts still require exact native local settlement.");
        var opened = await factory.OpenAsync(ct).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<bool>.Fail(opened.Error);
        using var origin = opened.Value;
        if (origin.Source != scope.Scope.Source)
            return Result<bool>.Fail(ErrorCode.Conflict, "The original delivery source changed; verified effects remain protected.");
        var pins = new List<Selector>();
        foreach (var op in proposal?.Operations ?? ImmutableArray<Op>.Empty)
        {
            var key = op.Item;
            if (key.IsLocal)
            {
                var mapping = snapshot.Value.Mappings.SingleOrDefault(value => value.Local == key
                    && value.ProposalDigest == proposal!.Digest);
                if (mapping is null) return Result<bool>.Fail(ErrorCode.Indeterminate, "The attributable Verified seed mapping is unavailable.");
                key = mapping.Remote;
                pins.Add(new(SelectorKind.Pin, key, connection: proposal!.Connection));
            }
            var read = await origin.Workspace.ReadAsync(key, ct).ConfigureAwait(false);
            if (!read.IsSuccess) return Result<bool>.Fail(read.Error);
        }
        var library = await user.ReadAsync(ct).ConfigureAwait(false);
        if (!library.IsSuccess) return Result<bool>.Fail(library.Error);
        var bench = library.Value.Benches.SingleOrDefault(value => value.Id == BenchId);
        if (library.Value.InstanceId != reference.Document.UserInstance || bench is null)
            return Result<bool>.Fail(ErrorCode.Conflict, "The original Bench was replaced or deleted; verified delivery is pending.");
        var known = bench.Selectors.Select(selector => BenchCodec.SerializeSelectors([selector])).ToHashSet(StringComparer.Ordinal);
        var additions = pins.Where(pin => known.Add(BenchCodec.SerializeSelectors([pin]))).ToArray();
        if (additions.Length != 0)
        {
            var pinned = await user.SaveAsync(new(bench.Id, bench.Name, bench.Selectors.Concat(additions)), bench.Revision, ct).ConfigureAwait(false);
            if (!pinned.IsSuccess) return Result<bool>.Fail(pinned.Error);
            bench = pinned.Value;
        }
        var captures = await user.ReadEditorCapturesAsync(ct).ConfigureAwait(false);
        if (!captures.IsSuccess) return Result<bool>.Fail(captures.Error);
        var plan = await user.ReadPlanAsync(BenchId, ct).ConfigureAwait(false);
        if (!plan.IsSuccess) return Result<bool>.Fail(plan.Error);
        foreach (var capture in scope.Captures)
        {
            var current = captures.Value.SingleOrDefault(value => value.Id == capture.Id);
            if (current is null || !current.Settled && !current.SameAs(capture))
                return Result<bool>.Fail(ErrorCode.Conflict, "Original verified effects cannot consume changed captured intent.");
            if (current.Settled) continue;
            var settled = await user.SaveEditorCaptureAsync(capture with { Revision = capture.Revision + 1,
                BenchRevision = bench.Revision, PlanRevision = plan.Value.Revision, Settled = true }, capture.Revision, ct).ConfigureAwait(false);
            if (!settled.IsSuccess) return Result<bool>.Fail(settled.Error);
        }
        return Result<bool>.Ok(true);
    }

    private async Task<NativePlanOutcome> Outcome(Error? error, CancellationToken ct)
    {
        var verified = 0;
        var verifiedOperations = 0;
        var deliveryPending = false;
        foreach (var scope in reference.Document.Scopes)
        {
            var snapshot = await Snapshot(scope.Scope, ct).ConfigureAwait(false);
            if (!snapshot.IsSuccess) { error ??= snapshot.Error; continue; }
            if (scope.Proposal is not null && Verified(snapshot.Value, Parse(scope.Proposal)))
            {
                verified++;
                deliveryPending |= reference.Document.Scopes.IndexOf(scope) >= reference.Delivered;
            }
            if (scope.Proposal is not null)
                verifiedOperations += Parse(scope.Proposal).Operations.Select((_, ordinal) => snapshot.Value.Journal.Any(row =>
                    row.Digest == Parse(scope.Proposal).Digest && row.Operation == ordinal && row.Status == OpStatus.Verified)).Count(value => value);
        }
        var complete = reference.Delivered == reference.Document.Scopes.Length
            && verified == reference.Document.Scopes.Count(scope => scope.Proposal is not null) && error is null;
        return new(complete ? "Complete" : deliveryPending ? "VerifiedDeliveryPending"
            : verifiedOperations > 0 ? "PartialProtected" : "Protected", verified, reference.Delivered,
            reference.Document.Scopes.Length - reference.Delivered, verifiedOperations, Operations.Length - verifiedOperations, error);
    }

    private static bool Verified(StoreSnapshot snapshot, Proposal proposal)
        => snapshot.Proposals.Any(current => current.Digest == proposal.Digest)
            && proposal.Operations.Select((_, ordinal) => snapshot.Journal.Any(row =>
                row.Digest == proposal.Digest && row.Operation == ordinal && row.Status == OpStatus.Verified)).All(value => value);
    private static async Task<Result<StoreSnapshot>> Snapshot(NativePlanScope scope, CancellationToken ct)
    {
        var store = await SqliteStore.OpenAsync(scope.DatabasePath, ct, scope.StoreInstance).ConfigureAwait(false);
        return store.IsSuccess ? await store.Value.ReadAsync(scope.Source.Connection, ct).ConfigureAwait(false)
            : Result<StoreSnapshot>.Fail(store.Error);
    }
    private static Proposal Parse(string json) => UserBenchStore.ParseIntent(json);
    private static string Bytes(Proposal proposal) => Encoding.UTF8.GetString(Model.ProposalBytes(proposal));
    private static async Task<ServiceProvider> Runtime(string root, Action<IServiceCollection>? configure, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(root) || Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) != root)
            throw new ArgumentException("An exact canonical native origin is required.", nameof(root));
        var paths = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), new TwigConfiguration(), root);
        var configuration = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
        var services = new ServiceCollection();
        configure?.Invoke(services);
        services.AddCoreWorkspaceServices(configuration, paths);
        return services.BuildServiceProvider();
    }
}
