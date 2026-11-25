using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Step = Twig.Store.Sqlite.WorkspacePublication.Step;

namespace Twig.Server;

internal sealed record PlanPublication(string Digest, Guid User, Guid Bench, Guid Plan, Guid Scope, Guid Origin,
    Guid Capture, string DefinitionsHash, Step Step, string? Pin);
[JsonSerializable(typeof(PlanPublication))]
internal sealed partial class DisplayJson;

internal sealed partial class ReleasedBenchStore
{
    internal Dictionary<string, PlanPublication> PendingPlans()
    {
        using var db = Open(read);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT step,digest,manifest FROM plan_publication WHERE state='Pending'";
        using var rows = cmd.ExecuteReader();
        var result = new Dictionary<string, PlanPublication>();
        while (rows.Read())
        {
            var plan = JsonSerializer.Deserialize(rows.GetString(2), DisplayJson.Default.PlanPublication);
            if (plan is null || rows.GetGuid(0) != plan.Step.Id || rows.GetString(1) != plan.Digest)
                throw new InvalidDataException("The original delivery manifest is inconsistent.");
            result.Add(plan.Digest, plan);
        }
        return result;
    }

    internal void PlanState(PlanPublication p, bool? applied)
    {
        using var db = OpenWrite();
        using var cmd = db.CreateCommand();
        cmd.CommandText = applied is null
            ? "INSERT INTO plan_publication VALUES($step,$digest,$manifest,'Pending')"
            : "UPDATE plan_publication SET state=$state WHERE step=$step AND digest=$digest AND manifest=$manifest AND state='Pending'";
        cmd.Parameters.AddWithValue("$step", p.Step.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$digest", p.Digest);
        cmd.Parameters.AddWithValue("$manifest", JsonSerializer.Serialize(p, DisplayJson.Default.PlanPublication));
        if (applied.HasValue) cmd.Parameters.AddWithValue("$state", applied.Value ? "Applied" : "NoEffect");
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidDataException("The pending Plan manifest changed.");
    }
}

internal sealed partial class ReleasedBenchDisplay
{
    private readonly Dictionary<string, PlanPublication> planPending;

    private void BeginPlan(PlanPublication plan)
    {
        lock (publication)
        {
            store.PlanState(plan, null);
            lock (release)
            {
                planPending.Add(plan.Digest, plan);
                foreach (var scope in partitionRequests.Keys.Where(p => p.Store == plan.User && p.Bench == plan.Bench).ToArray())
                    Withhold(scope);
            }
        }
    }

    private void EndPlan(PlanPublication plan, bool applied)
    {
        lock (publication)
        {
            store.PlanState(plan, applied);
            lock (release) planPending.Remove(plan.Digest);
        }
    }

    private sealed class PlanWriter(ReleasedBenchDisplay display, string path, Guid instance, string connection,
        Func<Step,PlanPublication> manifest) : WorkspacePublication
    {
        internal PlanPublication? Group { get; private set; }
        internal override async Task Begin(string database, Step step, HashSet<ItemKey>? footprint, CancellationToken ct)
        {
            if (Group is not null || !NativePathComparer.Instance.Equals(database, path)
                || step.Instance != instance || step.Connection != connection)
                throw new InvalidDataException("Unexpected domain write in a converged Plan delivery.");
            display.BeginPlan(Group = manifest(step));
            if (display.StepBoundary is { } cut) await cut("plan-manifest", step.After);
        }
        internal override Task Committed(Step step) =>
            display.StepBoundary?.Invoke("plan-scope", step.After) ?? Task.CompletedTask;
    }

    private static void CheckPlanPath(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Plan settlement cannot traverse redirected storage.");
    }

