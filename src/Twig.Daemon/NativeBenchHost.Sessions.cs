using Bench.Domain;
using Bench.Application;
using Bench.Infrastructure;
using static Bench.Server.NativeTransport;

namespace Bench.Server;

public static partial class NativeBenchHost
{
    public static void MapPersistentSessions(WebApplication app, NativePanel panel, ViewSessions sessions,
        Func<HttpContext, Guid, Task<ViewSession>> authorize, bool windowsSignIn,
        Func<SourceViewDto, SourceViewDto> presentSource)
    {
        if (windowsSignIn)
            app.MapPost("/api/v1/windows-profile", async (HttpContext http) =>
            {
                var body = await Body<OwnerProfileBody>(http);
                var store = Require(await panel.ListAsync(http.RequestAborted)).InstanceId;
                await Write(http, sessions.ResolveOwnerProfile(store, body));
            }).RequireAuthorization("WindowsOwner");
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
            anchor?.Root, population?.Root, ConnectionSummary(anchor), ConnectionSummary(population))
            { RepositoryRoot = session.RepositoryRoot, RepositoryBindings = session.RepositoryBindings,
                RuntimeContext = session.RuntimeContext };
        NativeBenchViewDto? view = null;
        if (state.CurrentBenchId is { } bench && shared.Benches.Any(value => value.Id == bench))
        {
            var captured = Require(await panel.ReadViewAsync(bench, anchor, chosen, ct, observations, AvailableConnections(session)));
            var mapped = ViewMapping.ViewLabeled(captured.View, captured.Labels);
            if (mapped.StoreId != shared.InstanceId || mapped.Revision != shared.Revision)
                throw new HttpFailure(409, "BenchViewChanged", "The shared Benches changed during initial loading.");
            view = new(mapped.StoreId, mapped.Revision, mapped.Id, mapped.Name,
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
            await SynchronizeRepository(panel, sessions, session, ct);
            var before = sessions.ReadState(session);
            var available = AvailableConnections(session);
            var registry = Require(await panel.WorkspacesAsync(ct));
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
            using var concurrency = new SemaphoreSlim(8);
            var restored = await Task.WhenAll(references.Select(async reference =>
            {
                await concurrency.WaitAsync(ct);
                try
                {
                    NativePanelContext? context = null;
                    var state = new NativeSourceRecovery(reference.Root, "unavailable", "Unavailable");
                    var observations = initialObservations is null ? null : new Dictionary<NativePanelContext, SourceObservation>();
                    if (available is not null && !registry.Any(workspace => ViewSession.PathComparer.Equals(workspace.Root, reference.Root)
                        && available.Contains(workspace.Connection)))
                    {
                        state = new(reference.Root, "unavailable", "RepositoryConnectionUnavailable");
                    }
                    else
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        try
                        {
                            var result = await panel.OpenAsync(reference.Root, timeout.Token, reference.Binding, observations);
                            if (!result.IsSuccess) state = new(reference.Root, "unavailable", result.Error.Code.ToString());
                            else if (SourceFingerprint.Binding(result.Value.Source.Binding) != reference.Binding)
                                state = new(reference.Root, "changed", "SourceChanged");
                            else { context = result.Value; state = new(reference.Root, "restored", null); }
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            state = new(reference.Root, "unavailable", "Unavailable");
                        }
                    }
                    return (reference.Root, Context: context, Recovery: state, Observations: observations);
                }
                finally { concurrency.Release(); }
            }));
            foreach (var result in restored)
            {
                recovery[result.Root] = result.Recovery;
                if (result.Context is { } context)
                {
                    opened.Add(result.Root, context);
                    if (initialObservations is not null && result.Observations is not null)
                        foreach (var observation in result.Observations) initialObservations[observation.Key] = observation.Value;
                }
            }
            var shared = Require(await panel.ListAsync(ct));
            var currentBench = before.Choices.BenchId ?? session.RepositoryBindings?.DefaultBench
                ?? (session.RuntimeContext is null && session.RepositoryBindings is null && shared.Benches.Length == 1
                    ? shared.Benches[0].Id : (Guid?)null);
            var resolved = currentBench is { } selected && shared.Benches.Any(value => value.Id == selected)
                ? Require(await panel.ResolveConnectionsAsync(selected, opened.Values, ct, available)).ToArray()
                : [];
            var instance = shared.InstanceId;
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
                    session.NativeContext = resolved.FirstOrDefault();
                    session.PopulationContext = before.Choices.AddRoot is { } add
                        ? resolved.FirstOrDefault(value => ViewSession.PathComparer.Equals(value.Root, add)) : null;
                    session.DisplayContexts.Clear();
                    foreach (var context in resolved)
                        session.DisplayContexts[context.Stamp.Connection] = context;
                    session.SourceRecovery.Clear();
                    foreach (var source in recovery) session.SourceRecovery.Add(source.Key, source.Value);
                    session.CurrentBenchId = currentBench;
                }
            });
        }
        finally { session.RecoveryGate.Release(); }
    }

    private static async Task ResolveBenchConnections(NativePanel panel, ViewSession session, CancellationToken ct)
    {
        Guid? bench;
        NativePanelContext[] previous;
        lock (session) { bench = session.CurrentBenchId; previous = session.DisplayContexts.Values.ToArray(); }
        if (bench is null) return;
        var resolved = Require(await panel.ResolveConnectionsAsync(bench.Value, previous, ct, AvailableConnections(session)));
        lock (session.Agent)
        lock (session)
        {
            if (session.CurrentBenchId != bench)
                throw new HttpFailure(409, "BenchViewChanged", "The selected Bench changed during connection resolution.");
            if (resolved.Length == previous.Length && resolved.All(previous.Contains)) return;
            session.Agent.Invalidate();
            session.Editor.Invalidate();
            session.NativeContext = resolved.FirstOrDefault();
            session.DisplayContexts.Clear();
            foreach (var source in resolved) session.DisplayContexts[source.Stamp.Connection] = source;
            if (session.PopulationContext is { } population && !resolved.Contains(population))
                session.PopulationContext = null;
        }
    }
}
