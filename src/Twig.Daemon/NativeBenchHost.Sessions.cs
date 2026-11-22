using Bench.Domain;
using Bench.Application;
using Bench.Infrastructure;
using Twig.Infrastructure.Services;
using static Bench.Server.BenchHost;

namespace Bench.Server;

public static partial class NativeBenchHost
{
    public static void MapPersistentSessions(WebApplication app, NativePanel panel, ViewSessions sessions,
        Func<HttpContext, Guid, Task<ViewSession>> authorize, bool windowsSignIn,
        Func<SourceViewDto, SourceViewDto> presentSource,
        Func<CancellationToken, Task<NativeAadEnrollment[]>>? readAccounts = null)
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
                    ? await InitialView(panel, sessions, session, presentSource, http.RequestAborted, readAccounts) : null;
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
        app.MapGet("/api/v1/views/{id:guid}/workspace", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            budget.CancelAfter(TimeSpan.FromSeconds(30));
            await RestoreViewReferences(panel, sessions, session, budget.Token,
                cachedAdmissionOnly: true, onlyIfNeeded: true);
            var workspace = await Workspace(panel, sessions, session, source =>
            {
                _ = presentSource(source);
                return source;
            }, budget.Token, readAccounts: readAccounts);
            _ = sessions.Content(id, Bearer(http), true);
            await Write(http, workspace);
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
            var initial = await InitialView(panel, sessions, session, presentSource, http.RequestAborted, readAccounts);
            await Write(http, new NativeOwnerSession(id, Bearer(http), true, initial.State) { InitialView = initial.View });
        });
    }

    private static async Task<(NativeViewStateInfo State, NativeInitialView View)> InitialView(
        NativePanel panel, ViewSessions sessions, ViewSession session, Func<SourceViewDto, SourceViewDto> presentSource, CancellationToken ct,
        Func<CancellationToken, Task<NativeAadEnrollment[]>>? readAccounts)
    {
        var observations = new Dictionary<NativePanelContext, SourceObservation>();
        await RestoreViewReferences(panel, sessions, session, ct, observations);
        var workspace = await Workspace(panel, sessions, session, presentSource, ct, observations, readAccounts);
        return (workspace.ViewState, new(workspace.Summary, workspace.Session, workspace.View));
    }

    private static async Task<NativeWorkspaceDto> Workspace(
        NativePanel panel, ViewSessions sessions, ViewSession session, Func<SourceViewDto, SourceViewDto> presentSource,
        CancellationToken ct, IReadOnlyDictionary<NativePanelContext, SourceObservation>? observations = null,
        Func<CancellationToken, Task<NativeAadEnrollment[]>>? readAccounts = null)
    {
        long selectionRevision;
        lock (session) selectionRevision = session.SelectionRevision;
        var state = sessions.ReadState(session);
        var shared = Require(await panel.ListAsync(ct));
        if (shared.InstanceId != state.StoreId)
            throw new HttpFailure(409, "StoreReplaced", "The native Bench store changed during workspace loading.");
        var selectionExists = state.CurrentBenchId is { } selected && shared.Benches.Any(value => value.Id == selected);
        if (selectionExists) await ResolveBenchConnections(panel, session, ct, cachedAdmissionOnly: true);
        NativePanelContext? anchor;
        NativePanelContext? population;
        NativePanelContext[] chosen;
        string? repositoryRoot;
        Twig.Infrastructure.Services.UserRepositoryBindings? repository;
        lock (session)
        {
            anchor = session.NativeContext; population = session.PopulationContext; chosen = session.DisplayContexts.Values.ToArray();
            repositoryRoot = session.RepositoryRoot; repository = session.RepositoryBindings;
        }
        var registry = Require(await panel.WorkspacesAsync(ct));
        var accounts = readAccounts is not null && registry.Any(workspace => workspace.Connection.Provider.Value == "ado")
            ? await readAccounts(ct) : [];
        var labels = new List<WorkspaceDto>();
        var labelBudget = System.Diagnostics.Stopwatch.StartNew();
        foreach (var registered in registry)
        {
            var label = labelBudget.Elapsed < TimeSpan.FromSeconds(2)
                ? await NativeConnectionLabels.ReadAsync(registered.Root, registered.Connection, ct)
                : NativeConnectionLabels.Unavailable(registered.Connection);
            labels.Add(new(registered.Root, registered.Connection.Provider.Value, label.Name, label.Available)
            {
                Connection = registered.Connection,
                Account = NativeConnectionLabels.Account(registered.Connection, accounts),
                Available = repository is null || repository.Connections.Contains(registered.Connection),
            });
        }
        var summary = new NativePanelSummary(shared.InstanceId, shared.Revision, shared.Benches.Select(Definition).ToArray(),
            labels.ToArray(),
            anchor?.Root, population?.Root, ConnectionSummary(anchor), ConnectionSummary(population))
            { RepositoryRoot = repositoryRoot, RepositoryBindings = repository,
                RuntimeContext = session.RuntimeContext };
        NativeBenchViewDto? view = null;
        NativePanelView? captured = null;
        if (state.CurrentBenchId is { } bench && shared.Benches.Any(value => value.Id == bench))
        {
            captured = Require(await panel.ReadViewAsync(bench, anchor, chosen, ct, observations,
                repository?.Connections.ToHashSet()));
            var mapped = ViewMapping.ViewLabeled(captured.View, captured.Labels);
            if (mapped.CatalogId != shared.InstanceId || mapped.CatalogRevision != shared.Revision)
                throw new HttpFailure(409, "BenchViewChanged", "The shared Benches changed during workspace loading.");
            view = new(mapped.CatalogId, mapped.CatalogRevision, mapped.Id, mapped.Name,
                mapped.Sources.Select(source => presentSource(source) with {
                    NormalDraftCount = captured.NormalDraftCounts.GetValueOrDefault(source.SourceId) }).ToArray())
                { Plan = PlanStatus(session, captured.Plan) };
        }
        if (captured is not null)
            _ = Require(await panel.CheckWorkspaceAsync(captured, ct, recheckLocalSnapshot: observations is null));
        if (!registry.SequenceEqual(Require(await panel.WorkspacesAsync(ct))))
            throw new HttpFailure(409, "WorkspaceChanged", "The registered connections changed during workspace loading.");
        if (repositoryRoot is not null && Require(await panel.ReadRepositoryAsync(repositoryRoot, ct)) is { } currentRepository
            && (currentRepository.RepositoryId != repository?.RepositoryId || currentRepository.Revision != repository?.Revision))
            throw new HttpFailure(409, "WorkspaceChanged", "The repository bindings changed during workspace loading.");
        if (Require(await panel.ListAsync(ct)) is { } final
            && (final.InstanceId != shared.InstanceId || final.Revision != shared.Revision))
            throw new HttpFailure(409, "BenchViewChanged", "The shared Benches changed before workspace loading completed.");
        var committed = sessions.CommitRestoration(session, state.Revision, () =>
        {
            lock (session)
                if (session.SelectionRevision != selectionRevision || session.CurrentBenchId != state.CurrentBenchId
                    || session.RepositoryRoot != repositoryRoot || !ReferenceEquals(session.RepositoryBindings, repository)
                    || !ReferenceEquals(session.NativeContext, anchor) || !ReferenceEquals(session.PopulationContext, population)
                    || session.DisplayContexts.Count != chosen.Length
                    || chosen.Any(value => !session.DisplayContexts.TryGetValue(value.Stamp.Connection, out var current)
                        || !ReferenceEquals(current, value)))
                    throw new HttpFailure(409, "ViewStateConflict", "The selection or connection choices changed during workspace loading.");
        });
        Guid? operationId;
        lock (session.Plan)
            operationId = session.Plan.Operation is { } operation && operation.BenchId == state.CurrentBenchId
                ? operation.Id : null;
        return new(committed, summary, new(shared.InstanceId, session.NativeContentBlocked,
            new(committed.Revision, committed.CurrentBenchId, committed.Choices.Layout,
                committed.Choices.GroupSource, committed.Choices.GroupField)), view)
        {
            PlanOperationId = operationId,
            SelectionError = state.CurrentBenchId is not null && !selectionExists
                ? new("NotFound", "The selected Bench was deleted. Choose another Bench; retained drafts and operations were not removed.")
                : null,
        };
    }

    private static async Task<NativeViewStateInfo> RestoreViewReferences(NativePanel panel, ViewSessions sessions,
        ViewSession session, CancellationToken ct,
        IDictionary<NativePanelContext, SourceObservation>? initialObservations = null,
        bool cachedAdmissionOnly = false, bool onlyIfNeeded = false)
    {
        await session.RecoveryGate.WaitAsync(ct);
        try
        {
            await SynchronizeRepository(panel, sessions, session, ct);
            long initialSelectionRevision;
            lock (session) initialSelectionRevision = session.SelectionRevision;
            var before = sessions.ReadState(session);
            if (onlyIfNeeded && session.StartupSelectionResolved && session.ReferencesRestored && session.AppliedChoices is { } applied
                && applied.BenchId == before.Choices.BenchId
                && ViewSession.PathComparer.Equals(applied.ViewingRoot, before.Choices.ViewingRoot)
                && ViewSession.PathComparer.Equals(applied.AddRoot, before.Choices.AddRoot)
                && ViewSession.PathComparer.Equals(applied.RepositoryRoot, before.Choices.RepositoryRoot)
                && applied.DisplayRoots.SequenceEqual(before.Choices.DisplayRoots, ViewSession.PathComparer))
                return before;
            var repository = session.RepositoryBindings;
            var available = repository?.Connections.ToHashSet();
            var cached = cachedAdmissionOnly || repository is not null;
            var registry = Require(await panel.WorkspacesAsync(ct));
            var sameScope = ViewSession.PathComparer.Equals(before.Choices.RepositoryRoot, repository?.RepositoryRoot);
            var retainedRoots = (sameScope ? ViewSessions.Roots(before.Choices) : []).ToHashSet(ViewSession.PathComparer);
            var references = sessions.StateReferences(session).Where(reference => retainedRoots.Contains(reference.Root));
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
                            var result = await panel.OpenAsync(reference.Root, timeout.Token, reference.Binding, observations,
                                cachedAdmissionOnly: cached);
                            if (cachedAdmissionOnly && !result.IsSuccess && result.Error.Code == Twig.Core.ErrorCode.Conflict)
                                throw new HttpFailure(409, "SourceChanged", "A retained source binding changed. Choose the exact intended connection explicitly.");
                            if (!result.IsSuccess) state = new(reference.Root, "unavailable", result.Error.Code.ToString());
                            else if (CatalogFingerprint.Binding(result.Value.Source.Binding) != reference.Binding
                                || (available is not null && (!available.Contains(result.Value.Stamp.Connection)
                                    || !registry.Any(workspace => ViewSession.PathComparer.Equals(workspace.Root, reference.Root)
                                        && workspace.Connection == result.Value.Stamp.Connection
                                        && workspace.StoreInstance == result.Value.Stamp.StoreInstance))))
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
            var retained = sameScope ? before.Choices.BenchId : null;
            var currentBench = before.CurrentBenchId;
            if (!session.StartupSelectionResolved)
                currentBench = RepositoryBench(sessions, session, repository, shared, retained);
            else if (repository is null && session.AppliedChoices is { } previous && previous.BenchId != retained)
                currentBench = retained;
            var preferredViewing = sameScope && before.Choices.ViewingRoot is { } viewingRoot
                ? opened.GetValueOrDefault(viewingRoot) : null;
            var restoredDisplay = preferredViewing is null ? opened.Values.AsEnumerable() : opened.Values
                .Where(source => source.Stamp.Connection != preferredViewing.Stamp.Connection).Append(preferredViewing);
            var resolved = currentBench is { } selected && shared.Benches.Any(value => value.Id == selected)
                ? Require(await panel.ResolveConnectionsAsync(selected, restoredDisplay, ct, available, cached)).ToArray()
                : [];
            var instance = shared.InstanceId;
            if (instance != session.CatalogId)
                throw new HttpFailure(409, "StoreReplaced", "The native Bench store changed during restoration. Connect explicitly.");
            return sessions.CommitRestoration(session, before.Revision, () =>
            {
                lock (session)
                {
                    if (session.SelectionRevision != initialSelectionRevision || !ReferenceEquals(session.RepositoryBindings, repository)
                        || !ReferenceEquals(session.NativeContext, initialViewing)
                        || !ReferenceEquals(session.PopulationContext, initialPopulation)
                        || session.DisplayContexts.Count != initialDisplay.Length
                        || initialDisplay.Any(pair => !session.DisplayContexts.TryGetValue(pair.Key, out var current)
                            || !ReferenceEquals(current, pair.Value)))
                        throw new HttpFailure(409, "ViewStateConflict", "A connection choice changed during restoration. Read the current view before restoring.");
                    session.Editor.Invalidate();
                    session.NativeContext = sameScope && before.Choices.ViewingRoot is { } viewing
                        ? resolved.FirstOrDefault(value => ReferenceEquals(value, opened.GetValueOrDefault(viewing)))
                        : repository is null || resolved.Length == 1 ? resolved.FirstOrDefault() : null;
                    session.PopulationContext = currentBench is not null && before.Choices.AddRoot is { } add
                        ? opened.GetValueOrDefault(add) : null;
                    session.DisplayContexts.Clear();
                    foreach (var context in resolved)
                        session.DisplayContexts[context.Stamp.Connection] = context;
                    session.SourceRecovery.Clear();
                    foreach (var source in recovery) session.SourceRecovery.Add(source.Key, source.Value);
                    session.CurrentBenchId = currentBench;
                    session.StartupSelectionResolved = true;
                    session.ReferencesRestored = true;
                    session.AppliedChoices = before.Choices;
                }
            });
        }
        finally { session.RecoveryGate.Release(); }
    }

    private sealed record SelectionSnapshot(long Revision, long ExplicitRevision, bool StartupResolved, Guid? Bench,
        UserRepositoryBindings? Repository, NativePanelContext? Viewing, NativePanelContext? Add, NativePanelContext[] Display)
    {
        internal bool Matches(SelectionSnapshot other) => Revision == other.Revision && StartupResolved == other.StartupResolved
            && Bench == other.Bench && ReferenceEquals(Repository, other.Repository)
            && ReferenceEquals(Viewing, other.Viewing) && ReferenceEquals(Add, other.Add)
            && Display.Length == other.Display.Length && Display.All(source => other.Display.Any(current => ReferenceEquals(current, source)));
    }

    private static SelectionSnapshot CaptureSelection(ViewSession session)
    {
        lock (session) return new(session.SelectionRevision, session.ExplicitSelectionRevision, session.StartupSelectionResolved,
            session.CurrentBenchId, session.RepositoryBindings, session.NativeContext, session.PopulationContext,
            session.DisplayContexts.Values.ToArray());
    }

    private static HttpFailure SelectionChanged()
        => new(409, "BenchViewChanged", "The selected Bench or connection choices changed during connection resolution.");

    private static async Task<(NativePanelContext? Viewing, NativePanelContext? Add, NativePanelContext[] Display)>
        ResolveSelectionChoices(NativePanel panel, SelectionSnapshot captured, Twig.Core.Bench? bench, HashSet<Twig.Core.Connection>? available,
            CancellationToken ct)
    {
        if (bench is null) return (null, null, []);
        var registry = Require(await panel.WorkspacesAsync(ct));
        async Task<NativePanelContext?> Keep(NativePanelContext? source)
        {
            if (source is null || available?.Contains(source.Stamp.Connection) == false
                || !registry.Any(workspace => ViewSession.PathComparer.Equals(workspace.Root, source.Root)
                    && workspace.Connection == source.Stamp.Connection && workspace.StoreInstance == source.Stamp.StoreInstance))
                return null;
            var exact = await panel.OpenAsync(source.Root, ct, CatalogFingerprint.Binding(source.Source.Binding), cachedAdmissionOnly: true);
            return exact.IsSuccess && exact.Value.Stamp == source.Stamp ? source : null;
        }
        var viewing = captured.Viewing is { } anchor && bench.Connections.Contains(anchor.Stamp.Connection) ? await Keep(anchor) : null;
        var add = viewing is not null && ReferenceEquals(viewing, captured.Add) ? viewing : await Keep(captured.Add);
        var permitted = bench.Connections.Where(connection => !(captured.Viewing?.Stamp.Connection == connection && viewing is null)
            && available?.Contains(connection) != false).ToHashSet();
        var previous = captured.Display.Where(source => permitted.Contains(source.Stamp.Connection)
            && source.Stamp.Connection != viewing?.Stamp.Connection
            && (!ReferenceEquals(source, captured.Add) || add is not null));
        if (viewing is not null) previous = previous.Append(viewing);
        var resolved = Require(await panel.ResolveConnectionsAsync(bench.Id, previous, ct, permitted,
            cachedAdmissionOnly: captured.Repository is not null)).ToArray();
        return (resolved.FirstOrDefault(source => ReferenceEquals(source, viewing)), add, resolved);
    }

    private static async Task ResolveBenchConnections(NativePanel panel, ViewSession session, CancellationToken ct,
        bool cachedAdmissionOnly = false)
    {
        var captured = CaptureSelection(session);
        if (captured.Bench is not { } bench) return;
        var available = captured.Repository?.Connections.ToHashSet();
        var resolved = Require(await panel.ResolveConnectionsAsync(bench, captured.Display, ct, available,
            cachedAdmissionOnly || captured.Repository is not null));
        lock (session.Agent)
        lock (session)
        {
            if (!captured.Matches(CaptureSelection(session)) || session.Revoked) throw SelectionChanged();
            if (resolved.Length == captured.Display.Length && resolved.All(captured.Display.Contains)) return;
            session.Agent.Invalidate();
            session.Editor.Invalidate();
            session.NativeContext = resolved.FirstOrDefault(source => ReferenceEquals(source, captured.Viewing))
                ?? (captured.Repository is null || resolved.Length == 1 ? resolved.FirstOrDefault() : null);
            session.DisplayContexts.Clear();
            foreach (var source in resolved) session.DisplayContexts[source.Stamp.Connection] = source;
        }
    }
}