    internal async Task RecoverPlans(string root, UserBenchStore definitions, CancellationToken ct)
    {
        foreach (var p in planPending.Values.ToArray())
        {
            var retained = await definitions.ReadPlanReviewAsync(p.Digest, ct);
            if (!retained.IsSuccess || retained.Value.Document is not { } doc
                || doc.Scopes.SingleOrDefault(e => e.Scope.Id == p.Scope) is not { } entry
                || doc.UserInstance != p.User || doc.BenchId != p.Bench || doc.PlanId != p.Plan
                || !entry.Captures.Any(c => c.Id == p.Capture) || !retained.Value.Units.Contains(p.Capture)
                || entry.Scope.Id != p.Scope || entry.Scope.StoreInstance != p.Step.Instance
                || entry.Scope.Source.StoreInstance != p.Origin || !NativePathComparer.Instance.Equals(root, entry.Scope.OriginRoot))
                throw new InvalidDataException("Cannot reconcile the original Plan stores.");
            var path = definitions.PlanDatabase(p.Plan, p.Scope);
            CheckPlanPath(path);
            var originPath = Path.Combine(root, ".twig", "core.db");
            CheckPlanPath(originPath);
            var origin = await SqliteStore.OpenAsync(originPath, ct, p.Origin);
            if (!origin.IsSuccess || !(await origin.Value.CheckWorkspaceAsync(entry.Scope.Source.Connection, ct)).IsSuccess)
                throw new InvalidDataException("The original delivery workspace was replaced.");
            var proof = ReleasedBenchStore.Proof(path, p.Step);
            var shared = await definitions.DeliveryApplied(p.User, p.Step.Id, p.Digest, p.DefinitionsHash, ct);
            if (proof is null || shared && proof != true) throw new InvalidDataException("Contradictory Plan step evidence.");
            if (shared || proof == false) EndPlan(p, shared);
            // Scope-only: keep this Bench withheld. Only the matching original can continue local delivery.
        }
    }

    public async Task<Result<NativePlanOutcome>> ContinuePlanAsync(string root, NativeSourceStamp origin,
        UserBenchStore definitions, NativePlanReview review, CancellationToken ct)
    {
        var retained = await definitions.ReadPlanReviewAsync(review.Digest, ct);
        if (!retained.IsSuccess) return Result<NativePlanOutcome>.Fail(retained.Error);
        var original = retained.Value;
        PlanPublication? group;
        lock (release) planPending.TryGetValue(review.Digest, out group);
        var doc = original.Document;
        var units = doc.Scopes.SelectMany((entry, index) => entry.Captures.Select((capture, ordinal) =>
            (entry, index, capture, ordinal))).Where(unit => original.Units.Contains(unit.capture.Id)
                && (group?.Capture == unit.capture.Id || unit.index >= original.Delivered && !original.DeliveredUnits.Contains(unit.capture.Id)))
            .OrderByDescending(unit => unit.capture.Id == group?.Capture);
        Error? protectedUnit = null;
        foreach (var (entry, index, capture, ordinal) in units)
        {
            if (entry.Scope.Source != origin || !NativePathComparer.Instance.Equals(root, entry.Scope.OriginRoot))
                return Result<NativePlanOutcome>.Fail(ErrorCode.Conflict, "The selected owner is not the reviewed Plan origin.");
            var result = await DeliverUnit(root, origin, definitions, review, doc, entry, index, capture, ordinal, ct);
            if (!result.IsSuccess)
            {
                lock (release) if (planPending.ContainsKey(review.Digest)) return Result<NativePlanOutcome>.Fail(result.Error);
                protectedUnit ??= result.Error;
            }
        }
        if (protectedUnit is null && original.Units.Count < doc.Scopes.Sum(s => s.Captures.Length))
            protectedUnit = new(ErrorCode.Conflict, "Unadmitted captures are inspect-only; continuation does not admit or dispatch them.");
        return await review.ObserveDeliveryAsync(protectedUnit, ct);
    }

