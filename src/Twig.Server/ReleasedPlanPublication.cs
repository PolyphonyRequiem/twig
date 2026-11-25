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
    Guid Capture, string DefinitionsHash, Step Step);
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
            if (!retained.IsSuccess || retained.Value.Document is not { Scopes: [var entry] } doc
                || doc.UserInstance != p.User || doc.BenchId != p.Bench || doc.PlanId != p.Plan
                || entry.Captures is not [var capture] || capture.Id != p.Capture || !retained.Value.Units.Contains(capture.Id)
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
        static Result<NativePlanOutcome> Refuse(string reason) => Result<NativePlanOutcome>.Fail(ErrorCode.Conflict, reason);
        var retained = await definitions.ReadPlanReviewAsync(review.Digest, ct);
        if (!retained.IsSuccess) return Result<NativePlanOutcome>.Fail(retained.Error);
        var original = retained.Value;
        PlanPublication? group;
        lock (release) planPending.TryGetValue(review.Digest, out group);
        if (group is null && (original.Delivered == original.Document.Scopes.Length || original.Units.Count == 0))
            return await review.ObserveAsync(ct);
        var doc = original.Document;
        if (origin.Connection.Provider.Value != "twig" || original.Abandoned || original.Admitted != 1 || doc.Scopes is not
            [{ Plan: null, Captures: [var capture], Previews: [""], CaptureVersions: [var version], Scope: var scope }]
            || !original.Units.SetEquals([capture.Id]) || !capture.Ready || capture.Settled || capture.Cancelled
            || scope.State != NativePlanScopeState.Ready || scope.Source != origin || capture.Origin != origin
            || capture.StoreInstance != scope.StoreInstance || capture.ScopeId != scope.Id || capture.UserInstance != doc.UserInstance
            || capture.PlanId != doc.PlanId || capture.BenchId != doc.BenchId
            || !NativePathComparer.Instance.Equals(root, scope.OriginRoot)
            || !NativePathComparer.Instance.Equals(scope.DatabasePath, definitions.PlanDatabase(doc.PlanId, scope.Id)))
            return Refuse("Only the exact admitted single converged local edit supports isolated Plan settlement.");
        if (UserBenchStore.ParseIntent(capture.IntentJson).Operations is not [{ Kind: OpKind.Batch, Item: var key }]
            || !key.BelongsTo(origin.Connection)) return Refuse("No qualified converged edit.");
        CheckPlanPath(scope.DatabasePath);
        var opened = await SqliteStore.OpenAsync(scope.DatabasePath, ct, scope.StoreInstance);
        if (!opened.IsSuccess) return Result<NativePlanOutcome>.Fail(opened.Error);
        var snapshot = await opened.Value.ReadAsync(origin.Connection, ct);
        if (!snapshot.IsSuccess) return Result<NativePlanOutcome>.Fail(snapshot.Error);
        var state = snapshot.Value;
        if (group is null)
        {
            if (state.RetainedItems.SingleOrDefault(d => d.Key == key) is not LocalEdits edit
                || CoreWorkspace.Fingerprint(origin.Connection, edit) != capture.DraftFingerprint)
                return Refuse("The reviewed scope draft is absent or replaced; no original ledger proves settlement.");
            var observed = await review.ObserveConvergenceAsync(scope, edit, version, ct);
            if (!observed.IsSuccess) return Result<NativePlanOutcome>.Fail(observed.Error);
        }
        using var benchLease = definitions.AcquireAuthoringLease(doc.BenchId);
        using var local = ConnectionOperationGate.Acquire(root, exclusive: true);
        lock (release)
            if (group != planPending.GetValueOrDefault(review.Digest))
                return Refuse("Original delivery advanced during observation. Inspect its existing result.");
        var current = await definitions.ReadPlanReviewAsync(review.Digest, ct);
        if (!current.IsSuccess || current.Value.Abandoned || !current.Value.Units.SetEquals([capture.Id]))
            return Refuse("The original admission changed before settlement.");
        if (group is null && current.Value.Delivered == 1) return await review.ObserveAsync(ct);
        var catalog = await definitions.ReadAsync(ct);
        var plan = await definitions.ReadPlanAsync(doc.BenchId, ct);
        if (!catalog.IsSuccess || catalog.Value.InstanceId != doc.UserInstance || !plan.IsSuccess || !plan.Value.PlanMode
            || plan.Value.Id != doc.PlanId || plan.Value.Revision != doc.PlanRevision || !plan.Value.Scopes.Contains(scope)
            || catalog.Value.Benches.SingleOrDefault(b => b.Id == doc.BenchId) is not { } bench || bench.Revision != doc.BenchRevision)
            return Refuse("The admitted Plan no longer owns its original stores.");
        var next = capture with { Revision = checked(capture.Revision + 1), Settled = true, BenchRevision = bench.Revision,
            PlanRevision = plan.Value.Revision };
        var hash = UserBenchStore.DeliveryHash(review.Digest, next, bench);
        if (group is null)
        {
            var captures = await definitions.ReadEditorCapturesAsync(ct);
            if (!captures.IsSuccess || !captures.Value.Any(c => c.SameAs(capture)))
                return Refuse("The original capture changed before delivery.");
            var writer = new PlanWriter(this, scope.DatabasePath, scope.StoreInstance, Persistence.ConnectionText(origin.Connection),
                step => new(review.Digest, doc.UserInstance, doc.BenchId, doc.PlanId, scope.Id, origin.StoreInstance, capture.Id, hash, step));
            using var tracking = writer.Enter();
            var committed = await opened.Value.CommitAsync(origin.Connection, state.Version,
                new(state.Version + 1, state.Items, state.Relationships, state.RetainedItems.Where(d => d.Key != key),
                    state.Plans, state.Journal, state.Mappings, state.Benches), ct);
            if (!committed.IsSuccess) return Result<NativePlanOutcome>.Fail(committed.Error);
            group = writer.Group ?? throw new InvalidDataException("The scope CAS has no publication manifest.");
        }
        else if (group.User != doc.UserInstance || group.Scope != scope.Id || group.Origin != origin.StoreInstance
            || group.Capture != capture.Id || group.DefinitionsHash != hash || ReleasedBenchStore.Proof(scope.DatabasePath, group.Step) != true)
            return Refuse("Incomplete original delivery evidence; the shared Bench remains withheld.");
        if (!await definitions.DeliveryApplied(doc.UserInstance, group.Step.Id, review.Digest, hash, ct))
        {
            var shared = await definitions.SaveEditorCaptureAsync(next, capture.Revision, ct,
                new(review.Digest, group.Step.Id, capture, hash, point => StepBoundary?.Invoke(point, group.Step.After) ?? Task.CompletedTask));
            if (!shared.IsSuccess) return Result<NativePlanOutcome>.Fail(shared.Error);
        }
        if (StepBoundary is { } stop) await stop("plan-definitions", group.Step.After);
        if (!await definitions.DeliveryApplied(doc.UserInstance, group.Step.Id, review.Digest, hash, ct))
            return Refuse("The exact shared definitions step was not confirmed.");
        EndPlan(group, true);
        await ProjectWorkspace(root, origin, definitions, [doc.BenchId]);
        return await review.ObserveAsync(ct);
    }
}
