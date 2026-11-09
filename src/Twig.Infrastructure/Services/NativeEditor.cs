using System.Collections.Immutable;
using System.Text;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Model = Twig.Core.Core;

namespace Twig.Infrastructure.Services;

internal sealed record NativeEditorItem(WorkItem Observation, Draft? Draft, long StoreVersion,
    NativeEditorCapture? Capture, EditorEvaluation Evaluation);

/// <summary>Native-only authoring session. Cached guidance never grants publication authority.</summary>
internal sealed partial class NativeEditor(CoreWorkspaceFactory factory, UserBenchStore user, CoreWorkspaceHandle handle,
    NativeSourceStamp origin, UserBenchSnapshot library, Bench bench, NativeBenchPlan plan, NativePlanScope? scope,
    NativeEditorMetadata metadata) : IDisposable
{
    internal NativeEditorMetadata Metadata => metadata;
    internal CoreWorkspace Workspace => handle.Workspace;
    internal NativeBenchPlan Plan => plan;
    internal NativeSourceStamp Origin => origin;
    internal Guid BenchId => bench.Id;
    internal Guid ScopeId => scope?.Id ?? Guid.Empty;
    internal Guid StoreInstance => handle.Document.DatabaseInstance;
    public void Dispose() => handle.Dispose();

    internal static async Task<Result<NativeEditor>> OpenAsync(CoreWorkspaceFactory factory, UserBenchStore user,
        Guid benchId, NativeSourceStamp expectedSource, CancellationToken ct, bool prepareScope = true)
    {
        var opened = await factory.OpenAsync(ct).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<NativeEditor>.Fail(opened.Error);
        var handle = opened.Value;
        try
        {
            if (handle.Source != expectedSource)
                return Result<NativeEditor>.Fail(ErrorCode.Conflict, "The selected native editor source changed.");
            var admitted = await factory.AdmitSourceAsync(handle, ct).ConfigureAwait(false);
            if (!admitted.IsSuccess) return Result<NativeEditor>.Fail(admitted.Error);
            using var authoring = prepareScope ? null : user.AcquireAuthoringLease(benchId);
            var library = await user.ReadAsync(ct).ConfigureAwait(false);
            if (!library.IsSuccess) return Result<NativeEditor>.Fail(library.Error);
            var bench = library.Value.Benches.SingleOrDefault(value => value.Id == benchId);
            if (bench is null) return Result<NativeEditor>.Fail(ErrorCode.NotFound, "The selected Bench no longer exists.");
            var mode = await user.ReadPlanAsync(benchId, ct).ConfigureAwait(false);
            if (!mode.IsSuccess) return Result<NativeEditor>.Fail(mode.Error);
            NativePlanScope? scope = null;
            if (mode.Value.PlanMode)
            {
                scope = mode.Value.Scopes.SingleOrDefault(value => value.State != NativePlanScopeState.Superseded
                    && value.Source.Connection == expectedSource.Connection);
                if (scope is null)
                {
                    if (!prepareScope)
                        return Result<NativeEditor>.Fail(ErrorCode.Conflict, "This Plan has no usable scope; preparation requires an explicit editor opening.");
                    mode = await user.ReservePlanScopeAsync(library.Value.InstanceId, benchId, mode.Value.Revision,
                        factory.WorkspaceRoot, expectedSource, ct).ConfigureAwait(false);
                    if (!mode.IsSuccess) return Result<NativeEditor>.Fail(mode.Error);
                    scope = mode.Value.Scopes.Single(value => value.Source == expectedSource && value.State == NativePlanScopeState.Preparing);
                }
                if (scope.Source != expectedSource)
                    return Result<NativeEditor>.Fail(ErrorCode.Conflict, "This Plan belongs to another exact origin; reassociation is explicit.");
                if (scope.State == NativePlanScopeState.Preparing)
                {
                    if (!prepareScope)
                        return Result<NativeEditor>.Fail(ErrorCode.Conflict, "This Plan scope is preparing; completion requires an explicit editor opening.");
                    mode = await user.CompleteCoveredPlanScopeAsync(library.Value.InstanceId, benchId, mode.Value.Revision,
                        scope.Id, factory.ExecutionRegistryPath, new(expectedSource.StoreInstance, handle.Document.DatabasePath,
                            factory.WorkspaceRoot, expectedSource.Connection), ct).ConfigureAwait(false);
                    if (!mode.IsSuccess) return Result<NativeEditor>.Fail(mode.Error);
                    scope = mode.Value.Scopes.Single(value => value.Id == scope.Id);
                }
                var scoped = await factory.OpenScopedAsync(scope, expectedSource, ct).ConfigureAwait(false);
                if (!scoped.IsSuccess) return Result<NativeEditor>.Fail(scoped.Error);
                handle.Dispose();
                handle = scoped.Value;
            }
            var captured = await handle.Workspace.CaptureEditorMetadataAsync(ct).ConfigureAwait(false);
            if (!captured.IsSuccess) return Result<NativeEditor>.Fail(captured.Error);
            var editor = new NativeEditor(factory, user, handle, expectedSource, library.Value, bench, mode.Value, scope, captured.Value);
            handle = null!;
            return Result<NativeEditor>.Ok(editor);
        }
        catch (ConnectionBindingResolutionException ex) when (ex.FailureCode == ConnectionOperationGate.ActiveFailureCode)
        {
            return Result<NativeEditor>.Fail(ErrorCode.Conflict, "The Bench has an active native authoring operation.");
        }
        finally { handle?.Dispose(); }
    }

    internal async Task<Result<NativeEditorItem>> SelectAsync(ItemKey key, CancellationToken ct)
    {
        using var rootLease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true);
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeEditorItem>.Fail(valid.Error);
        var available = await LocalEndpointAvailableAsync(key, ct).ConfigureAwait(false);
        if (!available.IsSuccess) return Result<NativeEditorItem>.Fail(available.Error);
        var observed = await ReadAdmittedSelectionAsync(key, ct).ConfigureAwait(false);
        return observed.IsSuccess ? await Item(observed.Value, ct).ConfigureAwait(false)
            : Result<NativeEditorItem>.Fail(observed.Error);
    }

    private async Task<Result<WorkItem>> ReadAdmittedSelectionAsync(ItemKey key, CancellationToken ct)
    {
        if (!key.IsLocal)
            return await Workspace.ReadAsync(key, ct, metadata).ConfigureAwait(false);
        var stored = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!stored.IsSuccess) return Result<WorkItem>.Fail(stored.Error);
        var seed = stored.Value.Drafts.SingleOrDefault(draft => draft.Key == key);
        return seed is null ? Result<WorkItem>.Fail(plan.PlanMode ? ErrorCode.NotFound : ErrorCode.Unsupported,
            "The exact owned seed is unavailable in this native store/mode; other Plan or CLI debt was not adopted.")
            : Result<WorkItem>.Ok(new(key, seed.Type, FieldValues.Empty, null, true));
    }

    internal async Task<Result<NativeEditorItem>> CreateSeedAsync(string type, CancellationToken ct)
    {
        using var benchLease = user.AcquireAuthoringLease(bench.Id);
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeEditorItem>.Fail(valid.Error);
        if (plan.PlanMode && scope is null || !metadata.Schema.Types.Any(value => value.Reference == type && !value.Hidden))
            return Result<NativeEditorItem>.Fail(ErrorCode.InvalidInput, "Choose an available discovered type in this exact native source.");
        if (!plan.PlanMode && origin.Connection.Provider.Value != "ado")
            return Result<NativeEditorItem>.Fail(ErrorCode.Unsupported, "Normal native creation supports an explicit ADO source only.");
        var key = ItemKey.Local(Guid.CreateVersion7());
        if (scope is not null)
        {
            var reserved = await user.ReservePlanSeedAsync(library.InstanceId, bench.Id, plan.Revision, scope.Id, key.LocalId, ct).ConfigureAwait(false);
            if (!reserved.IsSuccess) return Result<NativeEditorItem>.Fail(reserved.Error);
            plan = reserved.Value;
        }
        var observed = new WorkItem(key, type, FieldValues.Empty, null, true);
        var item = await Item(observed, ct).ConfigureAwait(false);
        return item.IsSuccess ? await StageAsync(item.Value, FieldValues.Empty, type, [], ct).ConfigureAwait(false)
            : Result<NativeEditorItem>.Fail(item.Error);
    }

    private async Task<Result<NativeEditorItem>> Item(WorkItem observed, CancellationToken ct)
    {
        var stored = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!stored.IsSuccess) return Result<NativeEditorItem>.Fail(stored.Error);
        var captures = await user.ReadEditorCapturesAsync(ct).ConfigureAwait(false);
        if (!captures.IsSuccess) return Result<NativeEditorItem>.Fail(captures.Error);
        return Item(observed, stored.Value, captures.Value);
    }

    private Result<NativeEditorItem> Item(WorkItem observed, StoreSnapshot stored, ImmutableArray<NativeEditorCapture> captures)
    {
        if (captures.Any(value => LocalCaptureOwnsEndpoint(value, observed.Key)))
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "An original local relationship capture protects this endpoint; observe or continue its exact digest, without scalar adoption.");
        var matches = captures.Where(value => !value.Settled && !value.Cancelled && value.StoreInstance == handle.Document.DatabaseInstance
            && value.PlanMode == plan.PlanMode && (!plan.PlanMode || value.PlanId == plan.Id)
            && UserBenchStore.ParseIntent(value.IntentJson).Operations[0].Item == observed.Key).ToArray();
        if (matches.Length > 1)
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "Multiple protected native captures own this target; none was selected implicitly.");
        if (matches.SingleOrDefault() is { } retained
            && (retained.Origin != origin || !NativePathComparer.Instance.Equals(retained.Root, factory.WorkspaceRoot)))
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "Retained intent belongs to another native authority/origin; it was not adopted.");
        var draft = stored.Drafts.SingleOrDefault(value => value.Key == observed.Key);
        if (observed.Key.IsLocal && !plan.PlanMode && matches.SingleOrDefault() is { } owner && owner.BenchId != bench.Id)
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "The normal seed is owned by another exact Bench; its capture was not adopted.");
        var evaluation = Model.EvaluateEditorFields(metadata.Schema, draft?.Baseline ?? observed, draft?.Changes ?? FieldValues.Empty,
            Workspace.Capabilities.AtomicFieldBatches, draft?.Type);
        return evaluation.IsSuccess ? Result<NativeEditorItem>.Ok(new(observed, draft, stored.Version, matches.SingleOrDefault(), evaluation.Value))
            : Result<NativeEditorItem>.Fail(evaluation.Error);
    }

    internal async Task<Result<NativeEditorItem>> StageAsync(NativeEditorItem item, FieldValues fields, string? type,
        ImmutableArray<CreationLink>? relationships, CancellationToken ct, ImmutableArray<NativeEditorEndpoint>? expectedEndpoints = null)
    {
        using var benchLease = user.AcquireAuthoringLease(bench.Id);
        using var rootLease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true);
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeEditorItem>.Fail(valid.Error);
        var current = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<NativeEditorItem>.Fail(current.Error);
        if (!plan.PlanMode && item.Observation.Key.IsLocal)
        {
            if (item.Draft is not null && item.Capture is null)
                return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "Unowned CLI seed debt cannot be adopted by native authoring.");
            if (item.Capture is { } normal && (normal.BenchId != bench.Id || normal.PlanMode
                || normal.SeedProposal is not null || normal.Origin != origin))
                return Result<NativeEditorItem>.Fail(ErrorCode.Indeterminate, "The original normal seed reference is protected; inspect or continue its delivery without restaging.");
            if (current.Value.Journal.Any(row => row.Status != OpStatus.Planned
                && current.Value.Proposals.Single(value => value.Digest == row.Digest).Operations[row.Operation].Item == item.Observation.Key))
                return Result<NativeEditorItem>.Fail(ErrorCode.Indeterminate, "An original creation attempt protects this UUID; no replacement intent was staged.");
            if (type is not null && type != item.Observation.Type || relationships is { IsEmpty: false }
                || item.Draft is { } seed && (seed.Parent is not null || seed.ParentRelation is not null
                    || seed.Resource is not null || !seed.Links.IsEmpty))
                return Result<NativeEditorItem>.Fail(ErrorCode.Unsupported, "Normal seeds support one explicit type and scalar fields only, without parent, links or resources.");
        }
        if (plan.PlanMode && item.Observation.Key.IsLocal)
        {
            var owner = await user.ReadPlanSeedOwnerAsync(item.Observation.Key.LocalId, ct).ConfigureAwait(false);
            if (!owner.IsSuccess) return Result<NativeEditorItem>.Fail(owner.Error);
            if (owner.Value.PlanId != plan.Id || owner.Value.ScopeId != scope?.Id || owner.Value.StoreInstance != handle.Document.DatabaseInstance)
                return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "The permanent seed belongs to another exact native scope.");
        }
        if (current.Value.Version != item.StoreVersion || Workspace.DraftFingerprint(item.Draft)
            != Workspace.DraftFingerprint(current.Value.Drafts.SingleOrDefault(value => value.Key == item.Observation.Key)))
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "The opened native/draft version changed; no intent was overwritten.");
        if (item.Capture is { Ready: true } owned && owned.DraftFingerprint != Workspace.DraftFingerprint(item.Draft))
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "Protected captured debt differs from Core; explicit recovery/review is required before restaging.");
        var priorIntent = item.Capture is null ? null : UserBenchStore.ParseIntent(item.Capture.IntentJson);
        var old = priorIntent?.Operations.Single();
        if (item.Capture is { Ready: false })
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "An incomplete durable capture needs explicit native recovery; all intent remains protected.");
        if (priorIntent is not null && priorIntent.SchemaFingerprint != metadata.Schema.Fingerprint)
            return Result<NativeEditorItem>.Fail(ErrorCode.SchemaChanged, "Retained intent has another captured schema; no metadata was silently replaced.");
        var links = relationships ?? (item.Observation.Key.IsLocal ? item.Draft?.Links ?? [] : old?.Links ?? []);
        if (links.Length > 128 || fields.Count > 256)
            return Result<NativeEditorItem>.Fail(ErrorCode.InvalidInput, "Native editor field/relationship bounds exceeded.");
        if (!item.Observation.Key.IsLocal && old is { Links.IsEmpty: false } && old.ExpectedRevision != item.Observation.Revision)
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "Retained relationship intent has an older baseline; it was not silently rebased.");
        var desired = new Draft(item.Observation.Key, type ?? item.Draft?.Type ?? item.Observation.Type,
            item.Observation.Key.IsLocal ? null : item.Draft?.Baseline ?? item.Observation,
            (item.Draft?.Changes ?? FieldValues.Empty).Overlay(fields), item.Draft?.Parent, item.Draft?.ParentRelation,
            item.Draft?.Resource, item.Observation.Key.IsLocal ? links : []);
        var endpoints = ImmutableArray.CreateBuilder<NativeEditorEndpoint>();
        foreach (var endpoint in links.Select(link => link.Other).Distinct())
        {
            if (endpoint.IsLocal)
            {
                var draft = current.Value.Drafts.SingleOrDefault(value => value.Key == endpoint);
                if (draft is null) return Result<NativeEditorItem>.Fail(ErrorCode.NotFound, "An exact relationship seed endpoint is missing.");
                if (plan.PlanMode)
                {
                    var owner = await user.ReadPlanSeedOwnerAsync(endpoint.LocalId, ct).ConfigureAwait(false);
                    if (!owner.IsSuccess) return Result<NativeEditorItem>.Fail(owner.Error);
                    if (owner.Value.PlanId != plan.Id || owner.Value.ScopeId != scope?.Id || owner.Value.StoreInstance != handle.Document.DatabaseInstance)
                        return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "A relationship seed endpoint is owned by another exact native scope.");
                }
                endpoints.Add(new(CleanCoreAdoAccess.ItemText(endpoint), null, Workspace.DraftFingerprint(draft)));
            }
            else
            {
                var read = await Workspace.ReadAsync(endpoint, ct, metadata).ConfigureAwait(false);
                if (!read.IsSuccess) return Result<NativeEditorItem>.Fail(read.Error);
                var endpointSnapshot = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
                if (!endpointSnapshot.IsSuccess) return Result<NativeEditorItem>.Fail(endpointSnapshot.Error);
                endpoints.Add(new(CleanCoreAdoAccess.ItemText(endpoint), read.Value.Revision?.Token,
                    Workspace.DraftFingerprint(endpointSnapshot.Value.Drafts.SingleOrDefault(draft => draft.Key == endpoint))));
            }
        }
        if (relationships is null && item.Capture is { } previous && !previous.Endpoints.SequenceEqual(endpoints))
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "A captured relationship endpoint changed; intent was not silently rebound.");
        if (expectedEndpoints is { } expected && (expected.IsDefault || !expected.SequenceEqual(endpoints)))
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "The displayed relationship endpoint revision or draft changed; the original capture and draft were not replaced.");
        var beforeStage = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!beforeStage.IsSuccess) return Result<NativeEditorItem>.Fail(beforeStage.Error);
        if (expectedEndpoints is { } witnessed && links.Select(link => link.Other).Distinct().Any(endpoint =>
            !witnessed.Any(proof => proof.Key == CleanCoreAdoAccess.ItemText(endpoint)
                && proof.Revision == beforeStage.Value.Items.SingleOrDefault(value => value.Key == endpoint)?.Revision?.Token
                && proof.DraftFingerprint == Workspace.DraftFingerprint(beforeStage.Value.Drafts.SingleOrDefault(draft => draft.Key == endpoint)))))
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "The displayed endpoint evidence changed before capture reservation; no intent was overwritten.");
        if (Workspace.DraftFingerprint(beforeStage.Value.Drafts.SingleOrDefault(value => value.Key == desired.Key))
            != Workspace.DraftFingerprint(item.Draft))
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "Draft intent changed while endpoint evidence was captured.");
        var op = new Op("intent", desired.Key.IsLocal ? OpKind.Create : OpKind.Batch, desired.Key, item.Observation.Revision,
            desired.Changes, desired.Type, desired.Parent, desired.ParentRelation, resource: desired.Resource, links: links,
            concurrency: desired.Key.IsLocal ? ConcurrencyMode.ObservedNonAtomic : Workspace.Concurrency);
        var intent = Encoding.UTF8.GetString(Model.ProposalBytes(new(Workspace.Connection, metadata.Schema.Fingerprint, beforeStage.Value.Version, [op])));
        var capture = new NativeEditorCapture(item.Capture?.Id ?? Guid.NewGuid(), (item.Capture?.Revision ?? 0) + 1,
            library.InstanceId, bench.Id, bench.Revision, plan.Id, plan.Revision, plan.PlanMode, scope?.Id ?? Guid.Empty,
            origin, factory.WorkspaceRoot, handle.Document.DatabaseInstance, beforeStage.Value.Version, intent,
            Workspace.DraftFingerprint(desired), false) { Endpoints = endpoints.ToImmutable(),
                ExecutionPath = handle.Document.DatabasePath, BeforeFingerprint = Workspace.DraftFingerprint(item.Draft) };
        var retained = await user.SaveEditorCaptureAsync(capture, capture.Revision - 1, ct).ConfigureAwait(false);
        if (!retained.IsSuccess) return Result<NativeEditorItem>.Fail(retained.Error);
        var admission = await user.AdmitCaptureStageAsync(capture, null, ct).ConfigureAwait(false);
        if (!admission.IsSuccess) return Result<NativeEditorItem>.Fail(admission.Error);
        var staged = desired.Key.IsLocal
            ? await Workspace.StageSeedAsync(desired.Type, fields, ct, desired.Key.LocalId,
                desired.Parent, desired.ParentRelation, desired.Resource, links).ConfigureAwait(false)
            : await Workspace.StageEditAsync(desired.Key, fields, ct, type, item.Observation, beforeStage.Value.Version).ConfigureAwait(false);
        if (!staged.IsSuccess) return Result<NativeEditorItem>.Fail(staged.Error);
        var after = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!after.IsSuccess) return Result<NativeEditorItem>.Fail(after.Error);
        var observedStage = await user.AdmitCaptureStageAsync(capture, after.Value.Version, ct).ConfigureAwait(false);
        if (!observedStage.IsSuccess) return Result<NativeEditorItem>.Fail(observedStage.Error);
        var ready = await user.SaveEditorCaptureAsync(capture with { Revision = capture.Revision + 1, Ready = true,
            StoreVersion = after.Value.Version }, capture.Revision, ct).ConfigureAwait(false);
        return ready.IsSuccess ? await Item(item.Observation, ct).ConfigureAwait(false) : Result<NativeEditorItem>.Fail(ready.Error);
    }

    internal async Task<Result<CoreExecution>> SaveAsync(NativeEditorItem item, CancellationToken ct)
    {
        using var benchLease = user.AcquireAuthoringLease(bench.Id);
        using var rootLease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true);
        if (plan.PlanMode)
            return Result<CoreExecution>.Fail(ErrorCode.Conflict, "Plan stages locally; publish only its exact whole-Bench review.");
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<CoreExecution>.Fail(valid.Error);
        var capture = item.Capture;
        var stored = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!stored.IsSuccess) return Result<CoreExecution>.Fail(stored.Error);
        var captures = await user.ReadEditorCapturesAsync(ct).ConfigureAwait(false);
        if (!captures.IsSuccess) return Result<CoreExecution>.Fail(captures.Error);
        if (capture is not { Ready: true, Settled: false } || !captures.Value.Any(value => value.SameAs(capture))
            || capture.Origin != origin || stored.Value.Version != item.StoreVersion
            || capture.PlanMode || !NativePathComparer.Instance.Equals(capture.Root, factory.WorkspaceRoot)
            || Workspace.DraftFingerprint(stored.Value.Drafts.SingleOrDefault(value => value.Key == item.Observation.Key)) != capture.DraftFingerprint)
            return Result<CoreExecution>.Fail(ErrorCode.Conflict, "The exact normal capture, source or native/draft revision changed.");
        if (capture.UserInstance != library.InstanceId || capture.BenchId != bench.Id || capture.BenchRevision != bench.Revision
            || capture.PlanId != plan.Id || capture.PlanRevision != plan.Revision)
        {
            var rebound = await user.SaveEditorCaptureAsync(capture with { Revision = capture.Revision + 1,
                UserInstance = library.InstanceId, BenchId = bench.Id, BenchRevision = bench.Revision,
                PlanId = plan.Id, PlanRevision = plan.Revision }, capture.Revision, ct).ConfigureAwait(false);
            if (!rebound.IsSuccess) return Result<CoreExecution>.Fail(rebound.Error);
            capture = rebound.Value;
        }
        if (capture.SeedProposal is not null)
            return Result<CoreExecution>.Fail(ErrorCode.Indeterminate, "The original seed execution is already retained; continue delivery only.");
        var intent = UserBenchStore.ParseIntent(capture.IntentJson);
        var schema = await Workspace.SchemaAsync(ct).ConfigureAwait(false);
        if (!schema.IsSuccess) return Result<CoreExecution>.Fail(schema.Error);
        if (schema.Value.Fingerprint != intent.SchemaFingerprint)
            return Result<CoreExecution>.Fail(ErrorCode.SchemaChanged, "The native editor schema changed; reopen without losing captured intent.");
        var op = intent.Operations.Single();
        var preview = await Workspace.PreviewEditorDraftAsync(op.Item, op.Item.IsLocal ? [] : op.Links, op.ExpectedRevision, ct,
            capture.Endpoints).ConfigureAwait(false);
        if (!preview.IsSuccess) return Result<CoreExecution>.Fail(preview.Error);
        if (op.Item.IsLocal)
        {
            if (preview.Value.Proposal is null)
                return Result<CoreExecution>.Fail(ErrorCode.Conflict, "Native seed creation requires an exact Core proposal.");
            var retained = await user.SaveEditorCaptureAsync(capture with { Revision = capture.Revision + 1,
                SeedProposal = Encoding.UTF8.GetString(Model.ProposalBytes(preview.Value.Proposal)) }, capture.Revision, ct).ConfigureAwait(false);
            if (!retained.IsSuccess) return Result<CoreExecution>.Fail(retained.Error);
            capture = retained.Value;
        }
        var result = await Workspace.SaveDraftAsync(preview.Value, preview.Value.Proposal?.Digest, ct).ConfigureAwait(false);
        if (op.Item.IsLocal)
        {
            var delivered = await ContinueSeedDeliveryAsync(capture.Id, preview.Value.Proposal!.Digest, ct).ConfigureAwait(false);
            var execution = result.IsSuccess ? result.Value : new CoreExecution([], result.Error);
            return delivered.IsSuccess ? Result<CoreExecution>.Ok(execution with
                { DraftRetained = delivered.Value.Status != "Complete", Error = delivered.Value.Error ?? execution.Error })
                : Result<CoreExecution>.Ok(execution with { DraftRetained = true, Error = delivered.Error });
        }
        if (!result.IsSuccess || !result.Value.EffectsVerified || result.Value.DraftRetained) return result;
        var settled = await user.SaveEditorCaptureAsync(capture with { Revision = capture.Revision + 1, Settled = true },
            capture.Revision, ct).ConfigureAwait(false);
        return settled.IsSuccess ? result : Result<CoreExecution>.Ok(result.Value with { DraftRetained = true, Error = settled.Error });
    }

    internal async Task<Result<NativeEditorItem>> RecoverCaptureAsync(NativeEditorItem item, CancellationToken ct)
    {
        using var benchLease = user.AcquireAuthoringLease(bench.Id);
        using var rootLease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true);
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeEditorItem>.Fail(valid.Error);
        var capture = item.Capture;
        var stored = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!stored.IsSuccess) return Result<NativeEditorItem>.Fail(stored.Error);
        if (capture is not { Ready: false, Settled: false } || capture.Origin != origin
            || stored.Value.Version <= capture.StoreVersion
            || Workspace.DraftFingerprint(stored.Value.Drafts.SingleOrDefault(value => value.Key == item.Observation.Key)) != capture.DraftFingerprint)
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "No exact durable native draft proves this interrupted capture; every artifact remains protected.");
        var witnessed = await user.AdmitCaptureStageAsync(capture, stored.Value.Version, ct).ConfigureAwait(false);
        if (!witnessed.IsSuccess) return Result<NativeEditorItem>.Fail(witnessed.Error);
        var ready = await user.SaveEditorCaptureAsync(capture with { Revision = capture.Revision + 1,
            Ready = true, StoreVersion = stored.Value.Version }, capture.Revision, ct).ConfigureAwait(false);
        return ready.IsSuccess ? await Item(item.Observation, ct).ConfigureAwait(false) : Result<NativeEditorItem>.Fail(ready.Error);
    }

    internal async Task<Result<NativeEditorItem>> ResolveInterruptedAsync(NativeEditorItem item, bool cancel, CancellationToken ct)
    {
        using var benchLease = user.AcquireAuthoringLease(bench.Id);
        using var rootLease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true);
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeEditorItem>.Fail(valid.Error);
        var pending = item.Capture;
        var stored = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!stored.IsSuccess) return Result<NativeEditorItem>.Fail(stored.Error);
        if (pending is not { Ready: false, Settled: false, Cancelled: false } || pending.Origin != origin
            || stored.Value.Version < pending.StoreVersion
            || Workspace.DraftFingerprint(stored.Value.Drafts.SingleOrDefault(value => value.Key == item.Observation.Key)) != pending.BeforeFingerprint)
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "The exact before-state is not present; interrupted intent remains protected.");
        if (cancel)
        {
            var cancelled = await user.CancelCaptureAsync(pending, ct).ConfigureAwait(false);
            return cancelled.IsSuccess ? await Item(item.Observation, ct).ConfigureAwait(false) : Result<NativeEditorItem>.Fail(cancelled.Error);
        }
        var intent = UserBenchStore.ParseIntent(pending.IntentJson).Operations.Single();
        var candidate = new Draft(intent.Item, intent.Type ?? item.Observation.Type,
            intent.Item.IsLocal ? null : item.Draft?.Baseline ?? item.Observation, intent.Fields,
            intent.Other, intent.Relation, intent.Resource, intent.Item.IsLocal ? intent.Links : []);
        if (Workspace.DraftFingerprint(candidate) != pending.DraftFingerprint)
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "Fresh baseline differs from exact retained intent; review is required.");
        var admitted = await user.AdmitCaptureStageAsync(pending, null, ct).ConfigureAwait(false);
        if (!admitted.IsSuccess) return Result<NativeEditorItem>.Fail(admitted.Error);
        var stage = intent.Item.IsLocal
            ? await Workspace.StageSeedAsync(candidate.Type, candidate.Changes, ct, intent.Item.LocalId,
                candidate.Parent, candidate.ParentRelation, candidate.Resource, candidate.Links).ConfigureAwait(false)
            : await Workspace.StageEditAsync(intent.Item, candidate.Changes, ct, candidate.Type, item.Observation,
                stored.Value.Version).ConfigureAwait(false);
        return stage.IsSuccess ? await RecoverCaptureAsync(item, ct).ConfigureAwait(false) : Result<NativeEditorItem>.Fail(stage.Error);
    }

    internal async Task<Result<bool>> ValidateAsync(CancellationToken ct)
    {
        var now = await user.ReadAsync(ct).ConfigureAwait(false);
        if (!now.IsSuccess) return Result<bool>.Fail(now.Error);
        var mode = await user.ReadPlanAsync(bench.Id, ct).ConfigureAwait(false);
        if (!mode.IsSuccess) return Result<bool>.Fail(mode.Error);
        if (now.Value.InstanceId != library.InstanceId || !now.Value.Benches.Any(value => value.Id == bench.Id && value.Revision == bench.Revision)
            || mode.Value.Id != plan.Id || mode.Value.Revision != plan.Revision || mode.Value.PlanMode != plan.PlanMode
            || scope is not null && !mode.Value.Scopes.Contains(scope))
            return Result<bool>.Fail(ErrorCode.Conflict, "The opened Bench/mode/scope handle is stale; retained intent was preserved.");
        var opened = await factory.OpenAsync(ct).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<bool>.Fail(opened.Error);
        using var current = opened.Value;
        if (current.Source != origin)
            return Result<bool>.Fail(ErrorCode.Conflict, "The native actor, origin store or authority changed.");
        return await factory.AdmitSourceAsync(current, ct).ConfigureAwait(false);
    }
}
