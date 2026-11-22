using System.Collections.Immutable;
using Bench.Application;
using Bench.Infrastructure;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Providers.Ado;
using static Twig.Server.BenchHost;

namespace Twig.Server;

internal sealed class NativePlanView
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal (Guid BenchId, string Digest)? Presented { get; set; }
    internal long Epoch { get; set; }
    internal NativePlanOperation? Operation { get; set; }
    internal void Invalidate(bool retire = false)
    {
        lock (this)
        {
            Epoch++; Presented = null;
            if (retire) Operation?.Lifetime.Cancel();
        }
    }
}

internal sealed class NativePlanOperation(Guid id, string status, CancellationTokenSource lifetime)
{
    internal Guid Id { get; } = id;
    internal Guid BenchId { get; init; }
    internal string Status { get; } = status;
    internal CancellationTokenSource Lifetime { get; } = lifetime;
    internal Task<PlanOperationDto>? Work { get; set; }
}

public static partial class NativeBenchHost
{
    private static PlanStatusDto PlanStatus(ViewSession session, NativePlanStatus status)
    {
        NativePanelContext[] contexts;
        lock (session) contexts = session.DisplayContexts.Values.ToArray();
        return new(status.UserInstance, status.BenchId, status.BenchRevision, status.PlanId, status.Revision,
            status.Enabled, status.Sources.Select(source =>
            {
                var context = contexts.SingleOrDefault(value => value.Stamp == source.Source && value.Root == source.OriginRoot);
                return new PlanScopeDto(source.Id, context?.Source.Id, context?.Source.Label
                    ?? NativeConnectionLabels.Unavailable(source.Source.Connection).Name, source.State);
            }).ToArray(), status.LastReviewDigest);
    }

    private static string PlanMessage(Error error) => error.Code == ErrorCode.StorageUnavailable
        ? "Native Plan storage or exact local settlement is unavailable. Original intent and receipts remain protected."
        : error.Message;
    private static PlanOutcomeDto PlanOutcome(NativePlanOutcome result) => new(result.Status, result.VerifiedScopes,
        result.DeliveredScopes, result.RemainingScopes, result.VerifiedOperations, result.RemainingOperations,
        result.Error is null ? null : PlanMessage(result.Error));

