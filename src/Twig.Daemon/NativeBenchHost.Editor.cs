using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Bench.Application;
using Bench.Infrastructure;
using Twig.Core;
using Twig.Infrastructure.Services;
using static Bench.Server.BenchHost;

namespace Bench.Server;

internal sealed class NativeEditorView
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal CancellationTokenSource Navigation { get; set; } = new();
    internal NativeItemEditor? Native { get; set; }
    internal NativePanelContext? Context { get; set; }
    internal Guid Bench { get; set; }
    internal Guid Ticket { get; set; }
    internal string? Type { get; set; }
    internal bool NormalSeed { get; set; }
    internal ItemKey? Item { get; set; }
    internal ItemKey? Endpoint { get; set; }
    internal ImmutableArray<ItemKey> EndpointCandidates { get; set; } = [];
    internal NativeAuthoringReview? Upgrade { get; set; }
    internal long Epoch { get; set; }
    internal bool LocalOnly { get; set; }
    internal Dictionary<Guid, NativeLocalRelationshipReview> LocalReviews { get; } = [];
    internal HashSet<Guid> LocalApplyAllowed { get; } = [];
    internal bool Retired { get; set; }
    internal void Invalidate(bool retire = false)
    {
        lock (this)
        {
            Navigation.Cancel();
            Navigation = new();
            Epoch++;
            Ticket = Guid.Empty;
            LocalOnly = false;
            LocalReviews.Clear(); LocalApplyAllowed.Clear();
            Item = Endpoint = null;
            EndpointCandidates = [];
            Upgrade = null;
            Retired |= retire;
        }
        if (retire && Gate.Wait(0))
        {
            try { Native?.Dispose(); Native = null; }
            finally { Gate.Release(); }
        }
    }
}

public static partial class NativeBenchHost
{
    private static void CancelEditor(ViewSession session)
    {
        session.Editor.Invalidate();
        session.Plan.Invalidate();
    }

