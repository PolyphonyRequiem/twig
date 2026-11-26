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

internal sealed record PlanCaptureUnit(Guid Scope, Guid Capture, string State, string? Digest, int? Operation, Guid? Attempt);

public sealed class NativePlanOutcome
{
    internal PlanCaptureUnit[] Units { get; init; } = [];
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

/// <summary>Exact prepared effects and the original whole-Plan attempt used for delivery and recovery.</summary>
public sealed class NativePlanReview
{
    private readonly string root;
    private readonly Action<IServiceCollection>? configure;
    private readonly UserBenchStore user;
    private NativePlanReviewReference reference;
    public string Digest => reference.Digest;
    internal string? StagedVersion { get; private init; }
    internal NativePlanReviewDocument Document => reference.Document;
    public Guid BenchId => reference.Document.BenchId;
    public ImmutableArray<Op> Operations => reference.Document.Scopes.SelectMany(scope =>
        scope.Plan is null ? ImmutableArray<Op>.Empty : Parse(scope.Plan).Operations).ToImmutableArray();
    public ImmutableArray<string> CoreDigests => reference.Document.Scopes.Where(scope => scope.Plan is not null)
        .Select(scope => Parse(scope.Plan!).Digest).ToImmutableArray();
    public ImmutableArray<Plan> Plans => reference.Document.Scopes.Where(scope => scope.Plan is not null)
        .Select(scope => Parse(scope.Plan!)).ToImmutableArray();
    public ImmutableArray<NativePlanSource> Sources => reference.Document.Scopes.Select(value =>
        new NativePlanSource(value.Scope.Id, value.Scope.StoreInstance, value.Scope.OriginRoot,
            value.Scope.Source, value.Scope.State.ToString())).ToImmutableArray();

    private NativePlanReview(string root, UserBenchStore user, NativePlanReviewReference reference, Action<IServiceCollection>? configure)
        => (this.root, this.user, this.reference, this.configure) = (root, user, reference, configure);

    public static Task<Result<NativePlanReview>> ReviewAsync(string root, Guid benchId,
        ImmutableArray<NativePlanSource> admittedSources, CancellationToken ct = default)
        => ReviewAsync(root, benchId, admittedSources, null, ct);

