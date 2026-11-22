using System.Text.Json;
using Twig.Infrastructure.Services;
using static Twig.Server.BenchHost;

namespace Twig.Server;

public static partial class NativeBenchHost
{
    private static EditorFormDto CaptureInputs(ViewSessions sessions, ViewSession session, EditorFormDto form,
        RelationshipFormDto? pair = null)
    {
        var state = session.Editor;
        var source = state.Context!;
        var binding = form.Binding!;
        var identity = new EditorInputIdentity(source.Source.Id, source.Root,
            Bench.Domain.CatalogFingerprint.Binding(source.Source.Binding), session.RepositoryRoot, state.Bench, form.ItemId!,
            pair is null ? state.NormalSeed ? "normal-seed" : "scalar" : pair.LocalOnly ? "local-relationship" : "relationships",
            binding.StoreInstance, binding.PlanMode, binding.PlanMode ? binding.PlanId : Guid.Empty, binding.ScopeId, pair?.Endpoint.ItemId);
        // Tickets and restored values are deliberately absent from durable baseline evidence.
        var baselineForm = form with { Ticket = Guid.Empty, Inputs = null };
        var baseline = pair is null ? JsonSerializer.Serialize(baselineForm, TransportJson.Default.EditorFormDto)
            : JsonSerializer.Serialize(pair with { Fields = baselineForm }, TransportJson.Default.RelationshipFormDto);
        var capture = new EditorInputCapture(identity, EditorInputCapture.InputDigest(baseline), form, pair);
        state.Inputs = capture;
        return form with { Inputs = sessions.ReadInputs(session, capture) };
    }

    private static RelationshipFormDto CaptureRelationshipInputs(ViewSessions sessions, ViewSession session, RelationshipFormDto pair)
        => pair with { Fields = CaptureInputs(sessions, session, pair.Fields, pair) };

    private static void MapEditorInputs(WebApplication app, ViewSessions sessions,
        Func<HttpContext, Guid, Task<ViewSession>> authorize,
        Func<HttpContext, ViewSession, Guid, Func<NativeItemEditor, CancellationToken, Task>, Task> edit)
    {
        static EditorInputCapture Capture(ViewSession session, string key)
            => session.Editor.Inputs is { } capture && capture.Key == key ? capture
                : throw new HttpFailure(409, "InputCaptureRequired", "Open a fresh exact native editor before accessing its private inputs.");

        app.MapPost("/api/v1/views/{id:guid}/editor/inputs/observe", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<EditorInputObserveBody>(http);
            await edit(http, session, body.Ticket, async (editor, ct) =>
            {
                _ = await authorize(http, id);
                ct.ThrowIfCancellationRequested();
                await Write(http, sessions.ReadInputs(session, Capture(session, body.Key)));
            });
        });
        app.MapPut("/api/v1/views/{id:guid}/editor/inputs", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<EditorInputWriteBody>(http, 256 * 1024 + 16384);
            if (body.Values is null) throw new HttpFailure(400, "InvalidInput", "Use explicit discard for private inputs.");
            await edit(http, session, body.Ticket, async (editor, ct) =>
            {
                _ = await authorize(http, id);
                ct.ThrowIfCancellationRequested();
                await Write(http, sessions.WriteInputs(session, Capture(session, body.Key), body.ExpectedRevision, body.WriteId, body.Values));
            });
        });
        app.MapPost("/api/v1/views/{id:guid}/editor/inputs/discard", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<EditorInputDiscardBody>(http);
            await edit(http, session, body.Ticket, async (editor, ct) =>
            {
                _ = await authorize(http, id);
                ct.ThrowIfCancellationRequested();
                await Write(http, sessions.WriteInputs(session, Capture(session, body.Key), body.ExpectedRevision, body.WriteId, null));
            });
        });
    }
}
