using Bench.Application;
using Bench.Infrastructure;
using Twig.Core;
using Twig.Infrastructure.Services;
using System.Text.Json.Serialization;
using static Twig.Server.BenchHost;

namespace Twig.Server;

public sealed record RepositoryContextBody(string? Root);
public sealed record ReadRepositoryBindingsBody(string Root);
public sealed record RepositoryBindingsBody(string Root, long ExpectedRevision, Connection[] Connections, Guid? DefaultBench)
{
    public Guid[]? AttachedBenches { get; init; }
    public Connection[]? ExcludedConnections { get; init; }
}
public sealed record AttachBenchConnectionBody(long ExpectedRevision, Connection Connection);
public sealed record BindRepositoryConnectionBody(long ExpectedRevision, Guid BenchId, long BenchRevision, Connection Connection,
    string? ExistingNativeDatabase = null, string? RepositoryId = null, Guid OperationId = default);

public static partial class NativeBenchHost
{
    private static async Task ApplyRuntimeRepository(NativePanel panel, ViewSession session, CancellationToken ct)
    {
        if (session.RuntimeRepositoryApplied || session.RuntimeContext is not { } runtime) return;
        if (runtime.IsRemote == true)
            throw new HttpFailure(422, "RemoteContextUnsupported", "This server supports same-machine session contexts only. A remote working directory was not mapped to local data.");
        if (runtime.WorkingDirectory is not { } root) { session.RuntimeRepositoryApplied = true; return; }
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new HttpFailure(409, "SessionDirectoryUnavailable", "The SDK session directory is unavailable on this machine. No alternate repository or profile was used.");
        var observed = await panel.ReadRepositoryAsync(root, ct);
        if (!observed.IsSuccess && observed.Error.Code != ErrorCode.InvalidInput) _ = Require(observed);
        var bindings = observed.IsSuccess ? observed.Value : null;
        session.RuntimeRepositoryRoot = bindings?.RepositoryRoot;
        session.RuntimeRepositoryApplied = true;
    }

    private static (Guid? BenchId, string? Reason) ResolveRepositoryBench(ViewSessions sessions, ViewSession session,
        UserRepositoryBindings bindings, UserBenchSnapshot library, Guid? retained)
    {
        var eligible = library.Benches.Where(bench => bindings.AttachedBenches.Contains(bench.Id))
            .Select(bench => bench.Id).ToHashSet();
        if (retained is { } previous && eligible.Contains(previous)) return (previous, "Retained");
        var recent = sessions.ReadRepositoryBenches(session, bindings.RepositoryId).FirstOrDefault(eligible.Contains);
        if (recent != Guid.Empty) return (recent, "Recent");
        if (bindings.DefaultBench is { } fallback && eligible.Contains(fallback)) return (fallback, "Default");
        return eligible.Count == 1 ? (eligible.Single(), "Sole") : (null, null);
    }

    private static HashSet<Connection>? AvailableConnections(ViewSession session)
        => session.RepositoryBindings is { } repository ? NativePanel.RepositorySources(repository).ToHashSet() : null;

    private static bool SameRepositorySources(UserRepositoryBindings? left, UserRepositoryBindings? right)
        => left?.RepositoryId == right?.RepositoryId && left?.Revision == right?.Revision
            && left?.DeclaredConnection == right?.DeclaredConnection && left?.DeclaredSourceStatus == right?.DeclaredSourceStatus
            && left?.DeclaredSourceLabel == right?.DeclaredSourceLabel;

    private static RepositorySetupDto DescribeRepository(UserRepositoryBindings repository, UserBenchSnapshot library,
        Guid? selected, string? reason, NativePanelContext[] opened, IEnumerable<UserBenchWorkspace> workspaces)
    {
        var attached = library.Benches.Where(bench => repository.AttachedBenches.Contains(bench.Id)).ToArray();
        var bench = attached.SingleOrDefault(value => value.Id == selected);
        var state = !repository.AttachmentsConfigured ? "NeedsAttachment"
            : repository.AttachedBenches.IsEmpty ? "NoAttachments"
            : attached.Length == 0 ? "NoEligibleAttachments"
            : bench is null ? "ChooseAttached" : "Selected";
        var connections = bench is null ? [] : NativePanel.Dependencies(bench, repository).Select((connection, index) =>
        {
            var count = workspaces.Count(workspace => workspace.Connection == connection);
            var availability = repository.ExcludedConnections.Contains(connection) ? "Excluded"
                : index >= NativePanel.ViewSourceLimit ? "ViewSourceLimitReached"
                : opened.Any(context => context.Stamp.Connection == connection) ? "Ready"
                : count > 1 ? "NeedsExactChoice"
                : count == 0 ? connection.Provider.Value == "twig" ? "CanInitializePrivate" : "Unavailable"
                : NativePanel.RepositorySources(repository).Contains(connection) ? "Unavailable" : "CanBindExact";
            return new RepositoryConnectionDto(connection, availability);
        }).ToArray();
        return new(state, attached.Select(Definition).ToArray(), repository.DefaultBench, bench?.Id, reason, connections)
        {
            DeclaredSourceLabel = repository.DeclaredSourceLabel,
            DeclaredSourceStatus = repository.DeclaredSourceStatus,
        };
    }

