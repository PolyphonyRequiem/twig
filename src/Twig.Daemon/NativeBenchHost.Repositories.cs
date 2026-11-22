using Bench.Application;
using Bench.Infrastructure;
using Twig.Core;
using Twig.Infrastructure.Services;
using System.Text.Json.Serialization;
using static Bench.Server.BenchHost;

namespace Bench.Server;

public sealed record RepositoryContextBody(string? Root);
public sealed record ReadRepositoryBindingsBody(string Root);
public sealed record RepositoryBindingsBody(string Root, long ExpectedRevision, Connection[] Connections, Guid? DefaultBench);
public sealed record AttachBenchConnectionBody(long ExpectedRevision, Connection Connection);

public static partial class NativeBenchHost
{
    private static async Task ApplyRuntimeRepository(NativePanel panel, ViewSession session, CancellationToken ct)
    {
        if (session.RuntimeRepositoryApplied || session.RuntimeContext is not { } runtime) return;
        if (runtime.IsRemote == true)
            throw new HttpFailure(422, "RemoteContextUnsupported", "This daemon supports same-machine session contexts only. A remote working directory was not mapped to local data.");
        if (runtime.WorkingDirectory is not { } root) { session.RuntimeRepositoryApplied = true; return; }
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new HttpFailure(409, "SessionDirectoryUnavailable", "The SDK session directory is unavailable on this machine. No alternate repository or profile was used.");
        var observed = await panel.ReadRepositoryAsync(root, ct);
        if (!observed.IsSuccess && observed.Error.Code != ErrorCode.InvalidInput) _ = Require(observed);
        session.RuntimeRepositoryRoot = observed.IsSuccess ? observed.Value.RepositoryRoot : null;
        session.RuntimeRepositoryApplied = true;
    }

    private static Guid? RepositoryBench(ViewSessions sessions, ViewSession session,
        UserRepositoryBindings? bindings, UserBenchSnapshot catalogue, Guid? retained)
    {
        bool Exists(Guid? id) => id is { } candidate && catalogue.Benches.Any(bench => bench.Id == candidate);
        if (bindings is null) return Exists(retained) ? retained : null;
        var remembered = sessions.ReadRepositoryBench(session, bindings.RepositoryId);
        if (Exists(remembered)) return remembered;
        return Exists(bindings.DefaultBench) ? bindings.DefaultBench : null;
    }

    private static HashSet<Connection>? AvailableConnections(ViewSession session)
        => session.RepositoryBindings?.Connections.ToHashSet();

    private static void RequireAvailable(ViewSession session, Connection connection)
    {
        if (session.RepositoryBindings is { } bindings && !bindings.Connections.Contains(connection))
            throw new HttpFailure(403, "RepositoryConnectionUnavailable",
                "Assign this registered connection in this user's repository bindings first. Existing Bench rules, drafts and pending operations were not retargeted.");
    }

    private static async Task RequireAvailableRoot(NativePanel panel, ViewSession session, string root, CancellationToken ct)
    {
        if (session.RepositoryBindings is not { } bindings) return;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var registry = Require(await panel.WorkspacesAsync(ct));
        if (!registry.Any(workspace => ViewSession.PathComparer.Equals(workspace.Root, path)
            && bindings.Connections.Contains(workspace.Connection)))
            throw new HttpFailure(403, "RepositoryConnectionUnavailable",
                "Choose a registered connection assigned to this repository. No source was opened.");
    }

    private static async Task SynchronizeRepository(NativePanel panel, ViewSessions sessions, ViewSession session, CancellationToken ct)
    {
        await session.RepositoryGate.WaitAsync(ct);
        try { await SynchronizeRepositoryLocked(panel, sessions, session, ct); }
        finally { session.RepositoryGate.Release(); }
    }

    private static async Task SynchronizeRepositoryLocked(NativePanel panel, ViewSessions sessions, ViewSession session, CancellationToken ct)
    {
        await ApplyRuntimeRepository(panel, session, ct);
        var root = sessions.ReadState(session).Choices.RepositoryRoot;
        var next = root is null ? null : Require(await panel.ReadRepositoryAsync(root, ct));
        ApplyRepositoryBindings(session, next);
    }