    private static void MapPlan(WebApplication app, NativePanel panel, ViewSessions sessions, CatalogChanges updates,
        Func<HttpContext, Guid, Task<ViewSession>> authorize)
    {
        static T Required<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new HttpFailure(
            result.Error.Code switch { ErrorCode.InvalidInput => 400, ErrorCode.NotFound => 404,
                ErrorCode.Conflict or ErrorCode.DigestMismatch or ErrorCode.SchemaChanged => 409,
                ErrorCode.PolicyViolation => 403, ErrorCode.Unsupported => 422, _ => 503 },
            result.Error.Code.ToString(), PlanMessage(result.Error));

        static string AttemptDigest(string? digest) => digest is { Length: 64 } && digest.All(char.IsAsciiHexDigit)
            ? digest : throw new HttpFailure(400, "InvalidInput", "Supply the digest of the original Plan attempt.");

        async Task Current(ViewSession session, Guid bench, CancellationToken ct)
        {
            if (session.CurrentBenchId != bench)
                throw new HttpFailure(409, "Conflict", "The selected Bench changed. Reopen its exact Plan before continuing.");
        }
        async Task<NativePanelContext[]> Connections(ViewSession session, NativePlanStatus status, CancellationToken ct)
        {
            NativePanelContext[] contexts;
            lock (session) contexts = session.DisplayContexts.Values.ToArray();
            var chosen = status.Sources.Select(source => contexts.SingleOrDefault(value =>
                value.Root == source.OriginRoot && value.Stamp == source.Source)
                ?? throw new HttpFailure(409, "PlanConnectionRequired", "Choose each original Plan connection in Connections. No replacement context was borrowed.")).ToArray();
            foreach (var context in chosen)
                _ = Required(await panel.EditorOperationAsync(context, _ => Task.FromResult(true), ct));
            return chosen;
        }
        PlanReviewDto ReviewDto(NativePanelContext[] contexts, NativePlanReview review)
        {
            var effects = review.Plans.SelectMany(plan =>
            {
                var source = contexts.Single(context => context.Stamp.Connection == plan.Connection);
                return plan.Operations.Select(op => new PlanEffectDto(source.Source.Id, source.Source.Label,
                    ViewMapping.Key(new(source.Source.Id, op.Item)),
                    op.Item.IsLocal ? op.Item.LocalId.ToString("D") : op.Item.NativeId!, op.Item.IsLocal,
                    op.Type, op.Fields.Select(field => new FieldDto(field.Key, ViewMapping.Value(field.Value))).ToArray(),
                    op.Links.Select(link => (link.Remove ? "Remove " : "Add ") + link.Relation
                        + (link.Other.IsLocal ? " to a local item" : " to item " + link.Other.NativeId)).ToArray()));
            }).ToArray();
            return new(review.BenchId, review.Digest, effects, review.CoreDigests.ToArray(),
                "Apply is sequential across items and connections, not atomic. Verified, remaining, uncertain and delivery-pending outcomes stay distinct."
                    + (review.Plans.Any(plan => plan.Connection.Provider.Value == "github")
                        ? " GitHub uses observed-non-atomic checks and readback, not server CAS or an edit lock. Repository automation rules are not discoverable." : "")
                    + (review.Plans.Any(plan => plan.Connection.Provider.Value == "ado"
                        && plan.Operations.Any(op => op.Kind == OpKind.Create)) ? " " + AdoProvider.CreateConcurrencyNotice : ""),
                null);
        }

        app.MapGet("/api/v1/views/{id:guid}/benches/{bench:guid}/plan", async (HttpContext http, Guid id, Guid bench) =>
        {
            var session = await authorize(http, id);
            await Current(session, bench, http.RequestAborted);
            var (status, version) = Required(await panel.ReadStagedPlanVersionAsync(bench, http.RequestAborted));
            string? stagedVersion = null;
            if (status.Enabled && status.Sources.Length != 0 && status.Sources.All(source => source.State == "Ready"))
            {
                _ = await Connections(session, status, http.RequestAborted);
                stagedVersion = version;
                await Current(session, bench, http.RequestAborted);
            }
            await Write(http, PlanStatus(session, status) with { StagedVersion = stagedVersion });
        });
        app.MapPut("/api/v1/views/{id:guid}/plan/mode", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<PlanModeBody>(http);
            await session.Editor.Gate.WaitAsync(http.RequestAborted);
            try
            {
                if (session.Plan.Operation?.Work is { IsCompleted: false })
                    throw new HttpFailure(409, "PlanBusy", "Wait for the original Plan operation's outcome before changing mode.");
                var source = session.NativeContext;
                var profile = session.ProfileGeneration;
                await Current(session, body.BenchId, http.RequestAborted);
                lock (session)
                    if (session.ProfileGeneration != profile || !ReferenceEquals(session.NativeContext, source))
                        throw new HttpFailure(409, "SourceChanged", "The source or profile changed before Plan enable.");
                var changed = Required(await panel.ChangePlanModeAsync(body.StoreId, body.BenchId, body.BenchRevision,
                    body.PlanRevision, body.Enabled, http.RequestAborted));
                CancelEditor(session); session.Editor.Native?.Dispose(); session.Editor.Native = null;
                session.Plan.Invalidate();
                try
                {
                    if (body.Enabled && source is not null)
                    {
                        CancellationToken navigation;
                        lock (session.Editor) navigation = session.Editor.Navigation.Token;
                        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, navigation);
                        lock (session)
                            if (session.ProfileGeneration != profile || !ReferenceEquals(session.NativeContext, source))
                                throw new HttpFailure(409, "SourceChanged", "The source or profile changed during Plan enable.");
                        _ = Required(await panel.EditorOperationAsync(source, async ct =>
                        {
                            using var prepared = Required(await panel.OpenEditorAsync(source, body.BenchId, ct));
                            if (!prepared.PlanMode)
                                throw new HttpFailure(409, "Conflict", "The Plan mode changed before its explicit source preparation.");
                            return true;
                        }, lifetime.Token));
                        lifetime.Token.ThrowIfCancellationRequested();
                        await Current(session, body.BenchId, lifetime.Token);
                        lock (session)
                            if (session.ProfileGeneration != profile || !ReferenceEquals(session.NativeContext, source))
                                throw new HttpFailure(409, "SourceChanged", "The source or profile changed during Plan source preparation.");
                        changed = Required(await panel.ReadPlanAsync(body.BenchId, lifetime.Token));
                        if (!changed.Enabled)
                            throw new HttpFailure(409, "Conflict", "The Plan mode changed after its explicit source preparation.");
                    }
                }
                catch (HttpFailure problem) when (body.Enabled)
                {
                    throw new HttpFailure(problem.Status, problem.Code,
                        "Plan mode was enabled, but current-source preparation was not confirmed. Reload its actual state. " + problem.Message);
                }
                catch (OperationCanceledException) when (body.Enabled)
                {
                    throw new HttpFailure(504, "Indeterminate",
                        "Plan mode was enabled, but current-source preparation was cancelled or timed out. Reload its actual state; no rollback was attempted.");
                }
                finally { updates.Publish(0); }
                await Write(http, PlanStatus(session, changed));
            }
            finally { session.Editor.Gate.Release(); }
        });
        app.MapPost("/api/v1/views/{id:guid}/plan/seed", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<PlanSeedBody>(http);
            await session.Editor.Gate.WaitAsync(http.RequestAborted);
            try
            {
                await Current(session, body.BenchId, http.RequestAborted);
                var mode = Required(await panel.ReadPlanAsync(body.BenchId, http.RequestAborted));
                if (!mode.Enabled) throw new HttpFailure(409, "Conflict", "Enter Plan mode explicitly before creating a local draft.");
                NativePanelContext source;
                lock (session) source = session.DisplayContexts.Values.SingleOrDefault(value => value.Source.Id == body.SourceId)
                    ?? throw new HttpFailure(409, "ContextRequired", "Choose this native connection first.");
                var form = Required(await panel.EditorOperationAsync(source, async ct =>
                {
                    session.Editor.Native?.Dispose(); session.Editor.Native = null;
                    session.Editor.Native = Required(await panel.OpenEditorAsync(source, body.BenchId, ct));
                    session.Editor.Context = source; session.Editor.Bench = body.BenchId;
                    return Required(await session.Editor.Native.CreateSeedAsync(body.Type, ct));
                }, http.RequestAborted));
                session.Editor.Ticket = form.Ticket; session.Editor.Type = form.Type;
                session.Editor.Item = form.Item; session.Editor.NormalSeed = false;
                session.Plan.Invalidate(); updates.Publish(0);
                await Write(http, CaptureInputs(sessions, session, BoundEditorForm(form, source, session.Editor.Native!)));
            }
            finally { session.Editor.Gate.Release(); }
        });
        app.MapPost("/api/v1/views/{id:guid}/plan/review", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<PlanBenchBody>(http);
            await session.Plan.Gate.WaitAsync(http.RequestAborted);
            try
            {
                var anchor = session.NativeContext;
                session.Plan.Invalidate();
                var epoch = session.Plan.Epoch;
                var generation = session.ProfileGeneration;
                Start(session, body.BenchId, "Reviewing", async ct =>
                {
                    await Current(session, body.BenchId, ct);
                    var status = Required(await panel.ReadPlanAsync(body.BenchId, ct));
                    var contexts = await Connections(session, status, ct);
                    var origin = contexts.FirstOrDefault()
                        ?? throw new HttpFailure(409, "PlanConnectionRequired", "This Plan has no available original source to review.");
                    var review = Required(await panel.ReviewPlanAsync(origin, body.BenchId, status.Sources, contexts, ct));
                    await Current(session, body.BenchId, ct);
                    lock (session.Plan)
                    {
                        if (session.Plan.Epoch != epoch || session.Revoked || session.ProfileGeneration != generation
                            || !ReferenceEquals(session.NativeContext, anchor))
                            throw new HttpFailure(409, "PlanNavigationChanged", "The view changed during preview. Reopen the named Plan.");
                        session.Plan.Presented = (review.BenchId, review.Digest);
                        return new(session.Plan.Operation!.Id, "Reviewed", ReviewDto(contexts, review), null, null);
                    }
                });
                await Write(http, new PlanOperationDto(session.Plan.Operation!.Id, "Reviewing", null, null, null), 202);
            }
            finally { session.Plan.Gate.Release(); }
        });
        app.MapPost("/api/v1/views/{id:guid}/plan/observe", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<PlanObserveBody>(http);
            await Current(session, body.BenchId, http.RequestAborted);
            var review = Required(await panel.OpenPlanReviewAsync(Context(session), AttemptDigest(body.Digest), http.RequestAborted));
            if (review.BenchId != body.BenchId) throw new HttpFailure(409, "Conflict", "The original attempt belongs to another Bench.");
            var status = Required(await panel.ReadPlanAsync(body.BenchId, http.RequestAborted));
            var contexts = await Connections(session, status with { Sources = review.Sources }, http.RequestAborted);
            var outcome = PlanOutcome(Required(await review.ObserveAsync(http.RequestAborted)));
            await Write(http, ReviewDto(contexts, review) with { Outcome = outcome });
        });
        app.MapPost("/api/v1/views/{id:guid}/plan/apply", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<PlanApplyBody>(http);
            if (body.StagedVersion is not { Length: 64 } || body.StagedVersion.Any(c => !char.IsAsciiHexDigit(c)))
                throw new HttpFailure(400, "InvalidInput", "Apply requires the staged version from the current Plan status.");
            await RunPlan(session, body.BenchId, body.StagedVersion, null, http);
        });
        app.MapPost("/api/v1/views/{id:guid}/plan/continue", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<PlanObserveBody>(http);
            await RunPlan(session, body.BenchId, null, AttemptDigest(body.Digest), http);
        });
        async Task RunPlan(ViewSession session, Guid benchId, string? stagedVersion, string? attemptDigest, HttpContext http)
        {
            await session.Plan.Gate.WaitAsync(http.RequestAborted);
            try
            {
                var anchor = Context(session);
                var epoch = session.Plan.Epoch;
                var generation = session.ProfileGeneration;
                await Current(session, benchId, http.RequestAborted);
                var phase = attemptDigest is null ? "Applying" : "Delivering";
                Start(session, benchId, phase, async ct =>
                {
                    async Task CheckView()
                    {
                        await Current(session, benchId, ct);
                        if (session.Plan.Epoch != epoch || session.ProfileGeneration != generation
                            || !ReferenceEquals(session.NativeContext, anchor) || session.Revoked)
                            throw new HttpFailure(409, "PlanNavigationChanged", "The view changed before dispatch. Nothing was dispatched.");
                    }
                    await CheckView();
                    NativePlanReview review;
                    NativePanelContext[] contexts;
                    if (attemptDigest is null)
                    {
                        var (status, version) = Required(await panel.ReadStagedPlanVersionAsync(benchId, ct));
                        if (stagedVersion != version)
                            throw new HttpFailure(409, "Conflict", "Staged Plan intent changed. Reload it before applying.");
                        contexts = await Connections(session, status, ct);
                        review = Required(await panel.ReviewPlanAsync(anchor, benchId, status.Sources, contexts, ct));
                    }
                    else
                    {
                        review = Required(await panel.OpenPlanReviewAsync(anchor, attemptDigest, ct));
                        if (review.BenchId != benchId)
                            throw new HttpFailure(409, "Conflict", "The original attempt belongs to another Bench.");
                        var status = Required(await panel.ReadPlanAsync(benchId, ct));
                        contexts = await Connections(session, status with { Sources = review.Sources }, ct);
                    }
                    await CheckView();
                    var result = Required(attemptDigest is null
                        ? await panel.ApplyPlanAsync(review, contexts, stagedVersion!, ct)
                        : await panel.ContinuePlanDeliveryAsync(review, contexts, ct));
                    return new(session.Plan.Operation!.Id, result.Status, null, PlanOutcome(result), null);
                });
                await Write(http, new PlanOperationDto(session.Plan.Operation!.Id, phase, null, null, null), 202);
            }
            finally { session.Plan.Gate.Release(); }
        }
        app.MapGet("/api/v1/views/{id:guid}/plan/operations/{operation:guid}", async (HttpContext http, Guid id, Guid operation) =>
        {
            var session = await authorize(http, id);
            var active = session.Plan.Operation;
            if (active is null || active.Id != operation || active.Work is null)
                throw new HttpFailure(404, "PlanOperationUnavailable", "Reopen the named Bench and observe its saved native review. Never resend a lost Apply.");
            await Write(http, active.Work.IsCompleted ? await active.Work
                : new PlanOperationDto(active.Id, active.Status, null, null, null));
        });

        void Start(ViewSession session, Guid benchId, string status, Func<CancellationToken, Task<PlanOperationDto>> work)
        {
            if (session.Plan.Operation?.Work is { IsCompleted: false })
                throw new HttpFailure(409, "PlanBusy", "Observe the original Plan operation; it will not be invoked twice.");
            NativePlanOperation operation;
            lock (session.Plan)
            {
                session.Plan.Operation?.Lifetime.Dispose();
                operation = new(Guid.NewGuid(), status, CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping))
                    { BenchId = benchId };
                session.Plan.Operation = operation;
            }
            operation.Work = Run();
            async Task<PlanOperationDto> Run()
            {
                try { return await work(operation.Lifetime.Token); }
                catch (HttpFailure error) { return new(operation.Id, "Protected", null, null, new(error.Code, error.Message)); }
                catch (OperationCanceledException)
                { return new(operation.Id, "Protected", null, null, new("PlanInterrupted", "Observe the saved native review and original receipts before continuing. Apply was not replayed.")); }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    app.Logger.LogError(error, "Native Plan operation failed; original references were preserved.");
                    return new(operation.Id, "Protected", null, null, new("PlanUnavailable", "Native Plan data is unavailable. Original intent and receipts remain protected; observe before continuing."));
                }
                finally { updates.Publish(0); }
            }
        }
    }

    private static EditorFormDto BoundEditorForm(NativeEditForm form, NativePanelContext source, NativeItemEditor editor)
        => EditorForm(form, source.Stamp.AuthorityStamp) with
        {
            InputBaseline = form.InputBaseline,
            ItemId = ViewMapping.Key(new(source.Source.Id, form.Item)),
            RelationshipsReadOnly = form.RelationshipsReadOnly,
            RelationshipIntent = form.RelationshipIntent.Select(link => RelationshipIntent(link, source)).ToArray(),
            Notice = source.Stamp.Connection.Provider.Value == "github"
                ? "GitHub Save and Plan effects use observed-non-atomic concurrency: fresh checks and readback, not a server CAS, edit lock or atomic field/close-gate guarantee. Repository automation rules are not discoverable; only native API semantics and declared Twig policies are validated."
                : source.Stamp.Connection.Provider.Value == "ado" && form.Item.IsLocal ? AdoProvider.CreateConcurrencyNotice : null,
            Binding = new(editor.PlanMode, editor.PlanId, editor.PlanRevision, editor.ScopeId, editor.StoreInstance)
        };
}