    private static void RequireAvailable(ViewSession session, Connection connection)
    {
        if (session.RepositoryBindings is { } bindings && !NativePanel.RepositorySources(bindings).Contains(connection))
            throw new HttpFailure(403, "RepositoryConnectionUnavailable",
                "Assign this registered connection in this user's repository bindings first. Existing Bench rules, drafts and pending operations were not retargeted.");
    }

    private static async Task RequireAvailableRoot(NativePanel panel, ViewSession session, string root, CancellationToken ct)
    {
        if (session.RepositoryBindings is not { } bindings) return;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var registry = Require(await panel.WorkspacesAsync(ct));
        if (!registry.Any(workspace => ViewSession.PathComparer.Equals(workspace.Root, path)
            && NativePanel.RepositorySources(bindings).Contains(workspace.Connection)))
            throw new HttpFailure(403, "RepositoryConnectionUnavailable",
                "Choose a registered connection assigned to this repository. No source was opened.");
    }

    private static async Task SynchronizeRepository(NativePanel panel, ViewSessions sessions, ViewSession session, CancellationToken ct)
    {
        await session.RepositoryGate.WaitAsync(ct);
        try
        {
            await ApplyRuntimeRepository(panel, session, ct);
            var root = session.RuntimeContext is not null && !session.RepositoryContextOverridden
                ? session.RuntimeRepositoryRoot : sessions.ReadState(session).Choices.RepositoryRoot;
            var next = root is null ? null : Require(await panel.ReadRepositoryAsync(root, ct));
            lock (session.Agent)
            lock (session)
            {
                if (ViewSession.PathComparer.Equals(session.RepositoryRoot, root)
                    && SameRepositorySources(session.RepositoryBindings, next)) return;
                session.Agent.Invalidate();
                session.Editor.Invalidate();
                session.Plan.Invalidate();
                session.Setup.Invalidate();
                session.NativeContext = session.PopulationContext = null;
                session.ReferencesRestored = false;
                session.DisplayContexts.Clear();
                session.RepositoryRoot = root;
                if (session.RepositoryBindings?.RepositoryId != next?.RepositoryId)
                {
                    session.CurrentBenchId = null;
                    session.RepositorySelectionReason = null;
                }
                else if (session.CurrentBenchId is { } current && next is not null && !next.AttachedBenches.Contains(current))
                {
                    session.CurrentBenchId = null;
                    session.RepositorySelectionReason = null;
                }
                session.RepositoryBindings = next;
            }
        }
        finally { session.RepositoryGate.Release(); }
    }