    internal static async Task<Result<NativePlanReview>> ReviewAsync(string root, Guid benchId,
        ImmutableArray<NativePlanSource> admittedSources, Action<IServiceCollection>? configure, CancellationToken ct,
        string? predecessorDigest = null)
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
            var stagedVersion = await user.ReadStagedPlanVersionAsync(benchId, ct).ConfigureAwait(false);
            if (!stagedVersion.IsSuccess) return Result<NativePlanReview>.Fail(stagedVersion.Error);
            NativePlanReviewDocument? predecessor = null;
            if (stagedVersion.Value.Status.LastReviewDigest is { } latest)
            {
                var prior = await user.ReadPlanReviewAsync(latest, ct).ConfigureAwait(false);
                if (!prior.IsSuccess) return Result<NativePlanReview>.Fail(prior.Error);
                predecessor = prior.Value.Document;
            }
            if (predecessorDigest is not null && predecessorDigest != stagedVersion.Value.Status.LastReviewDigest)
                return Result<NativePlanReview>.Fail(ErrorCode.Conflict, "Name the exact most recent Plan attempt for this Bench.");
            var namedNoEffect = false;
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
                var reviewRevision = Guid.CreateVersion7();
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
                    if (snapshot.Value.Journal.Any(row => row.Status != OpStatus.Planned && row.Status != OpStatus.Verified
                        && !UserBenchStore.DefinitivePlanNoEffect(snapshot.Value, row)))
                        return Result<NativePlanReview>.Fail(ErrorCode.Indeterminate, "An original Core attempt remains unresolved. Preserve its receipts; no replay is available.");
                    var owned = captures.Value.Where(capture => capture.PlanId == plan.Value.Id && capture.ScopeId == scope.Id
                        && capture.PlanMode && !capture.Cancelled && !capture.Settled).ToImmutableArray();
                    if (snapshot.Value.RetainedItems.IsEmpty && owned.IsEmpty) continue;
                    if (owned.Length != snapshot.Value.RetainedItems.Length || owned.Any(capture => !capture.Ready
                        || capture.Origin != scope.Source || capture.StoreInstance != scope.StoreInstance
                        || capture.Root != scope.OriginRoot || !snapshot.Value.RetainedItems.Any(draft =>
                            draft.Key == Parse(capture.IntentJson).Operations.Single().Item
                            && handle.Workspace.DraftFingerprint(draft) == capture.DraftFingerprint)))
                        return Result<NativePlanReview>.Fail(ErrorCode.Conflict, "Protected Plan captures and drafts disagree; no debt was adopted or omitted.");
                    var ordered = new List<NativeEditorCapture>();
                    var metadata = await handle.Workspace.CapturePlanReviewAsync(ct).ConfigureAwait(false);
                    if (!metadata.IsSuccess) return Result<NativePlanReview>.Fail(metadata.Error);
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
                        var priorScope = predecessor?.Scopes.SingleOrDefault(value => value.Scope == scope && value.Captures.Any(c => c.Id == capture.Id));
                        var priorPlan = priorScope?.Plan is { } bytes ? Parse(bytes) : null;
                        var failed = priorPlan is null ? null : snapshot.Value.Journal.SingleOrDefault(row => row.Digest == priorPlan.Digest
                            && priorPlan.Operations[row.Operation].Item == intent.Item && UserBenchStore.DefinitivePlanNoEffect(snapshot.Value, row));
                        if (failed is not null && predecessorDigest is null)
                            return Result<NativePlanReview>.Fail(ErrorCode.Conflict, "Explicitly re-review the named no-effect predecessor; ordinary review does not replace an attempt.");
                        namedNoEffect |= failed is not null;
                        var preview = intent.Item.IsLocal
                            ? await handle.Workspace.PreviewDraftAsync(intent.Item, ct, operations.Where(op => op.Kind == OpKind.Create), metadata.Value).ConfigureAwait(false)
                            : await handle.Workspace.PreviewEditorDraftAsync(intent.Item, intent.Links, intent.ExpectedRevision, ct, capture.Endpoints, metadata.Value).ConfigureAwait(false);
                        if (!preview.IsSuccess) return Result<NativePlanReview>.Fail(preview.Error);
                        previews.Add(preview.Value.Plan is null ? "" : Bytes(preview.Value.Plan));
                        if (preview.Value.Plan is { } itemPlan)
                        {
                            var effect = itemPlan.Operations.Single(op => op.Item == intent.Item);
                            operations.Add(effect.WithId(failed is null ? "plan-" + operations.Count
                                : $"plan-{operations.Count}:{failed.Digest}:{failed.Operation}:{failed.Attempt:D}"));
                        }
                    }
                    Plan? combined = null;
                    var versions = ordered.Select(capture => handle.Workspace.OwnerCapture(snapshot.Value.RetainedItems.Single(
                        draft => draft.Key == Parse(capture.IntentJson).Operations.Single().Item))).ToImmutableArray();
                    if (operations.Count != 0)
                    {
                        var binding = new ReviewBinding(reviewRevision, scope.StoreInstance, handle.Source.PrincipalId,
                            CaptureBinding(ordered, versions));
                        var prepared = await handle.Workspace.PrepareAsync(operations, ct, metadata.Value, binding: binding).ConfigureAwait(false);
                        if (!prepared.IsSuccess) return Result<NativePlanReview>.Fail(prepared.Error);
                        combined = prepared.Value;
                    }
                    snapshot = await handle.Workspace.SnapshotAsync(ct).ConfigureAwait(false);
                    if (!snapshot.IsSuccess) return Result<NativePlanReview>.Fail(snapshot.Error);
                    var unchangedPolicy = await handle.Workspace.RevalidatePlanReviewPolicyAsync(metadata.Value, ct).ConfigureAwait(false);
                    if (!unchangedPolicy.IsSuccess) return Result<NativePlanReview>.Fail(unchangedPolicy.Error);
                    results.Add(new(scope, versions, ordered.ToImmutableArray(), previews.ToImmutable(),
                        combined is null ? null : Bytes(combined)));
                }
                if (results.Count == 0)
                    return Result<NativePlanReview>.Fail(ErrorCode.NotFound, "This Plan has no staged intent to review.");
                if (predecessorDigest is not null && !namedNoEffect)
                    return Result<NativePlanReview>.Fail(ErrorCode.Conflict, "The named predecessor has no attributable no-effect attempt for these captures.");
                var document = new NativePlanReviewDocument(2, reviewRevision, library.Value.InstanceId, library.Value.Revision,
                    benchId, bench.Revision, plan.Value.Id, plan.Value.Revision, results.ToImmutable(), predecessorDigest);
                return Result<NativePlanReview>.Ok(new(root, user,
                    new(UserBenchStore.PlanReviewDigest(document), document, false, 0, 0), configure)
                    { StagedVersion = stagedVersion.Value.Version });
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

    public Task<Result<NativePlanOutcome>> ApplyAsync(CancellationToken ct = default)
        => Execute(false, ct);
    public Task<Result<NativePlanOutcome>> ContinueDeliveryAsync(CancellationToken ct = default)
        => Execute(true, ct);

    internal async Task<Result<bool>> ObserveConvergenceAsync(NativePlanScope scope, LocalEdits draft,
        string reviewedVersion, CancellationToken ct)
    {
        using var runtime = await Runtime(scope.OriginRoot, configure, ct).ConfigureAwait(false);
        var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
        var opened = await factory.OpenScopedAsync(scope, scope.Source, ct).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<bool>.Fail(opened.Error);
        using var handle = opened.Value;
        if (handle.Workspace.OwnerCapture(draft) != reviewedVersion)
            return Result<bool>.Fail(ErrorCode.Conflict, "The reviewed local edit was replaced.");
        var observed = await handle.Workspace.ObserveItemsAsync([draft.Key], ct).ConfigureAwait(false);
        if (!observed.IsSuccess) return Result<bool>.Fail(observed.Error);
        if (observed.Value is not { Complete: true, Continuation: null, Items: [var item] } || item.Key != draft.Key
            || draft.Type != draft.EditBaseline.Type && draft.Type != item.Type
            || !draft.Changes.All(field => item.Fields.TryGetValue(field.Key, out var value) && value.Equals(field.Value)))
            return Result<bool>.Fail(ErrorCode.Conflict, "The exact item no longer confirms the converged edit.");
        using var origin = (await factory.OpenAsync(ct)) is { IsSuccess: true } result ? result.Value : null;
        if (origin is null || origin.Source != scope.Source) return Result<bool>.Fail(ErrorCode.Conflict, "The reviewed origin changed.");
        return await factory.AdmitSourceAsync(origin, ct);
    }

    internal async Task<Result<bool>> ObserveVerifiedTargetAsync(NativePlanScope scope, StoreSnapshot state,
        JournalEntry row, ItemKey target, CancellationToken ct)
    {
        using var runtime = await Runtime(scope.OriginRoot, configure, ct, executionOwner: true).ConfigureAwait(false);
        var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
        using var registry = new SqliteSystemWorktreeRegistry(factory.ExecutionRegistryPath, TimeProvider.System);
        var refs = await registry.CoreAttemptReferencesAsync(scope.StoreInstance, ct);
        if (!refs.IsSuccess) return Result<bool>.Fail(refs.Error);
        var exact = refs.Value.Where(r => r.Digest == row.Digest && r.Operation == row.Operation).ToArray();
        if (exact.Length == 0 || !NativeCoreExecution.ReferencesMatch(state, exact)
            || exact.Any(r => r.Attempt != row.Attempt))
            return Result<bool>.Fail(ErrorCode.Indeterminate, "Original scope and registry attempt evidence disagree.");
        var opened = await factory.OpenAsync(ct);
        if (!opened.IsSuccess) return Result<bool>.Fail(opened.Error);
        using var origin = opened.Value;
        if (origin.Source != scope.Source) return Result<bool>.Fail(ErrorCode.Conflict, "The reviewed origin changed.");
        var admitted = await factory.AdmitSourceAsync(origin, ct);
        if (!admitted.IsSuccess) return admitted;
        var read = await origin.Workspace.ObserveItemsAsync([target], ct);
        if (!read.IsSuccess) return Result<bool>.Fail(read.Error);
        return read.Value is { Complete: true, Continuation: null, Items: [var item] } && item.Key == target
            ? await factory.RevalidateSourceAsync(origin, ct) : Result<bool>.Fail(ErrorCode.Partial, "No exact original target readback.");
    }

    internal async Task<Result<bool>> VerifyAcknowledgementsAsync(NativePlanReviewReference original,
        NativePlanReviewScope entry, CancellationToken ct, int? operation = null)
    {
        var scope = entry.Scope;
        var index = original.Document.Scopes.IndexOf(entry);
        if (original.Digest != Digest || index < 0 || index >= original.Admitted)
            return Result<bool>.Fail(ErrorCode.Conflict, "Only an originally admitted Plan scope can be verified.");
        if (entry.Plan is null || index < original.Delivered) return Result<bool>.Ok(true);
        var plan = Parse(entry.Plan);
        var captures = entry.Captures.Where((_, ordinal) => entry.Previews[ordinal].Length != 0).ToArray();
        if (plan.Connection != scope.Source.Connection || plan.Review is not { } binding
            || binding.Revision != original.Document.Id || binding.Store != scope.StoreInstance
            || binding.Actor != scope.Source.PrincipalId || binding.Capture != CaptureBinding(entry.Captures, entry.CaptureVersions)
            || captures.Length != plan.Operations.Length)
            return Result<bool>.Fail(ErrorCode.Conflict, "The retained Plan does not bind this exact scope and its reviewed captures.");

        IEnumerable<JournalEntry> Pending(StoreSnapshot state) => state.Journal.Where(row =>
            row.Digest == plan.Digest && (operation is null || row.Operation == operation)
            && row.Operation >= 0 && row.Operation < captures.Length && original.Units.Contains(captures[row.Operation].Id)
            && !original.DeliveredUnits.Contains(captures[row.Operation].Id)
            && (row.Status == OpStatus.Applied || row.CanRecheckAcknowledgedCreate && plan.Operations[row.Operation].Kind == OpKind.Create));

        var snapshot = await Snapshot(scope, ct).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<bool>.Fail(snapshot.Error);
        if (!Pending(snapshot.Value).Any()) return Result<bool>.Ok(true);
        try
        {
            using var runtime = await Runtime(scope.OriginRoot, configure, ct, executionOwner: true).ConfigureAwait(false);
            var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
            var opened = await factory.OpenScopedAsync(scope, scope.Source, ct).ConfigureAwait(false);
            if (!opened.IsSuccess) return Result<bool>.Fail(opened.Error);
            using var handle = opened.Value;
            snapshot = await handle.Workspace.SnapshotAsync(ct).ConfigureAwait(false);
            if (!snapshot.IsSuccess) return Result<bool>.Fail(snapshot.Error);
            using var registry = new SqliteSystemWorktreeRegistry(factory.ExecutionRegistryPath, TimeProvider.System);
            var refs = await registry.CoreAttemptReferencesAsync(scope.StoreInstance, ct).ConfigureAwait(false);
            if (!refs.IsSuccess) return Result<bool>.Fail(refs.Error);
            Error? error = null;
            foreach (var row in Pending(snapshot.Value))
            {
                var op = plan.Operations[row.Operation];
                var exact = refs.Value.Where(r => r.Digest == row.Digest && r.Operation == row.Operation).ToArray();
                if (op.Item != Parse(captures[row.Operation].IntentJson).Operations.Single().Item
                    || row.Attempt == Guid.Empty || exact.Length == 0 || exact.Any(r => r.Attempt != row.Attempt)
                    || !NativeCoreExecution.ReferencesMatch(snapshot.Value, exact))
                {
                    error ??= new(ErrorCode.Indeterminate, "Original scope and registry attempt evidence disagree.");
                    continue;
                }
                var verified = await handle.Workspace.VerifyPlanAcknowledgementAsync(plan, row.Receipts[0], ct).ConfigureAwait(false);
                if (!verified.IsSuccess) error ??= verified.Error;
                else if (verified.Value.Status != OpStatus.Verified)
                    error ??= verified.Value.Error ?? new(ErrorCode.Indeterminate, "The original acknowledgement is not yet Verified.");
            }
            return error is null ? Result<bool>.Ok(true) : Result<bool>.Fail(error);
        }
        catch (ConnectionBindingResolutionException)
        { return Result<bool>.Fail(ErrorCode.Conflict, "An active native operation protects the original Plan attempt."); }
    }

    internal async Task<Result<NativePlanOutcome>> AbandonUnadmittedAsync(CancellationToken ct = default)
    {
        var retained = await user.ReadPlanReviewAsync(Digest, ct).ConfigureAwait(false);
        if (!retained.IsSuccess) return Result<NativePlanOutcome>.Fail(retained.Error);
        var current = retained.Value;
        if (!current.Abandoned && current.Units.Count < current.Document.Scopes.Sum(s => s.Captures.Length))
        {
            var changed = await user.AdvancePlanReviewAsync(current, true, current.Delivered, current.Admitted, ct, abandon: true).ConfigureAwait(false);
            if (!changed.IsSuccess) return Result<NativePlanOutcome>.Fail(changed.Error);
        }
        return await ObserveDeliveryAsync(null, ct).ConfigureAwait(false);
    }

    public async Task<Result<NativePlanOutcome>> ObserveAsync(CancellationToken ct = default)
    {
        var retained = await user.ReadPlanReviewAsync(Digest, ct).ConfigureAwait(false);
        if (!retained.IsSuccess) return Result<NativePlanOutcome>.Fail(retained.Error);
        reference = retained.Value;
        Error? error = null;
        foreach (var scope in retained.Value.Document.Scopes.Skip(retained.Value.Delivered).Take(retained.Value.Admitted - retained.Value.Delivered))
        {
            var verified = await VerifyAcknowledgementsAsync(retained.Value, scope, ct).ConfigureAwait(false);
            if (!verified.IsSuccess) error ??= verified.Error;
        }
        return await ObserveDeliveryAsync(error, ct).ConfigureAwait(false);
    }

    internal async Task<Result<NativePlanOutcome>> ObserveDeliveryAsync(Error? reason, CancellationToken ct)
    {
        var retained = await user.ReadPlanReviewAsync(Digest, ct).ConfigureAwait(false);
        if (!retained.IsSuccess) return Result<NativePlanOutcome>.Fail(retained.Error);
        reference = retained.Value;
        return Result<NativePlanOutcome>.Ok(await Outcome(reason, ct).ConfigureAwait(false));
    }

    private async Task<Result<NativePlanOutcome>> Execute(bool deliveryOnly, CancellationToken ct)
    {
        try
        {
            using var benchLease = user.AcquireAuthoringLease(BenchId);
            var retained = await user.ReadPlanReviewAsync(Digest, ct).ConfigureAwait(false);
            if (retained.IsSuccess) reference = retained.Value;
            else if (retained.Error.Code != ErrorCode.NotFound || deliveryOnly || StagedVersion is null)
                return Result<NativePlanOutcome>.Fail(retained.Error);
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
                    if (!retained.IsSuccess)
                    {
                        var staged = await user.ReadStagedPlanVersionAsync(BenchId, ct).ConfigureAwait(false);
                        if (!staged.IsSuccess) return Result<NativePlanOutcome>.Fail(staged.Error);
                        if (staged.Value.Version != StagedVersion)
                            return Result<NativePlanOutcome>.Fail(ErrorCode.Conflict, "Staged Plan intent changed. Reload it before applying.");
                    }
                    var current = await ValidateWhole(library.Value, ct).ConfigureAwait(false);
                    if (!current.IsSuccess) return Result<NativePlanOutcome>.Fail(current.Error);
                    foreach (var scope in reference.Document.Scopes)
                    {
                        var valid = await ValidateScope(scope, ct).ConfigureAwait(false);
                        if (!valid.IsSuccess) return Result<NativePlanOutcome>.Fail(valid.Error);
                    }
                    var admitted = await user.RetainPlanReviewAsync(reference.Document, ct).ConfigureAwait(false);
                    if (!admitted.IsSuccess) return Result<NativePlanOutcome>.Fail(admitted.Error);
                    reference = admitted.Value;
                }
                for (var index = reference.Delivered; index < reference.Document.Scopes.Length; index++)
                {
                    if (deliveryOnly && index >= reference.Admitted)
                        return Result<NativePlanOutcome>.Ok(await Outcome(new(ErrorCode.Conflict,
                            "This scope was never admitted; its captures remain staged."), ct).ConfigureAwait(false));
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
                            snapshot.Value.RetainedItems.Single(draft => handle.Workspace.DraftFingerprint(draft) == capture.DraftFingerprint
                                && draft.Key == Parse(capture.IntentJson).Operations.Single().Item),
                            scope.Previews[ordinal].Length == 0 ? null : Parse(scope.Previews[ordinal]))).ToImmutableArray();
                        var plan = scope.Plan is null ? null : Parse(scope.Plan);
                        var admitted = await user.AdvancePlanReviewAsync(reference, true, reference.Delivered, index + 1, ct).ConfigureAwait(false);
                        if (!admitted.IsSuccess) return Result<NativePlanOutcome>.Ok(await Outcome(admitted.Error, ct).ConfigureAwait(false));
                        reference = reference with { Admitted = index + 1 };
                        async Task<Result<bool>> Admit(ItemKey key)
                        {
                            var capture = scope.Captures.Single(c => Parse(c.IntentJson).Operations.Single().Item == key);
                            var result = await user.AdmitPlanCaptureAsync(reference, index, capture.Id, ct).ConfigureAwait(false);
                            if (result.IsSuccess) reference = reference with { Units = reference.Units.Add(capture.Id) };
                            return result;
                        }
                        var saved = await handle.Workspace.SaveDraftSetAsync(previews, plan, ct, admitCapture: Admit).ConfigureAwait(false);
                        if (!saved.IsSuccess || !saved.Value.EffectsVerified || saved.Value.DraftRetained || saved.Value.Error is not null)
                            return Result<NativePlanOutcome>.Ok(await Outcome(saved.IsSuccess ? saved.Value.Error
                                ?? new(ErrorCode.Indeterminate, "Core effects or draft settlement remain unconfirmed.") : saved.Error, ct).ConfigureAwait(false));
                    }
                    var verified = deliveryOnly ? await VerifyAcknowledgementsAsync(reference, scope, ct).ConfigureAwait(false)
                        : Result<bool>.Ok(true);
                    var delivered = await Deliver(scope, handle.Workspace, factory, ct).ConfigureAwait(false);
                    var verificationError = verified.IsSuccess ? null : verified.Error;
                    if (!delivered.IsSuccess) return Result<NativePlanOutcome>.Ok(await Outcome(verificationError ?? delivered.Error, ct).ConfigureAwait(false));
                    if (!delivered.Value)
                        return Result<NativePlanOutcome>.Ok(await Outcome(verificationError ?? new(ErrorCode.Indeterminate,
                            "Verified original effects were delivered. Review only the remaining proven no-effect or untouched intent; uncertain effects remain protected."), ct).ConfigureAwait(false));
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
        if (scope.CaptureVersions.IsDefault || scope.CaptureVersions.Length != scope.Captures.Length ||
            scope.Captures.Where((capture, i) => !captures.Value.Any(current => current.SameAs(capture))
                || !snapshot.Value.RetainedItems.Any(draft => draft.Key == Parse(capture.IntentJson).Operations.Single().Item
                    && handle.Workspace.OwnerCapture(draft) == scope.CaptureVersions[i])).Any())
            return Result<bool>.Fail(ErrorCode.Conflict, "The reviewed Plan draft or capture changed.");
        if (scope.Plan is not null)
        {
            var plan = Parse(scope.Plan);
            if (plan.Review is not { } binding || binding.Revision != reference.Document.Id
                || binding.Store != scope.Scope.StoreInstance || binding.Actor != handle.Source.PrincipalId
                || binding.Capture != CaptureBinding(scope.Captures, scope.CaptureVersions))
                return Result<bool>.Fail(ErrorCode.Conflict, "The Plan does not bind this deliberate review and its exact source captures.");
            var checkedPlan = await handle.Workspace.CheckPlanAsync(plan, ct).ConfigureAwait(false);
            if (!checkedPlan.IsSuccess) return Result<bool>.Fail(checkedPlan.Error);
            if (!checkedPlan.Value.IsEmpty) return Result<bool>.Fail(checkedPlan.Value[0]);
        }
        return Result<bool>.Ok(true);
    }

    internal static string CaptureBinding(IEnumerable<NativeEditorCapture> captures, ImmutableArray<string> versions)
        => string.Join(';', captures.Select((capture, i) => $"{capture.Id:D}:{versions[i]}"));

    private async Task<Result<bool>> Deliver(NativePlanReviewScope scope, CoreWorkspace workspace, CoreWorkspaceFactory factory, CancellationToken ct)
    {
        var plan = scope.Plan is null ? null : Parse(scope.Plan);
        var captures = scope.Captures.Select((capture, ordinal) => (Capture: capture, Ordinal: ordinal,
            Key: Parse(capture.IntentJson).Operations.Single().Item)).Where(value => reference.Units.Contains(value.Capture.Id)).ToImmutableArray();
        var recovered = await workspace.RecoverPlanDraftsAsync(plan, captures.Select(value =>
            new CorePlanDeliveryCapture(new(value.Key, value.Capture.DraftFingerprint),
                scope.Previews[value.Ordinal].Length == 0)).ToImmutableArray(), ct).ConfigureAwait(false);
        if (!recovered.IsSuccess) return Result<bool>.Fail(recovered.Error);
        var evidence = recovered.Value;
        var deliveredCaptures = captures.Where(value => evidence.DeliveredKeys.Contains(value.Key))
            .Select(value => value.Capture).ToImmutableArray();
        var opened = await factory.OpenAsync(ct).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<bool>.Fail(opened.Error);
        using var origin = opened.Value;
        if (origin.Source != scope.Scope.Source)
            return Result<bool>.Fail(ErrorCode.Conflict, "The original delivery source changed; verified effects remain protected.");
        var pins = await NativeVerifiedDelivery.ReadbackAsync(evidence.Targets, origin.Workspace, ct).ConfigureAwait(false);
        if (!pins.IsSuccess) return Result<bool>.Fail(pins.Error);
        var bench = await NativeVerifiedDelivery.AddPinsAsync(user, reference.Document.UserInstance, BenchId, pins.Value, ct).ConfigureAwait(false);
        if (!bench.IsSuccess) return Result<bool>.Fail(bench.Error);
        var settledCaptures = await NativeVerifiedDelivery.SettleCapturesAsync(user, BenchId, bench.Value.Revision,
            deliveredCaptures, ct).ConfigureAwait(false);
        if (!settledCaptures.IsSuccess) return Result<bool>.Fail(settledCaptures.Error);
        return Result<bool>.Ok(evidence.AllEffectsVerified && captures.Length == scope.Captures.Length);
    }

    private async Task<NativePlanOutcome> Outcome(Error? error, CancellationToken ct)
    {
        var verified = 0;
        var verifiedOperations = 0;
        var deliveryPending = false;
        var units = new List<PlanCaptureUnit>();
        foreach (var scope in reference.Document.Scopes)
        {
            var index = reference.Document.Scopes.IndexOf(scope);
            var snapshot = await Snapshot(scope.Scope, ct).ConfigureAwait(false);
            var reviewed = scope.Plan is null ? null : Parse(scope.Plan);
            var ordinal = 0;
            for (var c = 0; c < scope.Captures.Length; c++)
            {
                int? operation = scope.Previews[c].Length == 0 ? null : ordinal++;
                var row = snapshot.IsSuccess && operation.HasValue ? snapshot.Value.Journal.SingleOrDefault(
                    j => j.Digest == reviewed?.Digest && j.Operation == operation) : null;
                var state = index < reference.Delivered || reference.DeliveredUnits.Contains(scope.Captures[c].Id) ? "Delivered" : !reference.Units.Contains(scope.Captures[c].Id)
                    ? reference.Abandoned ? "Abandoned" : "Unadmitted" : !snapshot.IsSuccess ? "Unavailable" : row?.Status.ToString() ?? "Admitted";
                units.Add(new(scope.Scope.Id, scope.Captures[c].Id, state, reviewed?.Digest, operation,
                    row is null || row.Attempt == Guid.Empty ? null : row.Attempt));
            }
            if (!snapshot.IsSuccess) { error ??= snapshot.Error; continue; }
            if (scope.Plan is null) continue;
            var plan = Parse(scope.Plan);
            var count = CoreWorkspace.VerifiedPlanOperations(snapshot.Value, plan).Length;
            if (count == plan.Operations.Length)
            {
                verified++;
                deliveryPending |= reference.Document.Scopes.IndexOf(scope) >= reference.Delivered;
            }
            verifiedOperations += count;
        }
        var complete = reference.Delivered == reference.Document.Scopes.Length
            && verified == reference.Document.Scopes.Count(scope => scope.Plan is not null) && error is null;
        return new(complete ? "Complete" : reference.Abandoned && reference.Delivered == reference.Admitted ? "AbandonedUnadmitted" : deliveryPending ? "VerifiedDeliveryPending"
            : verifiedOperations > 0 ? "PartialProtected" : "Protected", verified, reference.Delivered,
            reference.Document.Scopes.Length - reference.Delivered, verifiedOperations, Operations.Length - verifiedOperations, error) { Units = [.. units] };
    }

    private static async Task<Result<StoreSnapshot>> Snapshot(NativePlanScope scope, CancellationToken ct)
    {
        var store = await SqliteStore.OpenAsync(scope.DatabasePath, ct, scope.StoreInstance).ConfigureAwait(false);
        return store.IsSuccess ? await store.Value.ReadAsync(scope.Source.Connection, ct).ConfigureAwait(false)
            : Result<StoreSnapshot>.Fail(store.Error);
    }
    private static Plan Parse(string json) => UserBenchStore.ParseIntent(json);
    private static string Bytes(Plan plan) => Encoding.UTF8.GetString(Model.PlanBytes(plan));
    private static async Task<ServiceProvider> Runtime(string root, Action<IServiceCollection>? configure, CancellationToken ct,
        bool executionOwner = false)
    {
        if (!Path.IsPathFullyQualified(root) || Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) != root)
            throw new ArgumentException("An exact canonical native origin is required.", nameof(root));
        var paths = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), new TwigConfiguration(), root);
        var configuration = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
        var services = new ServiceCollection();
        configure?.Invoke(services);
        services.AddCoreWorkspaceServices(configuration, paths, executionOwner);
        return services.BuildServiceProvider();
    }
}

internal sealed partial class CoreWorkspace
{
    // Qualified readback preserves Applied on transient failures and never enters the dispatch path.
    internal Task<Result<Receipt>> VerifyPlanAcknowledgementAsync(Plan plan, Receipt acknowledgement, CancellationToken ct)
        => provider is IOriginalUpdateProvider original
            ? original.VerifyOriginalAsync(plan, acknowledgement, ct)
            : Task.FromResult(Result<Receipt>.Fail(ErrorCode.Unsupported, "This Plan provider cannot verify an original acknowledgement."));
}
