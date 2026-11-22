using Bench.Application;
using Bench.Infrastructure;
using Twig.Core;
using Twig.Infrastructure.Services;
using static Bench.Server.BenchHost;

namespace Bench.Server;

public static partial class NativeBenchHost
{
    private static T SeedRequired<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new HttpFailure(
        result.Error.Code switch { ErrorCode.InvalidInput => 400, ErrorCode.NotFound => 404,
            ErrorCode.Conflict or ErrorCode.DigestMismatch or ErrorCode.SchemaChanged => 409,
            ErrorCode.PolicyViolation => 403, ErrorCode.Unsupported => 422, _ => 503 },
        result.Error.Evidence.Contains("seed-source-binding-changed") ? "SourceChanged" : result.Error.Code.ToString(), result.Error.Message);

    private static NativeItemEditor SeedEditor(Result<NativeItemEditor> result)
        => SeedRequired(result.IsSuccess || result.Error.Code != ErrorCode.Conflict ? result
            : Result<NativeItemEditor>.Fail(result.Error with { Evidence = result.Error.Evidence.Add("seed-source-binding-changed") }));

    private static T SeedFieldRequired<T>(Result<T> result, NativeEditorView state)
    {
        if (!result.IsSuccess && state.NormalSeed && result.Error.Code == ErrorCode.Conflict)
            throw new HttpFailure(409, "SourceChanged", result.Error.Message);
        return SeedRequired(result);
    }

    private static void SeedDenial(Error? error)
    {
        if (error is not null && error.Code.ToString() is "PolicyViolation" or "ProviderAuthenticationRequired"
            or "ProviderAccessDenied" or "SourceChanged")
            throw new HttpFailure(403, error.Code.ToString(), error.Message);
    }

    private static SeedPublicationDto SeedPublication(NativeSeedPublication publication, NativePanelContext source)
        => new(publication.CaptureId, ViewMapping.Key(new(source.Source.Id, publication.Item)),
            publication.Item.LocalId.ToString("D"), publication.Digest, publication.Status,
            publication.Error is null ? null : new(publication.Error.Code.ToString(), publication.Error.Message));