    private static void ApplyRepositoryBindings(ViewSession session, UserRepositoryBindings? next)
    {
        lock (session.Agent)
        lock (session)
        {
            if (ViewSession.PathComparer.Equals(session.RepositoryRoot, next?.RepositoryRoot)
                && session.RepositoryBindings?.RepositoryId == next?.RepositoryId
                && session.RepositoryBindings?.Revision == next?.Revision) return;
            session.Agent.Invalidate();
            session.Editor.Invalidate();
            session.Plan.Invalidate();
            session.Setup.Invalidate();
            session.NativeContext = session.PopulationContext = null;
            session.ReferencesRestored = false;
            session.DisplayContexts.Clear();
            session.RepositoryRoot = next?.RepositoryRoot;
            session.AdvanceSelectionRevision();
            if (session.RepositoryBindings?.RepositoryId != next?.RepositoryId)
            {
                session.CurrentBenchId = null;
                session.StartupSelectionResolved = false;
            }
            session.RepositoryBindings = next;
        }
    }

    private static void MapRepositories(WebApplication app, NativePanel panel, ViewSessions sessions, CatalogChanges updates,
        Func<HttpContext, Guid, Task<ViewSession>> authorize)
    {
        app.MapPost("/api/v1/views/{id:guid}/repository-bindings", async (HttpContext http, Guid id) =>
        {
            _ = await authorize(http, id);
            var body = await Body<ReadRepositoryBindingsBody>(http);
            await Write(http, Require(await panel.ReadRepositoryAsync(body.Root, http.RequestAborted)));
        });
        app.MapPut("/api/v1/views/{id:guid}/repository-bindings", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<RepositoryBindingsBody>(http);
            var saved = Require(await panel.SaveRepositoryAsync(body.Root, body.ExpectedRevision,
                body.Connections, body.DefaultBench, http.RequestAborted));
            await SynchronizeRepository(panel, sessions, session, http.RequestAborted);
            updates.Publish(0);
            await Write(http, saved);
        });
        app.MapPut("/api/v1/views/{id:guid}/repository-context", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<RepositoryContextBody>(http);
            var bindings = body.Root is null ? null : Require(await panel.ReadRepositoryAsync(body.Root, http.RequestAborted));
            await session.RepositoryGate.WaitAsync(http.RequestAborted);
            try
            {
                var before = sessions.ReadState(session);
                _ = sessions.SaveState(session, new(before.Revision, before.Choices with
                {
                    RepositoryRoot = bindings?.RepositoryRoot,
                    BenchId = bindings?.RepositoryId == session.RepositoryBindings?.RepositoryId ? before.Choices.BenchId : null,
                    ViewingRoot = null, AddRoot = null, DisplayRoots = [], GroupSource = null, GroupField = null,
                }));
                session.RepositoryContextOverridden = true;
                session.RuntimeRepositoryApplied = true;
                ApplyRepositoryBindings(session, bindings);
            }
            finally { session.RepositoryGate.Release(); }
            updates.Publish(0);
            await Write(http, new Ack());
        });
        app.MapPost("/api/v1/views/{id:guid}/benches/{benchId:guid}/connections", async (HttpContext http, Guid id, Guid benchId) =>
        {
            var session = await authorize(http, id);
            var body = await Body<AttachBenchConnectionBody>(http);
            await SynchronizeRepository(panel, sessions, session, http.RequestAborted);
            RequireAvailable(session, body.Connection);
            var registry = Require(await panel.WorkspacesAsync(http.RequestAborted));
            if (!registry.Any(workspace => workspace.Connection == body.Connection))
                throw new HttpFailure(404, "ConnectionNotFound", "Choose an existing registered connection.");
            var library = Require(await panel.ListAsync(http.RequestAborted));
            var bench = library.Benches.SingleOrDefault(value => value.Id == benchId)
                ?? throw new HttpFailure(404, "NotFound", "The Bench was deleted.");
            var saved = BenchDefinitionResult(await panel.SaveAsync(new(bench.Id, bench.Name, bench.Selectors,
                connections: bench.Connections.Append(body.Connection)), body.ExpectedRevision, http.RequestAborted));
            session.Agent.Invalidate();
            session.Plan.Invalidate();
            updates.Publish(0);
            await Write(http, Definition(saved));
        });
    }
}

[JsonSerializable(typeof(RepositoryContextBody))]
[JsonSerializable(typeof(ReadRepositoryBindingsBody))]
[JsonSerializable(typeof(RepositoryBindingsBody))]
[JsonSerializable(typeof(AttachBenchConnectionBody))]
[JsonSerializable(typeof(UserRepositoryBindings))]
internal sealed partial class TransportJson;
