using Bench.Application;
using Bench.Infrastructure;
using Twig.Core;
using Twig.Infrastructure.Services;
using static Bench.Server.BenchHost;

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

    private static void MapSetup(WebApplication app, INativeConnectionSetupGateway? gateway, ViewSessions sessions,
        CatalogChanges updates, Func<HttpContext, Guid, Task<ViewSession>> authorize)
    {
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
            Func<NativeSetupOperation, CancellationToken, Task<SetupOperationDto>> work)
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
                var lifetime = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
                lifetime.CancelAfter(TimeSpan.FromMinutes(5));
                operation = new(session.ProfileGeneration, lifetime,
                    new(Guid.NewGuid(), "Running", phase) { Pending = true, Epoch = expectedEpoch });
                session.Setup.Operation = operation;
                epoch = session.Setup.Epoch;
                operation.Work = Run();
            }
            await Write(http, new SetupOperationDto(operation.Current.OperationId, "Running", phase) { Pending = true, Epoch = epoch }, 202);

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
                    operation.Current = (session.Setup.Epoch != epoch || operation.Lifetime.IsCancellationRequested
                        || session.Revoked || session.ProfileGeneration != operation.Generation
                        ? WithholdSetup(result) : result) with { Pending = false, Epoch = session.Setup.Epoch };
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
