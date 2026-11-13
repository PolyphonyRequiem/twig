using Bench.Domain;
using Bench.Application;
using Bench.Infrastructure;
using static Bench.Server.BenchHost;

namespace Bench.Server;

public static partial class NativeBenchHost
{
    public static void MapPersistentSessions(WebApplication app, NativePanel panel, ViewSessions sessions,
        Func<HttpContext, Guid, Task<ViewSession>> authorize, bool windowsSignIn,
        Func<SourceViewDto, SourceViewDto> presentSource)
    {
        if (windowsSignIn)
            app.MapPost("/api/v1/windows-renew", async (HttpContext http) =>
            {
                var body = await Body<OwnerRenewBody>(http);
                var store = Require(await panel.ListAsync(http.RequestAborted)).InstanceId;
                var content = http.Request.Headers["X-Twig-Bench-Content"];
                if (content.Count > 1)
                    throw new HttpFailure(400, "InvalidInput", "Use one existing content capability for renewal.");
                var renewed = sessions.RenewOwner(store, body, content.Count == 1 ? content[0] : null);
                var session = sessions.Content(renewed.Connection.ViewId, renewed.Connection.ContentCapability, true);
                (NativeViewStateInfo State, NativeInitialView View)? initial = renewed.Recovered && body.InitialView
                    ? await InitialView(panel, sessions, session, presentSource, http.RequestAborted) : null;
                var state = initial?.State ?? (renewed.Recovered
                    ? await RestoreViewReferences(panel, sessions, session, http.RequestAborted)
                    : sessions.ReadState(session));
                await Write(http, new NativeOwnerSession(renewed.Connection.ViewId,
                    renewed.Connection.ContentCapability, renewed.Recovered, state) { InitialView = initial?.View });
            }).RequireAuthorization("WindowsOwner");
        app.MapGet("/api/v1/views/{id:guid}/view-state", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            await Write(http, sessions.ReadState(session));
        });
        app.MapPut("/api/v1/views/{id:guid}/view-state", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<NativeViewStateBody>(http, 65536);
            await Write(http, sessions.SaveState(session, body));
        });
        app.MapPost("/api/v1/views/{id:guid}/view-state/restore", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            _ = await Body<EmptyBody>(http);
            await Write(http, await RestoreViewReferences(panel, sessions, session, http.RequestAborted));
        });
        app.MapPost("/api/v1/views/{id:guid}/initial-view", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            _ = await Body<EmptyBody>(http);
            var initial = await InitialView(panel, sessions, session, presentSource, http.RequestAborted);
            await Write(http, new NativeOwnerSession(id, Bearer(http), true, initial.State) { InitialView = initial.View });
        });
    }

    private static async Task<(NativeViewStateInfo State, NativeInitialView View)> InitialView(
        NativePanel panel, ViewSessions sessions, ViewSession session, Func<SourceViewDto, SourceViewDto> presentSource, CancellationToken ct)
    {
        var observations = new Dictionary<NativePanelContext, SourceObservation>();
        var state = await RestoreViewReferences(panel, sessions, session, ct, observations);
        var shared = Require(await panel.ListAsync(ct));
        if (shared.InstanceId != state.StoreId)
            throw new HttpFailure(409, "StoreReplaced", "The native Bench store changed during initial loading.");
        NativePanelContext? anchor;
        NativePanelContext? population;
        NativePanelContext[] chosen;
        lock (session) { anchor = session.NativeContext; population = session.PopulationContext; chosen = session.DisplayContexts.Values.ToArray(); }
        var summary = new NativePanelSummary(shared.InstanceId, shared.Revision, shared.Benches.Select(Definition).ToArray(),
            chosen.Concat(new[] { population }.OfType<NativePanelContext>()).DistinctBy(value => value.Root, ViewSession.PathComparer)
                .Select(value => ConnectionSummary(value)!).ToArray(),
            anchor?.Root, population?.Root, ConnectionSummary(anchor), ConnectionSummary(population));
        NativeBenchViewDto? view = null;
        if (anchor is not null && state.CurrentBenchId is { } bench && shared.Benches.Any(value => value.Id == bench))
        {
            var captured = Require(await panel.ReadViewAsync(bench, anchor, chosen, ct, observations));
            var mapped = ViewMapping.ViewLabeled(captured.View, captured.Labels);
            if (mapped.CatalogId != shared.InstanceId || mapped.CatalogRevision != shared.Revision)
                throw new HttpFailure(409, "BenchViewChanged", "The shared Benches changed during initial loading.");
            view = new(mapped.CatalogId, mapped.CatalogRevision, mapped.Id, mapped.Name,
                mapped.Sources.Select(source => presentSource(source) with {
                    NormalDraftCount = captured.NormalDraftCounts.GetValueOrDefault(source.SourceId) }).ToArray())
                { Plan = PlanStatus(session, captured.Plan) };
        }
        _ = Require(await panel.CheckInitialAsync(observations, ct));
        if (Require(await panel.ListAsync(ct)) is { } final
            && (final.InstanceId != shared.InstanceId || final.Revision != shared.Revision))
            throw new HttpFailure(409, "BenchViewChanged", "The shared Benches changed before initial loading completed.");
        var committed = sessions.CommitRestoration(session, state.Revision, () =>
        {
            lock (session)
                if (!ReferenceEquals(session.NativeContext, anchor) || !ReferenceEquals(session.PopulationContext, population)
                    || session.DisplayContexts.Count != chosen.Length
                    || chosen.Any(value => !session.DisplayContexts.TryGetValue(value.Stamp.Connection, out var current)
                        || !ReferenceEquals(current, value)))
                    throw new HttpFailure(409, "ViewStateConflict", "The connection choices changed during initial loading.");
        });
        return (committed, new(summary, new(shared.InstanceId, session.NativeContentBlocked,
            session.Preferences with { BenchId = state.CurrentBenchId }), view));
    }

    private static async Task<NativeViewStateInfo> RestoreViewReferences(NativePanel panel, ViewSessions sessions,
        ViewSession session, CancellationToken ct,
        IDictionary<NativePanelContext, SourceObservation>? initialObservations = null)
    {
        await session.RecoveryGate.WaitAsync(ct);
        try
        {
            var before = sessions.ReadState(session);
            var references = sessions.StateReferences(session);
            NativePanelContext? initialViewing;
            NativePanelContext? initialPopulation;
            KeyValuePair<Twig.Core.Connection, NativePanelContext>[] initialDisplay;
            lock (session)
            {
                initialViewing = session.NativeContext;
                initialPopulation = session.PopulationContext;
                initialDisplay = session.DisplayContexts.ToArray();
            }
            var opened = new Dictionary<string, NativePanelContext>(ViewSession.PathComparer);
            var recovery = new Dictionary<string, NativeSourceRecovery>(ViewSession.PathComparer);
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overall.CancelAfter(TimeSpan.FromSeconds(30));
            foreach (var reference in references)
            {
                ct.ThrowIfCancellationRequested();
                if (overall.IsCancellationRequested)
                {
                    recovery[reference.Root] = new(reference.Root, "unavailable", "Unavailable");
                    continue;
                }
                try
                {
                    var result = await panel.OpenAsync(reference.Root, overall.Token, reference.Binding, initialObservations);
                    if (!result.IsSuccess)
                    {
                        recovery[reference.Root] = new(reference.Root, "unavailable", result.Error.Code.ToString());
                        continue;
                    }
                    var context = result.Value;
                    if (CatalogFingerprint.Binding(context.Source.Binding) != reference.Binding)
                    {
                        recovery[reference.Root] = new(reference.Root, "changed", "SourceChanged");
                        continue;
                    }
                    opened.Add(reference.Root, context);
                    recovery[reference.Root] = new(reference.Root, "restored", null);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    recovery[reference.Root] = new(reference.Root, "unavailable", "Unavailable");
                }
            }
            Guid? currentBench = null;
            if (before.Choices.ViewingRoot is { } viewing && opened.TryGetValue(viewing, out var anchor))
            {
                try
                {
                    if (initialObservations is not null && initialObservations.TryGetValue(anchor, out var initial))
                        currentBench = initial.SelectedBenchId;
                    else
                    {
                        var selection = await panel.ReadSelectionAsync(anchor, overall.Token);
                        if (selection.IsSuccess) currentBench = selection.Value.SelectedBenchId;
                        else
                        {
                            opened.Remove(viewing);
                            recovery[viewing] = new(viewing, "unavailable", selection.Error.Code.ToString());
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    opened.Remove(viewing);
                    recovery[viewing] = new(viewing, "unavailable", "Unavailable");
                }
            }
            var instance = Require(await panel.ListAsync(ct)).InstanceId;
            if (instance != session.CatalogId)
                throw new HttpFailure(409, "StoreReplaced", "The native Bench store changed during restoration. Connect explicitly.");
            return sessions.CommitRestoration(session, before.Revision, () =>
            {
                lock (session)
                {
                    if (!ReferenceEquals(session.NativeContext, initialViewing)
                        || !ReferenceEquals(session.PopulationContext, initialPopulation)
                        || session.DisplayContexts.Count != initialDisplay.Length
                        || initialDisplay.Any(pair => !session.DisplayContexts.TryGetValue(pair.Key, out var current)
                            || !ReferenceEquals(current, pair.Value)))
                        throw new HttpFailure(409, "ViewStateConflict", "A connection choice changed during restoration. Read the current view before restoring.");
                    session.Editor.Invalidate();
                    session.NativeContext = before.Choices.ViewingRoot is { } viewingRoot ? opened.GetValueOrDefault(viewingRoot) : null;
                    session.PopulationContext = before.Choices.AddRoot is { } add ? opened.GetValueOrDefault(add) : null;
                    session.DisplayContexts.Clear();
                    foreach (var root in before.Choices.DisplayRoots)
                        if (opened.TryGetValue(root, out var context))
                            session.DisplayContexts[context.Stamp.Connection] = context;
                    if (session.NativeContext is { } contextRoot)
                        session.DisplayContexts[contextRoot.Stamp.Connection] = contextRoot;
                    if (session.PopulationContext is { } population)
                        session.DisplayContexts[population.Stamp.Connection] = population;
                    session.SourceRecovery.Clear();
                    foreach (var source in recovery) session.SourceRecovery.Add(source.Key, source.Value);
                    session.CurrentBenchId = currentBench;
                }
            });
        }
        finally { session.RecoveryGate.Release(); }
    }
}
