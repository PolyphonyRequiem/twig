using Bench.Application;
using Bench.Infrastructure;
using Twig.Core;
using Twig.Infrastructure.Services;
using static Bench.Server.BenchHost;
using NativeBench = Twig.Core.Bench;

namespace Bench.Server;

internal sealed class NativeSetupView
{
    internal long Epoch { get; private set; }
    internal NativeSetupOperation? Operation { get; set; }
    internal void Invalidate()
    {
        lock (this)
        {
            Epoch++;
            if (Operation is { } operation)
            {
                operation.Current = NativeBenchHost.WithholdSetup(operation.Current) with { Epoch = Epoch };
                operation.Lifetime.Cancel();
            }
        }
    }
}

internal sealed class NativeSetupOperation(Guid generation, CancellationTokenSource lifetime, SetupOperationDto current)
{
    internal Guid Generation { get; } = generation;
    internal CancellationTokenSource Lifetime { get; } = lifetime;
    internal SetupOperationDto Current { get; set; } = current;
    internal Task? Work { get; set; }
}

public static partial class NativeBenchHost
{
    internal static SetupOperationDto WithholdSetup(SetupOperationDto result) => result with
    {
        Status = "Cancelled", AuthorizationUrl = null, Enrollment = null, Binding = null, Source = null,
        Error = result.Error ?? new("SetupCancelled",
            "Setup was cancelled or its view changed. Possible native effects remain; nothing was replayed.")
    };