    private static void MapEditor(WebApplication app, NativePanel panel, CatalogChanges updates, Func<HttpContext, Guid, Task<ViewSession>> authorize)
    {
        static T Require<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new HttpFailure(result.Error.Code switch
        {
            ErrorCode.InvalidInput => 400, ErrorCode.NotFound => 404,
            ErrorCode.Conflict or ErrorCode.DigestMismatch or ErrorCode.SchemaChanged => 409,
            ErrorCode.PolicyViolation => 403, ErrorCode.Unsupported => 422, _ => 503,
        }, result.Error.Code.ToString(), result.Error.Message
            + (result.Error.Code == ErrorCode.Conflict && !result.Error.Evidence.IsEmpty
                ? " Conflicting fields: " + string.Join(", ", result.Error.Evidence.Take(8)) : ""));
        static T FieldRequired<T>(Result<T> result, NativeEditorView state)
            => state.NormalSeed ? SeedFieldRequired(result, state) : RelationshipRequired(result, state);
        static T RelationshipRequired<T>(Result<T> result, NativeEditorView state)
        {
            if (!result.IsSuccess && result.Error.Code == ErrorCode.SchemaChanged)
            {
                state.Native?.Dispose(); state.Native = null; state.Ticket = Guid.Empty;
                state.Item = state.Endpoint = null; state.EndpointCandidates = [];
            }
            return Require(result);
        }
        app.MapPost("/api/v1/views/{id:guid}/authoring/review", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<AuthoringReviewBody>(http);
            var build = typeof(NativeBenchHost).Assembly.GetName().Version!.ToString()
                + ":" + typeof(NativeBenchHost).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
                    .SingleOrDefault(value => value.Key == "SourceRevision")?.Value;
            if (build.EndsWith(':')) throw new HttpFailure(503, "BuildIdentityRequired", "Use the qualified paired CLI/panel source builds before the one-time safety check.");
            session.Editor.Upgrade = Require(await NativeAuthoringReview.ReviewAsync(Context(session).Root,
                body.ObservedReadonlyRelease, body.ObservedCliBuild, build, http.RequestAborted));
            await Write(http, new AuthoringReviewDto(session.Editor.Upgrade.Digest, session.Editor.Upgrade.Confirmation, NativeAuthoringReview.NativeBuild));
        });
        app.MapPost("/api/v1/views/{id:guid}/authoring/confirm", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<AuthoringConfirmBody>(http);
            var review = session.Editor.Upgrade ?? throw new HttpFailure(409, "ReviewRequired", "Review the actual native inventory first.");
            _ = Require(await review.ConfirmAsync(body.Digest, body.Confirmation, body.CompleteInventory, body.OldProcessesQuiesced, http.RequestAborted));
            session.Editor.Upgrade = null;
            await Write(http, new Ack());
        });
        app.MapPost("/api/v1/views/{id:guid}/editor", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<EditorOpenBody>(http);
            CancelEditor(session);
            var state = session.Editor;
            CancellationToken navigation;
            lock (state) navigation = state.Navigation.Token;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, navigation);
            await state.Gate.WaitAsync(lifetime.Token);
            try
            {
                state.Retired = false;
                NativePanelContext source;
                lock (session) source = session.DisplayContexts.Values.SingleOrDefault(value => value.Source.Id == body.SourceId)
                    ?? throw new HttpFailure(409, "ContextRequired", "Choose this item's existing native connection first.");
                var plan = Require(await panel.ReadPlanAsync(body.BenchId, lifetime.Token));
                if (plan.Enabled != body.PlanMode)
                    throw new HttpFailure(409, "Conflict", "The Bench mode changed. Your unsaved values were not moved to another mode.");
                if (source.Root != Context(session).Root && !plan.Enabled)
                    throw new HttpFailure(422, "Unsupported", "Select this item's connection as the Bench connection before editing.");
                var capture = Require(await panel.CaptureAsync(source, lifetime.Token, cachedGuidance: true));
                lifetime.Token.ThrowIfCancellationRequested();
                var key = capture.Observation.Snapshot.Items.Select(value => value.Key).SingleOrDefault(value =>
                    ViewMapping.Key(new(source.Source.Id, value)) == body.ItemId);
                if (key is null && plan.Enabled && plan.Sources.SingleOrDefault(value => value.Source == source.Stamp
                    && value.OriginRoot == source.Root) is { } scope)
                    key = Require(await panel.ReadPlanDraftsAsync(plan, source, scope.Id, lifetime.Token)).Select(draft => draft.Key)
                        .SingleOrDefault(value => ViewMapping.Key(new(source.Source.Id, value)) == body.ItemId);
                var form = Require(await panel.EditorOperationAsync(source, async ct =>
                {
                    if (Require(await panel.ReadSelectionAsync(Context(session), ct)).SelectedBenchId != body.BenchId)
                        throw new HttpFailure(409, "Conflict", "The selected Bench changed. Reopen its item before editing.");
                    // Cached key guidance cannot renew an older editor's metadata authority.
                    if (source.Source.AllowOffline || state.Native is null || state.Context != source || state.Bench != body.BenchId
                        || state.Native.PlanMode != plan.Enabled || state.Native.PlanId != plan.PlanId
                        || state.Native.PlanRevision != plan.Revision)
                    {
                        state.Native?.Dispose(); state.Native = null;
                        state.Native = Require(await panel.OpenExistingEditorAsync(source, body.BenchId, ct));
                        state.Context = source; state.Bench = body.BenchId;
                    }
                    if (key is null && !plan.Enabled && source.Stamp.Connection.Provider.Value == "ado")
                        key = Require(await state.Native.ReadOwnedSeedsAsync(ct))
                            .Where(seed => seed.Ready && seed.Draft is not null && seed.Publication is null)
                            .Select(seed => seed.Item).SingleOrDefault(value => ViewMapping.Key(new(source.Source.Id, value)) == body.ItemId);
                    if (key is null) throw new HttpFailure(404, "NotFound", "The exact displayed item or editable draft is unavailable.");
                    return Require(await state.Native.SelectAsync(key, ct));
                }, lifetime.Token));
                lifetime.Token.ThrowIfCancellationRequested();
                lock (state) { state.Ticket = form.Ticket; state.Type = form.Type; state.NormalSeed = form.Item.IsLocal && !plan.Enabled;
                    state.Item = form.Item; state.Endpoint = null;
                    state.EndpointCandidates = capture.Observation.Snapshot.Items.Select(item => item.Key).Where(item => !item.IsLocal).ToImmutableArray(); }
                await Write(http, BoundEditorForm(form, source, state.Native!));
            }
            finally
            {
                if (state.Retired) { state.Native?.Dispose(); state.Native = null; }
                state.Gate.Release();
            }
        });
        app.MapPost("/api/v1/views/{id:guid}/editor/evaluate", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<EditorChangeBody>(http);
            await Edit(http, session, body.Ticket, async (editor, ct) =>
                await Write(http, BoundEditorForm(FieldRequired(await editor.EvaluateAsync(body.Ticket, EditFields(body), ct), session.Editor),
                    session.Editor.Context!, editor)));
        });
        app.MapPost("/api/v1/views/{id:guid}/editor/stage", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<EditorChangeBody>(http);
            await Edit(http, session, body.Ticket, async (editor, ct) =>
            {
                var staged = FieldRequired(await editor.StageAsync(body.Ticket, EditFields(body), ct), session.Editor);
                session.Plan.Invalidate();
                await Write(http, BoundEditorForm(staged, session.Editor.Context!, editor));
            });
        });
        app.MapPost("/api/v1/views/{id:guid}/editor/save", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<EditorChangeBody>(http);
            await Edit(http, session, body.Ticket, async (editor, ct) =>
            {
                var saved = await editor.SaveAsync(body.Ticket, EditFields(body), ct);
                if (!saved.IsSuccess && saved.Error.Code == ErrorCode.SchemaChanged)
                {
                    editor.Dispose(); session.Editor.Native = null; session.Editor.Ticket = Guid.Empty;
                }
                var result = FieldRequired(saved, session.Editor);
                if (result.SeedPublication is not null && result.Error?.Code == ErrorCode.Conflict)
                    using (SeedEditor(await panel.OpenEditorAsync(session.Editor.Context!, session.Editor.Bench, ct))) { }
                SeedDenial(result.Error);
                var response = new EditorSavedDto(result.Verified, result.DraftRetained, result.Error?.Message)
                { SeedPublication = result.SeedPublication is null ? null : SeedPublication(result.SeedPublication, session.Editor.Context!) };
                if (result.SeedPublication is not null)
                {
                    editor.Dispose(); session.Editor.Native = null; session.Editor.Ticket = Guid.Empty;
                    session.Plan.Invalidate();
                }
                await Write(http, response);
            });
        });
        app.MapPost("/api/v1/views/{id:guid}/editor/choices", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<EditorChoiceBody>(http);
            await Edit(http, session, body.Ticket, async (editor, ct) =>
            {
                var choices = FieldRequired(await editor.ChoicesAsync(session.Editor.Type!, body.Field, body.Query, ct), session.Editor);
                await Write(http, choices.Items.Select(value => new EditorChoiceDto(ViewMapping.Value(value.Value), value.Label)).ToArray());
            });
        });
        app.MapPost("/api/v1/views/{id:guid}/editor/relationships", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<RelationshipOpenBody>(http);
            if (body.EndpointId.Length is 0 or > 128)
                throw new HttpFailure(400, "InvalidInput", "Choose one bounded explicit endpoint identity.");
            await Edit(http, session, body.Ticket, async (editor, ct) =>
            {
                var state = session.Editor;
                var source = state.Context!;
                var epoch = state.Epoch;
                var profile = session.ProfileGeneration;
                if (editor.PlanMode || state.NormalSeed || source != Context(session)
                    || source.Stamp.Connection.Provider.Value is not ("ado" or "github" or "twig") || state.Item is not { IsLocal: false } target)
                    throw new HttpFailure(422, "Unsupported", "Select two distinct published items in the normal viewing connection.");
                var endpoint = state.EndpointCandidates.SingleOrDefault(key =>
                    !key.IsLocal && key != target && ViewMapping.Key(new(source.Source.Id, key)) == body.EndpointId)
                    ?? throw new HttpFailure(404, "NotFound", "The explicit published endpoint is unavailable in this source.");
                var form = RelationshipRequired(await editor.SelectRelationshipsAsync(target, endpoint, ct), state);
                if (source.Stamp.Connection.Provider.Value == "github" && (!form.LocalOnly
                    || form.Relations.Length != 1 || form.Relations[0].Shape != RelationShape.Association
                    || form.Relations[0].Directed || form.Fields.Evaluation.Fields.Any(field => !field.ReadOnly)))
                    throw new HttpFailure(422, "Unsupported", "The native source did not qualify a readonly LOCAL-only Association form.");
                if (form.LocalOnly)
                {
                    var admitted = await panel.ReadEditorSourceAsync(source, ct);
                    if (!admitted.IsSuccess && (admitted.Error.Evidence.Contains("http:401") || admitted.Error.Evidence.Contains("http:403")))
                        throw new HttpFailure(403, "PolicyViolation", "This source cannot release LOCAL pair content.");
                    _ = Require(admitted);
                    _ = await authorize(http, id);
                    var mode = Require(await panel.ReadPlanAsync(state.Bench, ct));
                    if (mode.Enabled || mode.PlanId != editor.PlanId || mode.Revision != editor.PlanRevision
                        || Require(await panel.ReadSelectionAsync(source, ct)).SelectedBenchId != state.Bench)
                        throw new HttpFailure(409, "Conflict", "The LOCAL Bench or mode changed during pair qualification.");
                }
                ct.ThrowIfCancellationRequested();
                lock (state)
                {
                    if (state.Ticket != body.Ticket || state.Epoch != epoch || session.Revoked
                        || session.ProfileGeneration != profile || source != Context(session))
                        throw new HttpFailure(409, "Conflict", "The original item ticket changed during endpoint selection.");
                    state.Ticket = form.Fields.Ticket; state.Endpoint = endpoint; state.LocalOnly = form.LocalOnly;
                }
                await Write(http, BoundRelationshipForm(form, source, editor));
            });
        });
        app.MapPost("/api/v1/views/{id:guid}/editor/relationships/stage", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<RelationshipChangeBody>(http);
            await Edit(http, session, body.Ticket, async (editor, ct) =>
            {
                if (session.Editor.LocalOnly || session.Editor.Context!.Stamp.Connection.Provider.Value != "ado")
                    throw new HttpFailure(422, "Unsupported", "ADO relationship Stage cannot adopt LOCAL GitHub effects.");
                var form = RelationshipRequired(await editor.StageRelationshipsAsync(body.Ticket,
                    EditFields(new(body.Ticket, body.Fields)), RelationshipChanges(body, session.Editor), ct), session.Editor);
                ct.ThrowIfCancellationRequested();
                session.Plan.Invalidate();
                await Write(http, BoundRelationshipForm(form, session.Editor.Context!, editor));
            });
        });
        app.MapPost("/api/v1/views/{id:guid}/editor/relationships/save", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<RelationshipChangeBody>(http);
            await Edit(http, session, body.Ticket, async (editor, ct) =>
            {
                if (session.Editor.LocalOnly || session.Editor.Context!.Stamp.Connection.Provider.Value != "ado")
                    throw new HttpFailure(422, "Unsupported", "ADO relationship Save cannot adopt LOCAL GitHub effects.");
                var saved = RelationshipRequired(await editor.SaveRelationshipsAsync(body.Ticket,
                    EditFields(new(body.Ticket, body.Fields)), RelationshipChanges(body, session.Editor), ct), session.Editor);
                if (saved.Error?.Code == ErrorCode.PolicyViolation)
                    throw new HttpFailure(403, "PolicyViolation", "This source cannot release relationship editor content.");
                var message = saved.Verified && !saved.DraftRetained ? saved.Error?.Message
                    : "The original native relationship outcome is protected. Native reconciliation is required; do not restage or retry. " + saved.Error?.Message;
                session.Editor.Ticket = Guid.Empty;
                session.Plan.Invalidate();
                await Write(http, new EditorSavedDto(saved.Verified, saved.DraftRetained, message));
            });
        });
        app.MapPost("/api/v1/views/{id:guid}/editor/close", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<EditorCloseBody>(http);
            lock (session.Editor)
                if (body.Ticket == session.Editor.Ticket) CancelEditor(session);
            await Write(http, new Ack());
        });
        MapLocalRelationships(app, panel, updates, authorize, Edit);

        async Task Edit(HttpContext http, ViewSession session, Guid ticket,
            Func<NativeItemEditor, CancellationToken, Task> action)
        {
            var state = session.Editor;
            CancellationToken navigation;
            var profile = session.ProfileGeneration;
            var epoch = state.Epoch;
            var context = state.Context;
            lock (state)
            {
                if (ticket == Guid.Empty || state.Ticket != ticket || state.Native is null || state.Context is null)
                    throw new HttpFailure(409, "Conflict", "The selected editor item changed. Your unsaved values were not published.");
                navigation = state.Navigation.Token;
            }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, navigation);
            await state.Gate.WaitAsync(lifetime.Token);
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                lock (state)
                    if (state.Ticket != ticket || state.Native is null || state.Context != context || state.Epoch != epoch
                        || session.Revoked || session.ProfileGeneration != profile)
                        throw new HttpFailure(409, "Conflict", "The exact field ticket was retired while this request waited. Observe the original creation; nothing was replayed.");
                _ = Require(await panel.EditorOperationAsync(state.Context!, async ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    lock (state)
                        if (state.Ticket != ticket || state.Native is null || state.Context != context || state.Epoch != epoch
                            || session.Revoked || session.ProfileGeneration != profile)
                            throw new HttpFailure(409, "Conflict", "The original editor ticket was retired during source admission. Nothing was replayed.");
                    var selected = Require(await panel.ReadSelectionAsync(Context(session), ct));
                    if (selected.SelectedBenchId != state.Bench)
                        throw new HttpFailure(409, "Conflict", "The Bench selection changed. Reopen the exact item before saving.");
                    await action(state.Native!, ct);
                    return true;
                }, lifetime.Token));
            }
            catch (HttpFailure failure) when (failure.Code == "PolicyViolation")
            {
                state.Invalidate();
                state.Native?.Dispose(); state.Native = null;
                throw;
            }
            finally
            {
                if (state.Retired) { state.Native?.Dispose(); state.Native = null; }
                state.Gate.Release();
            }
        }
    }

    private static RelationshipIntentDto RelationshipIntent(CreationLink link, NativePanelContext source)
        => new(ViewMapping.Key(new(source.Source.Id, link.Other)), link.Relation, link.Outgoing, link.Remove);

    private static RelationshipFormDto BoundRelationshipForm(NativeRelationshipForm form, NativePanelContext source, NativeItemEditor editor)
    {
        return new(BoundEditorForm(form.Fields, source, editor), RelationshipEndpoint(form.Target, source), RelationshipEndpoint(form.Endpoint, source),
            form.Relations.Select(kind => new RelationshipKindDto(kind.Reference, kind.Shape.ToString(), kind.Directed)).ToArray(),
            form.Choices.Select(link => RelationshipIntent(link, source)).ToArray(),
            form.Intent.Select(link => RelationshipIntent(link, source)).ToArray(),
            form.Incident.Select(edge => new EdgeDto(ViewMapping.Key(new(source.Source.Id, edge.From)),
                ViewMapping.Key(new(source.Source.Id, edge.To)), edge.Kind, edge.Deleted, false, edge.Origin.ToString())
                { LocalVersion = edge.Origin == RelationshipOrigin.Local ? edge.LocalVersion : null }).ToArray())
        { LocalOnly = form.LocalOnly, Verification = form.Verification };
    }

    private static RelationshipEndpointDto RelationshipEndpoint(WorkItem item, NativePanelContext source)
    {
        var locator = item.Key.NativeId ?? item.Key.LocalId.ToString("D");
        if (source.Stamp.Connection.Provider.Value == "github")
        {
            if (!item.Fields.TryGetValue(Twig.Providers.GitHub.GitHubProvider.Number, out var number)
                || number.Kind != FieldKind.Integer || number.Number <= 0 || number.Number > int.MaxValue)
                throw new HttpFailure(409, "Conflict", "The native published issue number is unavailable.");
            locator = number.Number.ToString(CultureInfo.InvariantCulture);
        }
        return new(ViewMapping.Key(new(source.Source.Id, item.Key)), locator, item.Type, item.Revision!.Token,
            item.Fields.Select(field => new FieldDto(field.Key, ViewMapping.Value(field.Value))).ToArray());
    }

    private static ImmutableArray<CreationLink> RelationshipChanges(RelationshipChangeBody body, NativeEditorView state)
    {
        var endpoint = state.Endpoint ?? throw new HttpFailure(409, "Conflict", "Select both explicit endpoints before staging or saving.");
        var id = ViewMapping.Key(new(state.Context!.Source.Id, endpoint));
        if (body.Intent.Length is 0 or > 128 || body.Intent.Any(link => link is null || link.EndpointId != id
            || link.Relation.Length is 0 or > 256))
            throw new HttpFailure(400, "InvalidInput", "Supply bounded explicit relationship intent for this exact endpoint; empty intent cannot discard native debt.");
        return body.Intent.Select(link => new CreationLink(endpoint, link.Relation, link.Outgoing, link.Remove)).ToImmutableArray();
    }

    private static EditorFormDto EditorForm(NativeEditForm form, string authority) => new(form.Ticket, authority, form.Type, form.StateField,
        form.Evaluation.Fields.Select(field => new EditorFieldDto(field.Definition.Reference, field.Definition.Kind.ToString(),
            field.Definition.Name ?? field.Definition.Reference, field.ReadOnly, field.Required, field.HasAllowedConstraint,
            field.Allowed.Select(ViewMapping.Value).ToArray(), ViewMapping.Value(form.Values.TryGetValue(field.Definition.Reference,
                out var value) ? value : FieldValue.Null))).ToArray(), form.Evaluation.States.ToArray(),
        form.Evaluation.Errors.Select(error => error.Message).Distinct().ToArray());

    private static FieldValues EditFields(EditorChangeBody body)
    {
        if (body.Fields.Length > 64 || body.Fields.Any(field => field is null || field.Value is null)
            || body.Fields.Select(field => field.Reference).Distinct().Count() != body.Fields.Length)
            throw new HttpFailure(400, "InvalidInput", "Use a bounded, unique set of native fields.");
        try { return new(body.Fields.Select(field => KeyValuePair.Create(field.Reference, field.Value.Kind switch
        {
            "Null" => FieldValue.Null,
            "Text" => FieldValue.String(field.Value.Value.GetString()!),
            "Html" => FieldValue.RichText(field.Value.Value.GetString()!, FieldKind.Html),
            "Markdown" => FieldValue.RichText(field.Value.Value.GetString()!, FieldKind.Markdown),
            "Identity" => FieldValue.Identity(field.Value.Value.GetString()!),
            "Boolean" => FieldValue.Bool(field.Value.Value.GetBoolean()),
            "Integer" => FieldValue.Integer(long.Parse(field.Value.Value.GetString()!, CultureInfo.InvariantCulture)),
            "Decimal" => FieldValue.Decimal(decimal.Parse(field.Value.Value.GetString()!, CultureInfo.InvariantCulture)),
            "Instant" => FieldValue.At(DateTimeOffset.Parse(field.Value.Value.GetString()!, CultureInfo.InvariantCulture)),
            "StringList" => FieldValue.Strings(field.Value.Value.EnumerateArray().Select(value => value.GetString()!)),
            _ => throw new HttpFailure(422, "Unsupported", "This native field kind is read-only in the initial editor.")
        }))); }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidOperationException)
        { throw new HttpFailure(400, "InvalidInput", "An edited field does not match its native value type."); }
    }
}