    private static void MapSeeds(WebApplication app, NativePanel panel, ViewSessions sessions, CatalogChanges updates,
        Func<HttpContext, Guid, Task<ViewSession>> authorize)
    {
        app.MapGet("/api/v1/views/{id:guid}/benches/{bench:guid}/seeds",
            (HttpContext http, Guid id, Guid bench) => Read(http, id, bench, null));
        app.MapGet("/api/v1/views/{id:guid}/benches/{bench:guid}/sources/{sourceId:guid}/seeds",
            (HttpContext http, Guid id, Guid bench, Guid sourceId) => Read(http, id, bench, sourceId));

        async Task Read(HttpContext http, Guid id, Guid bench, Guid? sourceId)
        {
            var session = await authorize(http, id);
            await Bound(http, session, sourceId, bench, async (source, ct) =>
            {
                var seeds = SeedRequired(await panel.ReadSeedsAsync(source, bench, ct));
                foreach (var seed in seeds.Seeds) SeedDenial(seed.Publication?.Error);
                var definition = new Bench.Domain.BenchDefinition(bench,
                    seeds.Capture.Definitions.Benches.Single(value => value.Id == bench).Name,
                    seeds.Capture.Definitions.Benches.Single(value => value.Id == bench).Revision, []);
                var mapped = ViewMapping.ViewLabeled(new(definition, seeds.Capture.Definitions.InstanceId,
                    seeds.Capture.Definitions.Revision, [seeds.View]), new Dictionary<Guid, string> { [source.Source.Id] = source.Source.Label })
                    .Sources.Single();
                var active = seeds.Seeds.Where(value => value.Ready && value.Draft is not null && value.Publication is null)
                    .Select(value => ViewMapping.Key(new(source.Source.Id, value.Item))).ToHashSet(StringComparer.Ordinal);
                mapped = mapped with { Items = mapped.Items.Select(item => item with { NormalSeed = active.Contains(item.Id) }).ToArray() };
                ct.ThrowIfCancellationRequested();
                await Write(http, new NormalSeedViewDto(mapped, seeds.Capture.Observation.Schema.Types.Where(type => !type.Hidden)
                    .Select(type => new SeedTypeDto(type.Reference, type.Presentation?.Name ?? type.Reference)).ToArray(),
                    seeds.Seeds.Select(seed => new OwnedSeedDto(ViewMapping.Key(new(source.Source.Id, seed.Item)),
                        seed.Item.LocalId.ToString("D"), seed.CaptureId, seed.Ready, seed.Draft is not null,
                        seed.Publication is null ? null : SeedPublication(seed.Publication, source))).ToArray(), source.Stamp.StoreInstance));
            }, useEditor: false);
        }

        app.MapPost("/api/v1/views/{id:guid}/editor/seed", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<NormalSeedCreateBody>(http);
            if (body.Type is not { Length: >= 1 and <= 256 })
                throw new HttpFailure(400, "InvalidInput", "Choose an explicit connected type reference.");
            await Bound(http, session, body.SourceId, body.BenchId, async (source, ct) =>
            {
                var form = SeedRequired(await panel.EditorOperationAsync(source, async token =>
                {
                    var state = session.Editor;
                    var editor = SeedEditor(await panel.OpenEditorAsync(source, body.BenchId, token));
                    try
                    {
                        var created = SeedRequired(await editor.CreateSeedAsync(body.Type, token));
                        token.ThrowIfCancellationRequested();
                        state.Native?.Dispose();
                        state.Native = editor; state.Ticket = Guid.Empty; state.NormalSeed = false;
                        state.Context = source; state.Bench = body.BenchId;
                        return created;
                    }
                    finally { if (!ReferenceEquals(state.Native, editor)) editor.Dispose(); }
                }, ct));
                session.Editor.Ticket = form.Ticket; session.Editor.Type = form.Type; session.Editor.NormalSeed = true;
                session.Editor.Item = form.Item;
                updates.Publish(0);
                ct.ThrowIfCancellationRequested();
                await Write(http, CaptureInputs(sessions, session, BoundEditorForm(form, source, session.Editor.Native!)));
            });
        });
        app.MapPost("/api/v1/views/{id:guid}/seeds/observe", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<SeedReferenceBody>(http);
            await Publication(http, session, body.SourceId, body.BenchId, body.CaptureId, null);
        });
        app.MapPost("/api/v1/views/{id:guid}/seeds/delivery", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<SeedDeliveryBody>(http);
            if (body.Digest.Length != 64 || body.Digest.Any(value => !char.IsAsciiHexDigit(value)))
                throw new HttpFailure(400, "InvalidInput", "Use the exact original native digest.");
            await Publication(http, session, body.SourceId, body.BenchId, body.CaptureId, body.Digest);
        });

        async Task Publication(HttpContext http, ViewSession session, Guid sourceId, Guid bench, Guid capture, string? digest)
        {
            if (capture == Guid.Empty) throw new HttpFailure(400, "InvalidInput", "Use the original native capture.");
            await Bound(http, session, sourceId, bench, async (source, ct) =>
            {
                var result = SeedRequired(await panel.EditorOperationAsync(source, async token =>
                {
                    using var editor = SeedEditor(await panel.OpenEditorAsync(source, bench, token));
                    return SeedRequired(digest is null ? await editor.ObserveSeedAsync(capture, token)
                        : await editor.ContinueSeedDeliveryAsync(capture, digest, token));
                }, ct));
                if (result.Error?.Code == ErrorCode.Conflict)
                    using (SeedEditor(await panel.OpenEditorAsync(source, bench, ct))) { }
                SeedDenial(result.Error);
                session.Plan.Invalidate();
                if (digest is not null) updates.Publish(0);
                ct.ThrowIfCancellationRequested();
                await Write(http, SeedPublication(result, source));
            });
        }

        async Task Bound(HttpContext http, ViewSession session, Guid? sourceId, Guid bench,
            Func<NativePanelContext, CancellationToken, Task> action, bool useEditor = true)
        {
            var state = session.Editor;
            CancellationToken navigation;
            lock (state) navigation = state.Navigation.Token;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, navigation);
            if (useEditor) await state.Gate.WaitAsync(lifetime.Token);
            try
            {
                NativePanelContext source;
                lock (session)
                    source = sourceId is { } id
                        ? session.DisplayContexts.Values.SingleOrDefault(value => value.Source.Id == id)
                            ?? throw new HttpFailure(409, "ContextRequired", "The requested connection is not admitted in this view.")
                        : Context(session);
                if (source.Stamp.Connection.Provider.Value != "ado")
                    throw new HttpFailure(422, "Unsupported", "Item creation requires an admitted Azure DevOps connection.");
                if (SeedRequired(await panel.ReadPlanAsync(bench, lifetime.Token)).Enabled
                    || session.CurrentBenchId != bench)
                    throw new HttpFailure(409, "Conflict", "Choose this exact Bench in normal mode before continuing its item creation.");
                lock (session)
                    if (session.CurrentBenchId != bench || sourceId is not null
                        && !session.DisplayContexts.Values.Contains(source))
                        throw new HttpFailure(409, "ContextRequired", "The Bench or requested connection changed.");
                lifetime.Token.ThrowIfCancellationRequested();
                await action(source, lifetime.Token);
            }
            finally
            {
                if (useEditor)
                {
                    if (state.Retired) { state.Native?.Dispose(); state.Native = null; }
                    state.Gate.Release();
                }
            }
        }
    }
}