    private async Task<Result<bool>> DeliverUnit(string root, NativeSourceStamp origin, UserBenchStore definitions,
        NativePlanReview review, NativePlanReviewDocument doc, NativePlanReviewScope entry, int index,
        NativeEditorCapture capture, int ordinal, CancellationToken ct)
    {
        static Result<bool> Refuse(string reason) => Result<bool>.Fail(ErrorCode.Conflict, reason);
        PlanPublication? group;
        lock (release) planPending.TryGetValue(review.Digest, out group);
        var scope = entry.Scope;
        if (group is not null && group.Capture != capture.Id || !capture.Ready || capture.Settled || capture.Cancelled
            || scope.State != NativePlanScopeState.Ready || capture.Origin != origin
            || capture.StoreInstance != scope.StoreInstance || capture.ScopeId != scope.Id || capture.UserInstance != doc.UserInstance
            || capture.PlanId != doc.PlanId || capture.BenchId != doc.BenchId
            || !NativePathComparer.Instance.Equals(scope.DatabasePath, definitions.PlanDatabase(doc.PlanId, scope.Id)))
            return Refuse("The original capture is not owned by this exact scope.");
        if (UserBenchStore.ParseIntent(capture.IntentJson).Operations is not [{ Kind: OpKind.Batch or OpKind.Create, Item: var key }])
            return Refuse("No eligible original scalar or seed capture.");
        CheckPlanPath(scope.DatabasePath);
        var opened = await SqliteStore.OpenAsync(scope.DatabasePath, ct, scope.StoreInstance);
        if (!opened.IsSuccess) return Result<bool>.Fail(opened.Error);
        var snapshot = await opened.Value.ReadAsync(origin.Connection, ct);
        if (!snapshot.IsSuccess) return Result<bool>.Fail(snapshot.Error);
        var state = snapshot.Value;
        ItemKey? pin = null;
        if (group is null)
        {
            var draft = state.RetainedItems.SingleOrDefault(d => d.Key == key);
            var fingerprint = CoreWorkspace.Fingerprint(origin.Connection, draft);
            var version = draft is LocalEdits { CaptureRevision: { } revision } ? $"{revision:D}:{fingerprint}" : fingerprint;
            if (draft is not null && (fingerprint != capture.DraftFingerprint || version != entry.CaptureVersions[ordinal]))
                return Refuse("The reviewed draft changed; newer intent is protected.");
            if (entry.Previews[ordinal].Length == 0)
            {
                if (draft is not LocalEdits edit) return Refuse("No original converged draft is available.");
                var observed = await review.ObserveConvergenceAsync(scope, edit, version, ct);
                if (!observed.IsSuccess) return observed;
            }
            else
            {
                var plan = entry.Plan is null ? null : UserBenchStore.ParseIntent(entry.Plan);
                var operation = entry.Previews.Take(ordinal).Count(p => p.Length != 0);
                if (plan is null || plan.Connection != origin.Connection || plan.Review is not { } binding
                    || binding.Revision != doc.Id || binding.Store != scope.StoreInstance || binding.Actor != origin.PrincipalId
                    || binding.Capture != NativePlanReview.CaptureBinding(entry.Captures, entry.CaptureVersions)
                    || operation >= plan.Operations.Length || plan.Operations[operation].Item != key
                    || !state.Plans.Any(p => p.Digest == plan.Digest)
                    || state.Journal.SingleOrDefault(j => j.Digest == plan.Digest && j.Operation == operation) is not { Status: OpStatus.Verified } row)
                    return Refuse("No original Verified receipt for this admitted operation; uncertain or failed work is inspect-only.");
                if (key.IsLocal)
                {
                    var mapped = state.Mappings.SingleOrDefault(m => m.Local == key && m.PlanDigest == plan.Digest && m.Operation == operation);
                    if (mapped is null || !mapped.Remote.BelongsTo(origin.Connection) || row.Receipts[0].PublishedKey != mapped.Remote)
                        return Refuse("The verified create lacks its exact original published mapping.");
                    pin = mapped.Remote;
                }
                var readback = await review.ObserveVerifiedTargetAsync(scope, state, row, pin ?? key, ct);
                if (!readback.IsSuccess) return readback;
            }
        }
        else if (group.Pin is not null) pin = ItemKey.Remote(origin.Connection.Provider, origin.Connection.ResourceScope, group.Pin);
        using var benchLease = definitions.AcquireAuthoringLease(doc.BenchId);
        using var local = ConnectionOperationGate.Acquire(root, exclusive: true);
        lock (release)
            if (group != planPending.GetValueOrDefault(review.Digest))
                return Refuse("Original delivery advanced during observation. Inspect its existing result.");
        var current = await definitions.ReadPlanReviewAsync(review.Digest, ct);
        if (!current.IsSuccess || !current.Value.Units.Contains(capture.Id))
            return Refuse("The original admission changed before settlement.");
        if (group is not null && group.User == doc.UserInstance && group.Scope == scope.Id && group.Origin == origin.StoreInstance
            && ReleasedBenchStore.Proof(scope.DatabasePath, group.Step) == true
            && await definitions.DeliveryApplied(doc.UserInstance, group.Step.Id, review.Digest, group.DefinitionsHash, ct))
        {
            EndPlan(group, true);
            await ProjectWorkspace(root, origin, definitions, [doc.BenchId]);
            return Result<bool>.Ok(true);
        }
        if (group is null && (current.Value.Delivered > index || current.Value.DeliveredUnits.Contains(capture.Id))) return Result<bool>.Ok(true);
        if (current.Value.Delivered != index || current.Value.Admitted <= index) return Refuse("The original scope is not admitted for delivery.");
        var catalog = await definitions.ReadAsync(ct);
        var benchPlan = await definitions.ReadPlanAsync(doc.BenchId, ct);
        if (!catalog.IsSuccess || catalog.Value.InstanceId != doc.UserInstance || !benchPlan.IsSuccess || !benchPlan.Value.PlanMode
            || benchPlan.Value.Id != doc.PlanId || benchPlan.Value.Revision != doc.PlanRevision || !benchPlan.Value.Scopes.Contains(scope)
            || catalog.Value.Benches.SingleOrDefault(b => b.Id == doc.BenchId) is not { } bench
            || bench.Revision != checked(doc.BenchRevision + current.Value.AddedPins))
            return Refuse("The original Bench or scope changed outside this Plan's recorded delivery steps.");
        var after = UserBenchStore.WithDeliveryPin(bench, origin.Connection, pin);
        var next = capture with { Revision = checked(capture.Revision + 1), Settled = true, BenchRevision = after.Revision,
            PlanRevision = benchPlan.Value.Revision };
        var hash = UserBenchStore.DeliveryHash(review.Digest, next, after);
        if (group is null)
        {
            var captures = await definitions.ReadEditorCapturesAsync(ct);
            if (!captures.IsSuccess || !captures.Value.Any(c => c.SameAs(capture)))
                return Refuse("The original capture changed before delivery.");
            var writer = new PlanWriter(this, scope.DatabasePath, scope.StoreInstance, Persistence.ConnectionText(origin.Connection),
                step => new(review.Digest, doc.UserInstance, doc.BenchId, doc.PlanId, scope.Id, origin.StoreInstance, capture.Id, hash, step, pin?.NativeId));
            using var tracking = writer.Enter();
            var committed = await opened.Value.CommitAsync(origin.Connection, state.Version,
                new(state.Version + 1, state.Items, state.Relationships, state.RetainedItems.Where(d => d.Key != key),
                    state.Plans, state.Journal, state.Mappings, state.Benches), ct);
            if (!committed.IsSuccess) return Result<bool>.Fail(committed.Error);
            group = writer.Group ?? throw new InvalidDataException("The scope CAS has no publication manifest.");
        }
        else if (group.User != doc.UserInstance || group.Scope != scope.Id || group.Origin != origin.StoreInstance
            || group.Capture != capture.Id || group.DefinitionsHash != hash || ReleasedBenchStore.Proof(scope.DatabasePath, group.Step) != true)
            return Refuse("Incomplete original delivery evidence; the shared Bench remains withheld.");
        if (!await definitions.DeliveryApplied(doc.UserInstance, group.Step.Id, review.Digest, hash, ct))
        {
            var shared = await definitions.SaveEditorCaptureAsync(next, capture.Revision, ct,
                new(review.Digest, group.Step.Id, capture, hash, index, entry.Captures.Length, bench, pin,
                    point => StepBoundary?.Invoke(point, group.Step.After) ?? Task.CompletedTask));
            if (!shared.IsSuccess) return Result<bool>.Fail(shared.Error);
        }
        if (StepBoundary is { } stop) await stop("plan-definitions", group.Step.After);
        if (!await definitions.DeliveryApplied(doc.UserInstance, group.Step.Id, review.Digest, hash, ct))
            return Refuse("The exact shared definitions step was not confirmed.");
        EndPlan(group, true);
        await ProjectWorkspace(root, origin, definitions, [doc.BenchId]);
        return Result<bool>.Ok(true);
    }
}