    private static void MapSetup(WebApplication app, INativeConnectionSetupGateway? gateway, NativePanel panel, ViewSessions sessions,
        CatalogChanges updates, Func<HttpContext, Guid, Task<ViewSession>> authorize)
    {
        app.MapGet("/api/v1/views/{id:guid}/connections", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            if (session.Setup.Operation?.Work is { IsCompleted: true } work) await work;
            BenchConnectionsDto value;
            lock (session.Agent)
            {
                var request = session.Agent.ConnectionRequest;
                var operation = request is not null && session.Setup.Operation?.Current.ConnectionRequestId == request.Value.RequestId
                    ? session.Setup.Operation.Current : null;
                value = new(request?.Value, operation);
            }
            await Write(http, value);
        });
        app.MapPost("/api/v1/views/{id:guid}/connections/request", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<ConnectionTargetBody>(http);
            var library = Require(await panel.ListAsync(http.RequestAborted));
            var bench = library.Benches.SingleOrDefault(value => value.Id == body.BenchId);
            if (bench is null || bench.Revision != body.BenchRevision
                || session.CurrentBenchId != body.BenchId)
                throw new HttpFailure(409, "BenchChanged", "Choose the current Bench and review its saved revision before adding a connection.");
            await Write(http, PrepareConnectionRequest(session, bench, body.Provider, body.Repository).Value);
        });
        app.MapPost("/api/v1/views/{id:guid}/connections/cancel", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<ConnectionRequestBody>(http);
            bool cancelSetup;
            lock (session.Agent)
            {
                if (session.Agent.ConnectionRequest is not { } request || request.Value.RequestId != body.RequestId)
                    throw new HttpFailure(409, "ConnectionRequestChanged", "The connection request was replaced or is unavailable.");
                request.Lifetime.Cancel();
                request.Value = request.Value with { State = "Cancelled" };
                cancelSetup = session.Setup.Operation?.Current.ConnectionRequestId == body.RequestId;
            }
            if (cancelSetup) session.Setup.Invalidate();
            updates.PublishAgent(session.Id);
            await Write(http, new Ack());
        });
        app.MapPost("/api/v1/views/{id:guid}/connections/approve",
            (HttpContext http, Guid id) => ApproveConnection(http, id, false));
        app.MapPost("/api/v1/views/{id:guid}/agent/connection-request-start",
            (HttpContext http, Guid id) => ApproveConnection(http, id, true));
        async Task ApproveConnection(HttpContext http, Guid id, bool automatic)
        {
            var session = automatic ? AutomaticSession(http, id) : await authorize(http, id);
            var body = automatic
                ? new ConnectionApprovalBody((await Body<ConnectionRequestBody>(http)).RequestId)
                : await Body<ConnectionApprovalBody>(http);
            var request = PendingConnectionRequest(session, body.RequestId);
            if (request.Value.State != "Requested")
                throw new HttpFailure(409, "ConnectionSetupAlreadyStarted", "Observe the original setup. It cannot be approved or dispatched twice.");
            _ = await CheckConnectionRequestAsync(panel, session, request, http.RequestAborted);
            NativeSetupTarget target;
            if (request.Value.Provider == "github")
            {
                if (body.Account is not null) RequiredText(body.Account, "Use a named GitHub account.", 128);
                target = new NativeGitHubSetupTarget("github.com", request.Value.Repository!, body.Account ?? "");
            }
            else
            {
                if (body.Account is not null) throw new HttpFailure(400, "InvalidInput", "A native Twig connection does not use a GitHub account.");
                target = new NativeTwigSetupTarget();
            }
            await Start(http, session, session.Setup.Epoch, "Connection setup", async (operation, ct) =>
            {
                _ = await CheckConnectionRequestAsync(panel, session, request, ct);
                var result = await Gateway().InitializeManagedAsync(target, ct);
                if (!result.IsSuccess) return Failed(operation.Current, result.Error);
                _ = await CheckConnectionRequestAsync(panel, session, request, ct);
                var observed = result.Value.Observation;
                if (observed.Source.Connection.Provider.Value != request.Value.Provider
                    || observed.Schema is not { IsSuccess: true } schema || schema.Value.Connection != observed.Source.Connection)
                    return operation.Current with { Status = "Failed", Error = new("SetupReadbackUnavailable",
                        "Setup did not return the exact requested provider and creation-bound schema. No source was admitted.") };
                return operation.Current with { Status = "Ready", Source = new(result.Value.WorkspaceRoot,
                    observed.Source, schema.Value.Connection, observed.SchemaObservedAt) };
            }, request, automatic);
        }
        app.MapPost("/api/v1/views/{id:guid}/connections/add",
            (HttpContext http, Guid id) => AddConnection(http, id, false));
        app.MapPost("/api/v1/views/{id:guid}/agent/connection-request-add",
            (HttpContext http, Guid id) => AddConnection(http, id, true));
        async Task AddConnection(HttpContext http, Guid id, bool automatic)
        {
            var session = automatic ? AutomaticSession(http, id) : await authorize(http, id);
            var body = automatic
                ? new ConnectionAdmissionBody((await Body<ConnectionRequestBody>(http)).RequestId,
                    session.Setup.Operation?.Current.OperationId ?? Guid.Empty, false)
                : await Body<ConnectionAdmissionBody>(http);
            if (body.IncludeAll)
                throw new HttpFailure(400, "MembershipIsSeparate", "Adding a connection does not select work. Add items or save a membership rule separately.");
            await session.Agent.Gate.WaitAsync(http.RequestAborted);
            var attempted = false;
            NativeConnectionRequest? admitting = null;
            var completed = false;
            try
            {
                var request = PendingConnectionRequest(session, body.RequestId);
                admitting = request;
                if (request.Value.State != "Ready")
                    throw new HttpFailure(409, "ConnectionNotReady", "Only an unattempted, verified connection admission can proceed. Observe the saved Bench; no admission was replayed.");
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, request.Lifetime.Token);
                var ct = lifetime.Token;
                var definition = await CheckConnectionRequestAsync(panel, session, request, ct);
                var operation = Original(session, body.OperationId);
                if (operation.Current.ConnectionRequestId != body.RequestId || operation.Work is not { IsCompletedSuccessfully: true }
                    || operation.Current is not { Status: "Ready", Pending: false, Source: { } source })
                    throw new HttpFailure(409, "ConnectionNotReady", "Observe the exact original setup until verified readback is Ready.");
                if (session.Editor.Ticket != Guid.Empty || session.Plan.Operation?.Work is { IsCompleted: false })
                    throw new HttpFailure(409, "EditorBusy", "Finish the current editor or Plan operation before changing Bench membership.");
                var opened = ConnectionObservation(await panel.OpenAsync(source.WorkspaceRoot, ct), app.Logger);
                RequireAvailable(session, opened.Stamp.Connection);
                if (opened.Stamp != source.Source || opened.Stamp.Connection != source.SchemaConnection)
                    throw new HttpFailure(409, "SourceChanged", "The prepared source or principal changed. No admission or Bench rule was saved.");
                _ = ConnectionObservation(await panel.ValidateDisplayAccessAsync(opened, ct), app.Logger);
                _ = await CheckConnectionRequestAsync(panel, session, request, ct);
                var changed = !definition.Connections.Contains(opened.Stamp.Connection);
                var retained = sessions.ReadState(session);
                attempted = true;
                request.Value = request.Value with { State = "Adding" };
                if (changed)
                {
                    _ = BenchDefinitionResult(await panel.SaveAsync(new NativeBench(definition.Id, definition.Name,
                        definition.Selectors, connections: definition.Connections.Append(opened.Stamp.Connection)),
                        definition.Revision, ct));
                }
                ct.ThrowIfCancellationRequested();
                _ = PendingConnectionRequest(session, request.Value.RequestId);
                lock (session)
                    session.DisplayContexts[opened.Stamp.Connection] = opened;
                _ = sessions.SaveState(session, new(retained.Revision, retained.Choices with
                {
                    ViewingRoot = request.Anchor?.Root, AddRoot = request.Population?.Root,
                    BenchId = definition.Id,
                    DisplayRoots = retained.Choices.DisplayRoots.Append(opened.Root).Distinct(ViewSession.PathComparer).ToArray(),
                }));
                session.Agent.Invalidate();
                session.Plan.Invalidate();
                request.Value = request.Value with { State = "Added" };
                session.Agent.ConnectionRequest = request;
                completed = true;
                updates.Publish(0);
                if (automatic) await Write(http, request.Value);
                else await Write(http, new ConnectionAddedDto(request.Value, opened.Root, changed));
            }
            catch (OperationCanceledException) when (attempted)
            {
                throw new HttpFailure(504, "Indeterminate", "The local Bench save may have completed. Read its saved rules before another action; do not replay admission.");
            }
            finally
            {
                if (attempted && !completed && admitting is not null)
                {
                    admitting.Value = admitting.Value with { State = "Indeterminate" };
                    session.Agent.Snapshot = null;
                    session.Plan.Invalidate();
                    updates.Publish(0);
                }
                session.Agent.Gate.Release();
            }
        }
        app.MapPost("/api/v1/views/{id:guid}/setup/enroll-aad", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<SetupEnrollmentBody>(http);
            RequiredText(body.Alias, "Name the AAD identity to enroll.", 64);
            OptionalText(body.Tenant, "Choose a tenant ID or domain, not an authority URL.");
            await Start(http, session, body.Epoch, "Enrollment", async (operation, ct) =>
            {
                var result = await Gateway().EnrollAadAsync(new(body.Alias, body.Tenant), url =>
                {
                    ct.ThrowIfCancellationRequested();
                    lock (session.Setup)
                    {
                        if (operation.Lifetime.IsCancellationRequested || session.Revoked
                            || session.ProfileGeneration != operation.Generation)
                            throw new OperationCanceledException(ct);
                        operation.Current = operation.Current with { AuthorizationUrl = url.AbsoluteUri };
                    }
                }, ct);
                return result.IsSuccess
                    ? operation.Current with { Status = "Enrolled", AuthorizationUrl = null, Enrollment = result.Value }
                    : Failed(operation.Current, result.Error);
            });
        });
        app.MapPost("/api/v1/views/{id:guid}/setup/ado-binding", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<SetupBindingBody>(http);
            RequiredText(body.Organization, "Supply the ADO organization.");
            RequiredText(body.Project, "Supply the ADO project.");
            RequiredText(body.IdentityAlias, "Choose the named AAD identity.", 64);
            await Start(http, session, body.Epoch, "Binding", async (operation, ct) =>
            {
                var result = await Gateway().CreateAdoBindingAsync(new(body.Organization, body.Project, body.IdentityAlias), ct);
                return result.IsSuccess ? operation.Current with { Status = "Bound", Binding = result.Value }
                    : Failed(operation.Current, result.Error);
            });
        });
        app.MapPost("/api/v1/views/{id:guid}/setup/initialize-ado", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<SetupAdoBody>(http);
            RequiredRoot(body.WorkspaceRoot);
            RequiredText(body.Organization, "Supply the ADO organization.");
            RequiredText(body.Project, "Supply the ADO project.");
            RequiredText(body.BindingId, "Choose an exact native ADO binding.");
            OptionalText(body.Team, "Supply an explicit team or leave it empty.");
            OptionalText(body.GitProject, "Supply an explicit Git project or leave it empty.");
            await Initialize(http, session, body.Epoch, new(body.WorkspaceRoot,
                new NativeAdoSetupTarget(body.Organization, body.Project, body.BindingId, body.Team, body.GitProject)));
        });
        app.MapPost("/api/v1/views/{id:guid}/setup/initialize-github", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<SetupGitHubBody>(http);
            RequiredRoot(body.WorkspaceRoot);
            RequiredText(body.Host, "Supply the GitHub host.");
            RequiredText(body.Repository, "Supply the explicit owner/repository.");
            RequiredText(body.Account, "Choose the named GitHub account; the active account is never inferred.");
            await Initialize(http, session, body.Epoch, new(body.WorkspaceRoot,
                new NativeGitHubSetupTarget(body.Host, body.Repository, body.Account)));
        });
        app.MapPost("/api/v1/views/{id:guid}/setup/initialize-twig", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<SetupTwigBody>(http);
            RequiredRoot(body.WorkspaceRoot);
            await Initialize(http, session, body.Epoch, new(body.WorkspaceRoot, new NativeTwigSetupTarget()));
        });
        app.MapGet("/api/v1/views/{id:guid}/setup/operation", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            if (session.Setup.Operation?.Work is { IsCompleted: true } work) await work;
            SetupOperationDto value;
            lock (session.Setup) value = (session.Setup.Operation?.Current ?? new(Guid.Empty, "Idle", "None"))
                with { Epoch = session.Setup.Epoch };
            await Write(http, value);
        });
        app.MapDelete("/api/v1/views/{id:guid}/setup/operation", async (HttpContext http, Guid id) =>
        {
            var session = await authorize(http, id);
            var body = await Body<SetupCancelBody>(http);
            SetupOperationDto value;
            lock (session.Setup)
            {
                CurrentEpoch(session, body.Epoch);
                session.Setup.Invalidate();
                value = session.Setup.Operation?.Current ?? new(Guid.Empty, "Idle", "None") { Epoch = session.Setup.Epoch };
            }
            await Write(http, value);
        });
        app.MapGet("/api/v1/views/{id:guid}/setup/operations/{operation:guid}", async (HttpContext http, Guid id, Guid operation) =>
        {
            var session = await authorize(http, id);
            NativeSetupOperation active;
            lock (session.Setup) active = Original(session, operation);
            if (active.Work is { IsCompleted: true } work) await work;
            SetupOperationDto value;
            lock (session.Setup) value = Original(session, operation).Current;
            await Write(http, value);
        });
        app.MapDelete("/api/v1/views/{id:guid}/setup/operations/{operation:guid}", async (HttpContext http, Guid id, Guid operation) =>
        {
            var session = await authorize(http, id);
            _ = await Body<EmptyBody>(http);
            SetupOperationDto value;
            lock (session.Setup)
            {
                _ = Original(session, operation);
                session.Setup.Invalidate();
                value = session.Setup.Operation!.Current;
            }
            await Write(http, value);
        });

        INativeConnectionSetupGateway Gateway() => gateway
            ?? throw new HttpFailure(503, "SetupUnavailable", "Native connection setup is not configured. No alternate setup path was used.");
        ViewSession AutomaticSession(HttpContext http, Guid id)
        {
            var session = sessions.Presentation(id, Bearer(http));
            if (!session.Agent.OwnerHostAuthenticated || !session.Agent.AutomaticAccess || session.Revoked)
                throw new HttpFailure(403, "AgentPermissionRequired", "Automatic setup requires an authenticated local owner host and unrestricted local access.");
            _ = sessions.ReadState(session);
            return session;
        }
        Task Initialize(HttpContext http, ViewSession session, long epoch, NativeFreshSetupRequest request)
            => Start(http, session, epoch, "Initialization", async (operation, ct) =>
            {
                var result = await Gateway().InitializeFreshAsync(request, ct);
                if (!result.IsSuccess) return Failed(operation.Current, result.Error);
                var observation = result.Value;
                if (observation.Schema is not { IsSuccess: true } schema
                    || schema.Value.Connection != observation.Source.Connection)
                    return operation.Current with { Status = "Failed", Error = new("SetupReadbackUnavailable",
                        "Native initialization did not return a successful creation-bound schema readback. Possible effects remain; no Ready source was inferred.") };
                return operation.Current with { Status = "Ready",
                    Source = new(request.WorkspaceRoot, observation.Source, schema.Value.Connection, observation.SchemaObservedAt) };
            });
        async Task Start(HttpContext http, ViewSession session, long expectedEpoch, string phase,
            Func<NativeSetupOperation, CancellationToken, Task<SetupOperationDto>> work,
            NativeConnectionRequest? request = null, bool requestOnly = false)
        {
            _ = Gateway();
            NativeSetupOperation operation;
            long epoch;
            lock (session.Setup)
            {
                CurrentEpoch(session, expectedEpoch);
                if (session.Setup.Operation?.Work is { IsCompleted: false })
                    throw new HttpFailure(409, "SetupBusy", "Observe or cancel the original setup operation. It will not be dispatched twice.");
                session.Setup.Operation?.Lifetime.Dispose();
                if (request is not null)
                {
                    _ = PendingConnectionRequest(session, request.Value.RequestId);
                    if (request.Value.State != "Requested")
                        throw new HttpFailure(409, "ConnectionSetupAlreadyStarted", "Observe the original connection setup; it will not be dispatched twice.");
                    request.Value = request.Value with { State = "SettingUp" };
                }
                var lifetime = request is null ? CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping)
                    : CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping, request.Lifetime.Token);
                lifetime.CancelAfter(TimeSpan.FromMinutes(5));
                operation = new(session.ProfileGeneration, lifetime,
                    new(Guid.NewGuid(), "Running", phase) { Pending = true, Epoch = expectedEpoch,
                        ConnectionRequestId = request?.Value.RequestId });
                session.Setup.Operation = operation;
                epoch = session.Setup.Epoch;
                operation.Work = Run();
            }
            if (requestOnly) await Write(http, request!.Value, 202);
            else await Write(http, new SetupOperationDto(operation.Current.OperationId, "Running", phase) { Pending = true, Epoch = epoch,
                ConnectionRequestId = request?.Value.RequestId }, 202);

            async Task Run()
            {
                await Task.Yield();
                SetupOperationDto result = operation.Current;
                try
                {
                    _ = sessions.ReadState(session);
                    operation.Lifetime.Token.ThrowIfCancellationRequested();
                    lock (session.Setup)
                        if (session.Setup.Epoch != epoch || session.Revoked || session.ProfileGeneration != operation.Generation)
                            throw new OperationCanceledException(operation.Lifetime.Token);
                    result = await work(operation, operation.Lifetime.Token);
                    // Reuse owner/profile admission at the completion boundary, not native source validation.
                    _ = sessions.ReadState(session);
                }
                catch (HttpFailure error)
                { result = result with { Status = "Failed", AuthorizationUrl = null, Enrollment = null, Binding = null, Source = null,
                    Error = result.Error ?? new(error.Code, error.Message) }; }
                catch (OperationCanceledException)
                { result = WithholdSetup(result); }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    app.Logger.LogError("Native setup failed ({Type}); possible effects remain; no replay was attempted.", error.GetType().Name);
                    result = result with { Status = "Failed", AuthorizationUrl = null, Enrollment = null, Binding = null, Source = null,
                        Error = result.Error ?? new("SetupUnavailable", "Native setup did not complete. Possible effects remain; no alternate path or replay was used.") };
                }
                lock (session.Setup)
                {
                    operation.Current = (session.Setup.Epoch != epoch || operation.Lifetime.IsCancellationRequested
                        || session.Revoked || session.ProfileGeneration != operation.Generation
                        ? WithholdSetup(result) : result) with { Pending = false, Epoch = session.Setup.Epoch };
                    if (request is not null) request.Value = request.Value with { State = operation.Current.Status };
                }
                updates.Publish(0);
            }
        }
        SetupOperationDto Failed(SetupOperationDto operation, Error error)
        {
            var diagnostics = NativeConnectionSetup.ReadFailure(error);
            app.Logger.LogWarning("Native setup refused ({Code}); diagnostics supported: {Supported}; no replay.", error.Code, diagnostics.IsSuccess);
            return operation with { Status = diagnostics.IsSuccess && diagnostics.Value.Cancelled ? "Cancelled" : "Failed",
                AuthorizationUrl = null, Error = new(error.Code.ToString(), error.Message),
                NativeFailure = diagnostics.IsSuccess ? new(diagnostics.Value.Phase.ToString(), diagnostics.Value.Cancelled,
                    diagnostics.Value.Effects.Select(effect => new SetupEffectDto(effect.Kind.ToString(), effect.State.ToString())).ToArray()) : null,
                DiagnosticsError = diagnostics.IsSuccess ? null : new(diagnostics.Error.Code.ToString(), diagnostics.Error.Message) };
        }
    }

    private static NativeSetupOperation Original(ViewSession session, Guid id)
        => session.Setup.Operation is { } operation && operation.Current.OperationId == id ? operation
            : throw new HttpFailure(404, "SetupOperationUnavailable", "The original setup operation is unavailable. No setup was replayed.");
    private static void CurrentEpoch(ViewSession session, long epoch)
    {
        if (session.Setup.Epoch != epoch)
            throw new HttpFailure(409, "SetupNavigationChanged", "Setup navigation changed. Observe the original operation; no stale request was dispatched or replayed.");
    }
    private static void RequiredText(string value, string message, int maximum = 1024)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != value.Trim() || value.Any(char.IsControl))
            throw new HttpFailure(400, "InvalidInput", message);
    }
    private static void OptionalText(string? value, string message)
    {
        if (value is not null) RequiredText(value, message);
    }
    private static void RequiredRoot(string root)
    {
        RequiredText(root, "Supply a canonical absolute fresh Git worktree root.", 4096);
        if (!Path.IsPathFullyQualified(root)
            || !ViewSession.PathComparer.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))))
            throw new HttpFailure(400, "InvalidInput", "Supply a canonical absolute fresh Git worktree root. No existing state is reset or repaired.");
    }
}