    private static void MapRepositories(WebApplication app, NativePanel panel, ViewSessions sessions, CatalogChanges updates,
        INativeConnectionSetupGateway? setup,
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
                body.Connections, body.DefaultBench, http.RequestAborted, body.AttachedBenches, body.ExcludedConnections));
            await SynchronizeRepository(panel, sessions, session, http.RequestAborted);
            updates.Publish(0);
            await Write(http, saved);
        });
        app.MapPut("/api/v1/views/{id:guid}/repository-context", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<RepositoryContextBody>(http);
            var bindings = body.Root is null ? null : Require(await panel.ReadRepositoryAsync(body.Root, http.RequestAborted));
            var before = sessions.ReadState(session);
            var selected = body.Root is null ? session.CurrentBenchId
                : bindings!.RepositoryId == session.RepositoryBindings?.RepositoryId ? session.CurrentBenchId : null;
            _ = sessions.SaveState(session, new(before.Revision, before.Choices with
            {
                RepositoryRoot = bindings?.RepositoryRoot,
                BenchId = selected, ViewingRoot = null, AddRoot = null, DisplayRoots = [],
                GroupSource = null, GroupField = null,
            }));
            session.RuntimeRepositoryApplied = true;
            session.RepositoryContextOverridden = true;
            await SynchronizeRepository(panel, sessions, session, http.RequestAborted);
            lock (session) session.CurrentBenchId = selected;
            updates.Publish(0);
            await Write(http, new Ack());
        });
        app.MapPut("/api/v1/views/{id:guid}/repository-connection", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<BindRepositoryConnectionBody>(http);
            var ct = http.RequestAborted;
            var repository = session.RepositoryBindings;
            var selection = session.SelectionRevision;
            if (body.OperationId == Guid.Empty)
                throw new HttpFailure(400, "InvalidInput", "Provide an operation ID and reuse it when retrying this setup.");
            if (repository is null || repository.RepositoryId != body.RepositoryId)
                throw new HttpFailure(409, "RepositorySelectionChanged", "The repository changed; refresh before applying setup.");
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body, TransportJson.Default.BindRepositoryConnectionBody)));
            if (repository.SetupReceipts.TryGetValue(body.OperationId, out var retained))
            {
                if (digest != retained)
                    throw new HttpFailure(409, "SetupIntentChanged", "This operation ID is already bound to different setup intent.");
                await Write(http, repository);
                return;
            }
            if (session.CurrentBenchId != body.BenchId)
                throw new HttpFailure(409, "RepositorySelectionChanged", "Choose the attached Bench for this repository again.");
            var library = Require(await panel.ListAsync(ct));
            if (library.Benches.SingleOrDefault(bench => bench.Id == body.BenchId) is not { } bench
                || bench.Revision != body.BenchRevision || !repository.AttachedBenches.Contains(bench.Id))
                throw new HttpFailure(409, "BenchChanged", "The selected Bench or its repository attachment changed.");
            if (repository.ExcludedConnections.Contains(body.Connection))
                throw new HttpFailure(403, "RepositoryConnectionExcluded", "The connection is explicitly excluded.");
            if (repository.Revision != body.ExpectedRevision)
                throw new HttpFailure(409, "RepositoryBindingsChanged", "Refresh the repository bindings before applying setup.");
            if (repository.Connections.Contains(body.Connection))
            {
                await Write(http, repository);
                return;
            }
            if (repository.DeclaredConnection != body.Connection && !bench.Connections.Contains(body.Connection))
                throw new HttpFailure(409, "SourceChanged", "The connection is neither declared by the repository nor required by the selected Bench.");
            var candidates = Require(await panel.WorkspacesAsync(ct)).Where(value => value.Connection == body.Connection).ToArray();
            NativeSourceStamp? prepared = null;
            if (candidates.Length == 0 && body.ExistingNativeDatabase is { } database)
            {
                if (body.Connection.Provider.Value != "twig" || setup is null)
                    throw new HttpFailure(409, "PrivateSetupUnavailable", "Only an existing exact native database can be initialized privately.");
                var managed = Require(await setup.InitializeRepositoryPrivateAsync(database, body.Connection, ct));
                prepared = managed.Observation.Source;
                candidates = Require(await panel.WorkspacesAsync(ct)).Where(value => value.Connection == body.Connection).ToArray();
                if (prepared.Connection != body.Connection || candidates.Length != 1
                    || !ViewSession.PathComparer.Equals(managed.WorkspaceRoot, candidates[0].Root)
                    || prepared.StoreInstance != candidates[0].StoreInstance)
                    throw new HttpFailure(409, "SourceChanged", "The private workspace could not be verified as the exact registered source.");
            }
            if (candidates.Length != 1)
                throw new HttpFailure(409, candidates.Length == 0 ? "ConnectionUnavailable" : "NeedsExactChoice",
                    "Setup requires a unique exact registered workspace.");
            var candidate = candidates[0];
            var opened = Require(await panel.OpenAsync(candidate.Root, ct, expectedSource: prepared, expectedWorkspace: candidate));
            var latest = Require(await panel.ReadRepositoryAsync(repository.RepositoryRoot, ct));
            if (opened.Stamp.Connection != body.Connection || opened.Stamp.StoreInstance != candidate.StoreInstance
                || session.SelectionRevision != selection || session.CurrentBenchId != body.BenchId
                || latest.RepositoryId != repository.RepositoryId || session.RepositoryBindings?.RepositoryId != repository.RepositoryId)
                throw new HttpFailure(409, "SourceChanged", "The source or selected Bench changed during admission.");
            var saved = Require(await panel.SaveRepositoryAsync(repository.RepositoryRoot, body.ExpectedRevision,
                repository.Connections.Append(body.Connection), repository.DefaultBench, ct, expectedBenchId: bench.Id,
                expectedBenchRevision: bench.Revision, expectedWorkspace: candidate, operationId: body.OperationId, operationDigest: digest));
            await SynchronizeRepository(panel, sessions, session, ct);
            updates.Publish(0);
            await Write(http, saved);
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
[JsonSerializable(typeof(BindRepositoryConnectionBody))]
[JsonSerializable(typeof(UserRepositoryBindings))]
internal sealed partial class TransportJson;
