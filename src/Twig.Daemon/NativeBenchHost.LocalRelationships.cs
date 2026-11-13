using System.Text;
using Bench.Application;
using Bench.Infrastructure;
using Twig.Core;
using Twig.Infrastructure.Services;
using static Bench.Server.BenchHost;

namespace Bench.Server;

public static partial class NativeBenchHost
{
    private const string LocalVerification = "LOCAL store CAS only; no native tracker mutation or tracker publication.";

    private static void MapLocalRelationships(WebApplication app, NativePanel panel, CatalogChanges updates,
        Func<HttpContext, Guid, Task<ViewSession>> authorize,
        Func<HttpContext, ViewSession, Guid, Func<NativeItemEditor, CancellationToken, Task>, Task> edit)
    {
        static T Required<T>(Result<T> result)
        {
            if (result.IsSuccess) return result.Value;
            if (result.Error.Evidence.Contains("http:401") || result.Error.Evidence.Contains("http:403"))
                throw new HttpFailure(403, "PolicyViolation", "This source cannot release LOCAL editor content; explicit native reauthorization is required.");
            throw new HttpFailure(result.Error.Code switch
            {
                ErrorCode.InvalidInput => 400, ErrorCode.NotFound => 404,
                ErrorCode.Conflict or ErrorCode.DigestMismatch or ErrorCode.SchemaChanged => 409,
                ErrorCode.PolicyViolation => 403, ErrorCode.Unsupported => 422, _ => 503,
            }, result.Error.Code.ToString(), result.Error.Message);
        }

        async Task Current(HttpContext http, Guid id, ViewSession session, NativePanelContext source,
            long epoch, Guid profile, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _ = await authorize(http, id);
            var state = session.Editor;
            if (session.Revoked || session.ProfileGeneration != profile || state.Epoch != epoch
                || !ReferenceEquals(Context(session), source) || !ReferenceEquals(state.Context, source)
                || source.Stamp.Connection.Provider.Value != "github" || state.Native is null || state.Native.PlanMode
                || state.Native.StoreInstance != source.Stamp.StoreInstance)
                throw new HttpFailure(409, "Conflict", "The original LOCAL source/session/navigation binding changed; no replacement effect.");
            var plan = Required(await panel.ReadPlanAsync(state.Bench, ct));
            if (plan.Enabled || state.Native.PlanId != plan.PlanId || state.Native.PlanRevision != plan.Revision
                || Required(await panel.ReadSelectionAsync(source, ct)).SelectedBenchId != state.Bench)
                throw new HttpFailure(409, "Conflict", "The original LOCAL Bench/mode changed; observe in its exact normal source.");
        }

        app.MapPost("/api/v1/views/{id:guid}/editor/local/reviews", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<LocalRelationshipReadBody>(http);
            if (body.ItemId.Length is 0 or > 128)
                throw new HttpFailure(400, "InvalidInput", "Choose one bounded published item identity.");
            CancelEditor(session);
            var state = session.Editor;
            var source = Context(session);
            var epoch = state.Epoch; var profile = session.ProfileGeneration;
            var navigation = state.Navigation.Token;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, navigation);
            await state.Gate.WaitAsync(lifetime.Token);
            try
            {
                if (source.Source.Id != body.SourceId || source.Stamp.Connection.Provider.Value != "github")
                    throw new HttpFailure(422, "Unsupported", "Original LOCAL reviews require the explicit normal viewing GitHub source.");
                var mode = Required(await panel.ReadPlanAsync(body.BenchId, lifetime.Token));
                if (mode.Enabled) throw new HttpFailure(409, "Conflict", "LOCAL associations are separate from Plan.");
                var capture = Required(await panel.CaptureAsync(source, lifetime.Token));
                var item = capture.Observation.Snapshot.Items.SingleOrDefault(item => !item.Key.IsLocal
                    && ViewMapping.Key(new(source.Source.Id, item.Key)) == body.ItemId)
                    ?? throw new HttpFailure(404, "NotFound", "The exact published item is unavailable.");
                _ = Required(await panel.EditorOperationAsync(source, async ct =>
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (state.Epoch != epoch || session.ProfileGeneration != profile || session.Revoked
                        || !ReferenceEquals(Context(session), source))
                        throw new HttpFailure(409, "Conflict", "LOCAL navigation changed during source admission.");
                    state.Native?.Dispose(); state.Native = null;
                    state.Native = Required(await panel.OpenEditorAsync(source, body.BenchId, ct));
                    state.Context = source; state.Bench = body.BenchId; state.Item = item.Key;
                    state.LocalOnly = true; state.Ticket = Guid.NewGuid();
                    var reviews = Required(await state.Native.ReadLocalRelationshipReviewsAsync(ct));
                    ItemKey Display(ItemKey key) => capture.Observation.Snapshot.Mappings.FirstOrDefault(mapping => mapping.Local == key)?.Remote ?? key;
                    foreach (var review in reviews.Where(review => review.Proposal.Operations.Any(op =>
                        Display(op.Item) == item.Key || op.Other is { } other && Display(other) == item.Key)))
                    {
                        state.LocalReviews[review.CaptureId] = review;
                        if (LocalUnattempted(review.Outcome)) state.LocalApplyAllowed.Add(review.CaptureId);
                    }
                    await Current(http, id, session, source, epoch, profile, ct);
                    await Write(http, state.LocalReviews.Values.Select(review => LocalReview(review, state,
                        capture.Observation.Snapshot)).ToArray());
                    return true;
                }, lifetime.Token));
            }
            catch (HttpFailure failure) when (failure.Code == "PolicyViolation")
            {
                state.Invalidate(); state.Native?.Dispose(); state.Native = null;
                throw;
            }
            finally
            {
                if (state.Retired) { state.Native?.Dispose(); state.Native = null; }
                state.Gate.Release();
            }
        });

        app.MapPost("/api/v1/views/{id:guid}/editor/local/review", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<LocalRelationshipReviewBody>(http);
            await edit(http, session, body.Ticket, async (editor, ct) =>
            {
                var state = session.Editor; var source = state.Context!;
                var epoch = state.Epoch; var profile = session.ProfileGeneration;
                if (!state.LocalOnly || state.Endpoint is null || state.LocalReviews.Count != 0)
                    throw new HttpFailure(409, "Conflict", "Choose the exact qualified LOCAL pair; original reviews cannot be replaced.");
                await Current(http, id, session, source, epoch, profile, ct);
                var review = Required(await editor.ReviewLocalRelationshipAsync(body.Ticket, body.Adding, ct));
                state.LocalReviews[review.CaptureId] = review;
                state.Ticket = Guid.NewGuid();
                var original = Required(await editor.ReadLocalRelationshipReviewsAsync(ct))
                    .Single(value => value.CaptureId == review.CaptureId && value.Proposal.Digest == review.Proposal.Digest);
                state.LocalReviews[original.CaptureId] = original;
                if (LocalUnattempted(original.Outcome)) state.LocalApplyAllowed.Add(original.CaptureId);
                await Current(http, id, session, source, epoch, profile, ct);
                var snapshot = Required(await panel.ReadEditorSourceAsync(source, ct)).Snapshot;
                await Current(http, id, session, source, epoch, profile, ct);
                await Write(http, LocalReview(original, state, snapshot));
            });
        });

        foreach (var operation in new[] { "apply", "observe", "continue" })
        {
            app.MapPost("/api/v1/views/{id:guid}/editor/local/" + operation, async (HttpContext http, Guid id) =>
            {
                var session = await authorize(http, id);
                var body = await Body<LocalRelationshipReferenceBody>(http);
                if (body.CaptureId == Guid.Empty || body.Digest.Length != 64 || body.Digest.Any(value => !char.IsAsciiHexDigit(value))
                    || body.Operation != 0)
                    throw new HttpFailure(400, "InvalidInput", "Use the original native capture, exact digest and single operation ordinal.");
                await edit(http, session, body.Ticket, async (editor, ct) =>
                {
                    var state = session.Editor; var source = state.Context!;
                    var epoch = state.Epoch; var profile = session.ProfileGeneration;
                    if (!state.LocalOnly || !state.LocalReviews.TryGetValue(body.CaptureId, out var original)
                        || original.Proposal.Digest != body.Digest || original.Outcome is null
                        || (original.Outcome.Journal?.Attempt ?? Guid.Empty) != body.Attempt)
                        throw new HttpFailure(409, "Conflict", "Rediscover the exact original LOCAL reference; no replacement or replay.");
                    await Current(http, id, session, source, epoch, profile, ct);
                    if (operation == "apply" && !state.LocalApplyAllowed.Remove(body.CaptureId))
                        throw new HttpFailure(409, "Conflict", "The original LOCAL effect is protected; Apply cannot be replayed.");
                    var result = Required(operation switch
                    {
                        "apply" => await editor.ApplyLocalRelationshipAsync(body.CaptureId, body.Digest, ct),
                        "continue" => await editor.ContinueLocalRelationshipAsync(body.CaptureId, body.Digest, ct),
                        _ => await editor.ObserveLocalRelationshipAsync(body.CaptureId, body.Digest, ct),
                    });
                    if (result.Error?.Code == ErrorCode.PolicyViolation)
                        throw new HttpFailure(403, "PolicyViolation", "This source cannot release original LOCAL content.");
                    var observed = Required(await editor.ReadLocalRelationshipReviewAsync(body.CaptureId, body.Digest, ct));
                    state.LocalReviews[body.CaptureId] = observed;
                    if (!LocalUnattempted(observed.Outcome)) state.LocalApplyAllowed.Remove(body.CaptureId);
                    await Current(http, id, session, source, epoch, profile, ct);
                    var snapshot = Required(await panel.ReadEditorSourceAsync(source, ct)).Snapshot;
                    await Current(http, id, session, source, epoch, profile, ct);
                    await Write(http, LocalReview(observed, state, snapshot, result.Error));
                    if (operation != "observe") updates.Publish(0);
                });
            });
        }
    }

    private static bool LocalUnattempted(NativeLocalRelationshipOutcome? outcome) => outcome is { Settled: false, Error: null,
        Journal: { Status: OpStatus.Planned, Attempt: var attempt, Receipts.IsEmpty: true } } && attempt == Guid.Empty;

    private static LocalRelationshipReviewDto LocalReview(NativeLocalRelationshipReview review, NativeEditorView state, StoreSnapshot snapshot,
        Error? operationError = null)
    {
        var proposal = review.Proposal;
        if (proposal.Operations.Length != 1 || proposal.Operations[0] is not { Origin: RelationshipOrigin.Local,
            Kind: OpKind.Relate or OpKind.Unrelate, Other: not null, Relation: not null, Fields.Count: 0,
            Links.IsEmpty: true, Body: null, Resource: null, Type: null } op)
            throw new HttpFailure(422, "Unsupported", "The native review is not one canonical field-free LOCAL Association.");
        var outcome = review.Outcome ?? throw new HttpFailure(503, "Unavailable", "The original native LOCAL outcome is unavailable.");
        var row = outcome.Journal;
        if (row is not null && (row.Digest != proposal.Digest || row.Operation != 0
            || row.Receipts.Any(receipt => receipt.PublishedKey is not null || receipt.Revision is not null)))
            throw new HttpFailure(409, "Conflict", "The original LOCAL journal does not match its canonical effect.");
        var source = state.Context!;
        RelationshipEndpointDto Endpoint(ItemKey key)
        {
            var displayed = snapshot.Mappings.FirstOrDefault(mapping => mapping.Local == key)?.Remote ?? key;
            var item = snapshot.Items.SingleOrDefault(item => item.Key == displayed)
                ?? throw new HttpFailure(409, "Conflict", "The original LOCAL endpoint observation is unavailable.");
            return RelationshipEndpoint(item, source);
        }
        Failure? Failure(Error? error) => error is null ? null : new(error.Code.ToString(), error.Message);
        var result = new LocalRelationshipOutcomeDto(outcome.Digest, row?.Operation ?? 0, row?.Attempt ?? Guid.Empty,
            row?.Status.ToString() ?? "Preparing", outcome.Settled, row?.Receipts.Select(receipt => new LocalRelationshipReceiptDto(
                receipt.Digest, receipt.Operation, receipt.Attempt, receipt.Status.ToString(), receipt.Evidence.ToArray(),
                Failure(receipt.Error))).ToArray() ?? [], Failure(operationError ?? outcome.Error));
        return new(state.Ticket, review.CaptureId, proposal.Digest, Encoding.UTF8.GetString(Twig.Core.Core.ProposalBytes(proposal)),
            new(op.Kind.ToString(), Endpoint(op.Item), Endpoint(op.Other), op.Relation, op.ExpectedLocalVersion,
                proposal.ExpectedStoreVersion, proposal.SchemaFingerprint),
            new(false, state.Native!.PlanId, state.Native.PlanRevision, state.Native.ScopeId, state.Native.StoreInstance),
            source.Stamp.AuthorityStamp, state.Epoch,
            state.LocalApplyAllowed.Contains(review.CaptureId) && LocalUnattempted(outcome),
            !outcome.Settled && row?.Status is null or OpStatus.Applied or OpStatus.Verified,
            result, LocalVerification);
    }
}
