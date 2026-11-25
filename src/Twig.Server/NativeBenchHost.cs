using System.Collections.Immutable;
using System.Net;
using Bench.Application;
using Bench.Domain;
using Bench.Infrastructure;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Twig.Core;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using static Twig.Server.BenchHost;
using NativeBench = Twig.Core.Bench;

namespace Twig.Server;

/// <summary>Production panel host. Legacy catalog/approval endpoints are deliberately not mounted.</summary>
public static partial class NativeBenchHost
{
    private const int DefinitionBodyLimit = 2 * 1024 * 1024 + 16384;
    private static bool QualifiedOwnerRoute(HttpRequest request) => (request.Method, request.Path.Value?.Split('/')) switch
    {
        ("GET", ["", "api", "v2", "views", var view, "authorization" or "benches"]) => Guid.TryParseExact(view, "D", out _),
        ("PUT" or "POST", ["", "api", "v2", "views", var view, "benches", var bench, "partitions", var partition, .. var rest])
            => Guid.TryParseExact(view, "D", out _) && Guid.TryParseExact(bench, "D", out _) && Guid.TryParseExact(partition, "D", out _)
                && (request.Method == "PUT" ? rest is ["input"] : rest is ["input", "inspect"] or ["action"]),
        ("GET", ["", "api", "v2", "views", var view, "benches", var bench, "partitions", var partition, "page"])
            => Guid.TryParseExact(view, "D", out _) && Guid.TryParseExact(bench, "D", out _) && Guid.TryParseExact(partition, "D", out _),
        ("GET", ["", "api", "v2", "views", var view, "benches", var bench, "partitions"])
            => Guid.TryParseExact(view, "D", out _) && Guid.TryParseExact(bench, "D", out _),
        ("GET", ["", "api", "v1", "status" or "owner-status" or "windows-profile"]) => true,
        ("POST", ["", "api", "v1", "canvas-views" or "windows-profile" or "windows-enroll" or "windows-renew" or "windows-agent"]) => true,
        ("GET", ["", "api", "v1", "views", var id, "benches" or "events" or "session" or "presentation"])
            => Guid.TryParseExact(id, "D", out _),
        ("DELETE", ["", "api", "v1", "views", var id]) => Guid.TryParseExact(id, "D", out _),
        _ => false
    };

    public static WebApplication Build(ServerOptions options, NativePanel panel, ViewSessions sessions, CatalogChanges? changes = null,
        INativeConnectionSetupGateway? connectionSetup = null)
    {
        var updates = changes ?? new CatalogChanges();
        if (options.ExecutionV2 && options.ProcessInstance == Guid.Empty)
            throw new ArgumentException("Execution negotiation requires a process identity.");
        var executionSession = options.ExecutionV2 ? $"local-execution-v2/{options.ProcessInstance:D}" : null;
        string[] capabilities = ["native-source", "verified-canvas-assets-v1", "scoped-bench-query-rules-v1", "owner-profile-v1", "owner-approved-connections-v1", "automatic-connections-v1", "owner-agent-v1", "mcp-read-v1", "explicit-bench-connections-v1", "repository-bindings-v1", "session-context-v1", "ado-setup-discovery-v1", "managed-ado-setup-v1", "ado-identity-reuse-v1", "managed-github-setup-v1", "workspace-read-v1", "private-editor-inputs-v1", "source-scoped-seeds-v1", "bench-seed-members-v1"];
        var status = new ServiceStatus(CanvasAssets.Protocol, "Native user-wide Benches", true, options.ServerId,
            CanvasAssets.ServerVersion, options.ExecutionV2 ? [.. capabilities, "local-execution-v2"] : capabilities,
            executionSession);
        var statusBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(status, TransportJson.Default.ServiceStatus);
        if (statusBytes.Length > 4096) throw new InvalidDataException("The owner status exceeds the client discovery bound.");
        Task WriteStatus(HttpContext context)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            return context.Response.Body.WriteAsync(statusBytes.AsMemory(), context.RequestAborted).AsTask();
        }
        if (options.WindowsSignIn && (options.Certificate is null || !OperatingSystem.IsWindows() || options.Port == 0))
            throw new ArgumentException("Same-owner Windows authentication requires the existing trusted stable HTTPS localhost endpoint.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        if (options.ExecutionV2)
        {
            builder.Services.AddSingleton(_ => new ReleasedBenchDisplay(Path.Combine(options.DataDirectory, "released-benches.db"), sessions: sessions));
            builder.Services.AddSingleton<Twig.Commands.IOwnerBenchPublication>(services => services.GetRequiredService<ReleasedBenchDisplay>());
        }
        if (options.ExecutionV2)
            builder.Services.AddSingleton(sp => new OwnerCommandEndpoint(options.ProcessInstance, options.DataDirectory,
                panel.Definitions, sp, sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        if (options.ExecutionV2)
            builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(10));
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Logging.AddFilter((category, level) =>
            (category is "Twig.Server.NativeBenchHost" or "Bench.SourceDiagnostics" or "Twig.Server.NativeMetadataSync") && level >= LogLevel.Warning);
        if (!options.ExecutionV2)
            builder.Services.AddHostedService(services =>
                new NativeMetadataSync(panel, services.GetRequiredService<ILogger<NativeMetadataSync>>()));
        if (options.WindowsSignIn)
        {
            var owner = WindowsOwner();
            builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
            builder.Services.AddAuthorization(authorization => authorization.AddPolicy("WindowsOwner", policy =>
                policy.AddAuthenticationSchemes(NegotiateDefaults.AuthenticationScheme).RequireAuthenticatedUser()
                    .RequireAssertion(context => IsWindowsOwner(context.User, owner))));
        }
        builder.WebHost.ConfigureKestrel(server =>
        {
            if (options.Certificate is null) server.Listen(IPAddress.Loopback, options.Port);
            else server.ListenLocalhost(options.Port, endpoint => endpoint.UseHttps(options.Certificate));
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = DefinitionBodyLimit;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.Limits.MaxConcurrentConnections = 32;
        });
        var app = builder.Build();
        if (options.ExecutionV2) MapReleasedDisplay(app, sessions, options, panel);
        var diagnostics = new NativeSourceDiagnostics(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Bench.SourceDiagnostics"));
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'"
                + (context.Request.Path == "/" ? "; frame-ancestors 'none'" : "");
            var host = $"{(options.Certificate is null ? "127.0.0.1" : "localhost")}:{context.Connection.LocalPort}";
            var origin = context.Request.Headers.Origin.ToString();
            var bootstrap = (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && context.Request.Path.Value is "/" or "/canvas" or "/app.js" or "/style.css"
                    or "/view-model.js" or "/item-detail.js" or "/assignee-profiles.js" or "/item-editor.js" or "/editor-drafts.js" or "/connection-setup.js" or "/vendor/purify.es.mjs" or "/vendor/marked.esm.js"
                    or "/icons.css";
            if (context.Request.Host.Value != host || !bootstrap && (origin.Length != 0 && origin != $"{context.Request.Scheme}://{host}"
                || context.Request.Headers["Sec-Fetch-Site"] == "cross-site"))
            { await Write(context, new Failure("OriginRejected", "Use the exact local service origin."), 403); return; }
            if (context.Request.Path.StartsWithSegments("/api")
                && executionSession is not null
                && !(HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Value is "/api/v1/status" or "/api/v1/owner-status")
                && context.Request.Headers["X-Twig-Execution"] != executionSession)
            { await Write(context, new Failure("PolicyViolation", "A matched Twig client must negotiate this owner before continuing."), 403); return; }
            if (context.Request.Path.StartsWithSegments("/api")
                && (context.Request.QueryString.HasValue || context.Request.Method != "GET" && context.Request.Headers["X-Twig-Bench"] != "1"))
            { await Write(context, new Failure("InvalidTransport", "Use capability headers and bounded JSON."), 403); return; }
            if (options.ExecutionV2 && context.Request.Path.StartsWithSegments("/api") && !QualifiedOwnerRoute(context.Request))
            { await Write(context, new Failure("PolicyViolation", "This route is not qualified for the isolated execution owner."), 403); return; }
            try { await next(context); }
            catch (HttpFailure error) when (!context.Response.HasStarted)
            {
                if (error.Code == "PolicyViolation" && context.Items[typeof(ViewSession)] is ViewSession denied)
                    sessions.DetachPresentation(denied);
                await Write(context, new Failure(error.Code, error.Message), error.Status);
            }
            catch (Exception error) when (!context.Response.HasStarted && error is System.Text.Json.JsonException or BadHttpRequestException or ArgumentException)
            { await Write(context, new Failure("InvalidInput", "The request does not match the bounded native panel contract."), 400); }
            catch (OperationCanceledException) when (!context.Response.HasStarted)
            {
                var read = HttpMethods.IsGet(context.Request.Method) || context.Request.Path.Value is { } path
                    && (path.Contains("/planning/", StringComparison.Ordinal) || path.EndsWith("/mcp", StringComparison.Ordinal));
                await Write(context, read
                    ? new Failure("ReadTimeout", "Loading took too long. Your connection and choices were kept; try again.")
                    : new Failure("Indeterminate", "This change could not be confirmed. Check its saved state before trying again."), 504);
            }
            catch (Exception error) when (!context.Response.HasStarted && error is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                app.Logger.LogError(error, "Native panel operation failed; existing data was preserved.");
                await Write(context, new Failure("Unavailable", "Native data is unavailable; nothing was reset."), 503);
            }
        });
        if (options.WindowsSignIn) { app.UseAuthentication(); app.UseAuthorization(); }
        if (options.WindowsSignIn)
            app.MapGet("/api/v1/owner-status", (HttpContext context) =>
            {
                if (!options.ExecutionV2 && options.ProcessInstance != Guid.Empty)
                    context.Response.Headers[ServerLauncher.InstanceHeader] = options.ProcessInstance.ToString("D");
                return WriteStatus(context);
            })
                .RequireAuthorization("WindowsOwner");
        app.MapGet("/api/v1/status", WriteStatus);
        app.MapPost("/api/v1/canvas-views", async (HttpContext context) =>
        {
            var body = await Body<RegisterCanvasBody>(context);
            if (options.ExecutionV2 && body.Context?.WorkingDirectory is not null)
                throw new HttpFailure(403, "PolicyViolation", "Fixture views cannot attach a workspace directory.");
            await Write(context, sessions.RegisterNativeCanvas(Require(await panel.ListAsync(context.RequestAborted)).InstanceId, body.Context));
        });
        if (options.WindowsSignIn)
            app.MapPost("/api/v1/windows-enroll", async (HttpContext context) =>
            {
                var body = await Body<OwnerConnectBody>(context);
                var instance = Require(await panel.ListAsync(context.RequestAborted)).InstanceId;
                await Write(context, sessions.ConnectOwner(instance, body.ViewId, body.ProfileId));
            }).RequireAuthorization("WindowsOwner");
        app.MapGet("/api/v1/views/{id:guid}/session", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id);
            await Write(context, new NativeSessionInfo(session.CatalogId, session.NativeContentBlocked,
                session.Preferences with { BenchId = session.CurrentBenchId }));
        });
        app.MapDelete("/api/v1/views/{id:guid}/session", async (HttpContext context, Guid id) =>
        {
            sessions.Revoke(await Authorize(context, id));
            await Write(context, new Ack());
        });
        app.MapPost("/api/v1/views/{id:guid}/attachment", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            _ = await Body<EmptyBody>(context);
            if (session.NativeContext is { } source)
                _ = ConnectionObservation(await panel.CaptureAsync(source, context.RequestAborted), app.Logger);
            sessions.AttachPresentation(id, Bearer(context));
            await Write(context, new Ack());
        });
        app.MapPut("/api/v1/views/{id:guid}/layout", async (HttpContext context, Guid id) =>
        {
            _ = sessions.Presentation(id, Bearer(context));
            sessions.SetLayout(id, Bearer(context), await Body<LayoutBody>(context));
            updates.PublishPresentation();
            await Write(context, new Ack());
        });
        app.MapGet("/api/v1/views/{id:guid}/presentation", (HttpContext context, Guid id) =>
        {
            _ = sessions.Presentation(id, Bearer(context));
            return Write(context, new Ack());
        });
        app.MapDelete("/api/v1/views/{id:guid}", (HttpContext context, Guid id) =>
        {
            sessions.Close(id, Bearer(context));
            return Write(context, new Ack());
        });
        app.MapPut("/api/v1/views/{id:guid}/session", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var body = await Body<ViewBody>(context);
            if (body.ExpectedRevision != session.Preferences.Revision)
                throw new HttpFailure(409, "Conflict", "The panel preferences changed. Reload first.");
            if (body.BenchId != session.CurrentBenchId)
                throw new HttpFailure(409, "Conflict", "Use the Bench selection endpoint before changing its presentation.");
            sessions.Update(session, new(body.ExpectedRevision, body.BenchId, body.Layout, body.GroupSource, body.GroupField), body.ExpectedRevision);
            updates.PublishPresentation();
            await Write(context, session.Preferences with { BenchId = body.BenchId });
        });
        app.MapGet("/api/v1/views/{id:guid}/benches", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            await ResolveBenchConnections(panel, session, context.RequestAborted);
            var shared = Require(await panel.ListAsync(context.RequestAborted));
            var workspaces = Require(await panel.WorkspacesAsync(context.RequestAborted));
            NativeAadEnrollment[] identities = [];
            if (connectionSetup is not null && workspaces.Any(workspace => workspace.Connection.Provider.Value == "ado"))
            {
                identities = await ReadAccounts(context.RequestAborted);
            }
            var labels = new List<WorkspaceDto>();
            var labelBudget = System.Diagnostics.Stopwatch.StartNew();
            foreach (var workspace in workspaces)
            {
                var display = labelBudget.Elapsed < TimeSpan.FromSeconds(2)
                    ? await NativeConnectionLabels.ReadAsync(workspace.Root, workspace.Connection, context.RequestAborted)
                    : NativeConnectionLabels.Unavailable(workspace.Connection);
                labels.Add(new(workspace.Root, workspace.Connection.Provider.Value, display.Name, display.Available)
                    { Connection = workspace.Connection, Account = NativeConnectionLabels.Account(workspace.Connection, identities),
                        Available = session.RepositoryBindings is null
                        || NativePanel.RepositorySources(session.RepositoryBindings).Contains(workspace.Connection) });
            }
            await Write(context, new NativePanelSummary(shared.InstanceId, shared.Revision,
                shared.Benches.Select(Definition).ToArray(),
                labels.ToArray(), session.NativeContext?.Root, session.PopulationContext?.Root,
                ConnectionSummary(session.NativeContext), ConnectionSummary(session.PopulationContext))
                { RepositoryRoot = session.RepositoryRoot, RepositoryBindings = session.RepositoryBindings,
                    RuntimeContext = session.RuntimeContext });
        });
        app.MapPut("/api/v1/views/{id:guid}/context", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            long revision;
            lock (session) revision = session.SelectionRevision;
            var body = await Body<ContextBody>(context);
            session.Agent.Invalidate();
            CancelEditor(session);
            session.Setup.Invalidate();
            await RequireAvailableRoot(panel, session, body.Root, context.RequestAborted);
            var opened = ConnectionObservation(await panel.OpenAsync(body.Root, context.RequestAborted), app.Logger);
            if (Require(await panel.ReadAdmittedSourceAsync(opened, context.RequestAborted)).Source != opened.Stamp)
                throw new HttpFailure(409, "Conflict", "The admitted connection changed while opening the source.");
            _ = sessions.CommitViewChoice(session, Bearer(context), revision, () =>
            {
                RequireAvailable(session, opened.Stamp.Connection);
                session.NativeContext = opened;
                session.DisplayContexts[opened.Stamp.Connection] = opened;
                session.ReferencesRestored = true;
                return true;
            });
            updates.Publish(0);
            await Write(context, new Ack());
        });
        app.MapPut("/api/v1/views/{id:guid}/selection", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            long revision;
            lock (session) revision = session.SelectionRevision;
            var body = await Body<SelectionBody>(context);
            session.Agent.Invalidate();
            CancelEditor(session);
            session.Setup.Invalidate();
            var shared = Require(await panel.ListAsync(context.RequestAborted));
            if (body.BenchId is { } selected && !shared.Benches.Any(value => value.Id == selected))
                throw new HttpFailure(404, "NotFound", "The selected Bench was deleted.");
            if (body.BenchId is { } candidate && session.RepositoryBindings is { } repository
                && !repository.AttachedBenches.Contains(candidate))
                throw new HttpFailure(403, "BenchNotAttached", "Choose an eligible Bench attached to this repository.");
            var committed = sessions.CommitViewChoice(session, Bearer(context), revision, () =>
            {
                if (session.CurrentBenchId != body.ExpectedBenchId)
                    throw new HttpFailure(409, "Conflict", "The selected Bench changed. Refresh first.");
                if (session.RepositoryBindings is not null)
                {
                    var retained = sessions.ReadState(session);
                    _ = sessions.SaveState(session, new(retained.Revision, retained.Choices with
                    {
                        BenchId = body.BenchId, ViewingRoot = null, AddRoot = null, DisplayRoots = [],
                        GroupSource = null, GroupField = null,
                    }));
                    session.NativeContext = null;
                    session.DisplayContexts.Clear();
                }
                session.CurrentBenchId = body.BenchId;
                session.RepositorySelectionReason = body.BenchId is null ? null : "Explicit";
                session.ReferencesRestored = true;
                session.PopulationContext = null;
                if (body.BenchId is null)
                {
                    session.NativeContext = null;
                    session.DisplayContexts.Clear();
                }
                return (session.SelectionRevision, RepositoryId: session.RepositoryBindings?.RepositoryId);
            });
            sessions.RememberRepositoryBench(session, body.BenchId, committed.SelectionRevision, committed.RepositoryId);
            await ResolveBenchConnections(panel, session, context.RequestAborted, cachedAdmissionOnly: session.RepositoryBindings is not null);
            updates.Publish(0);
            await Write(context, new Ack());
        });
        app.MapPost("/api/v1/views/{id:guid}/context/recheck", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            _ = await Body<EmptyBody>(context);
            session.Setup.Invalidate();
            var captured = Context(session);
            _ = ConnectionObservation(await panel.RecheckAsync(captured, context.RequestAborted), app.Logger);
            if (!ReferenceEquals(session.NativeContext, captured))
                throw new HttpFailure(409, "Conflict", "The selected connection changed during reauthorization. Recheck the intended connection.");
            updates.Publish(0);
            await Write(context, new Ack());
        });
        app.MapPut("/api/v1/views/{id:guid}/population-context", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var body = await Body<PopulationContextBody>(context);
            session.Agent.Invalidate();
            if (body.Root is null && session.NativeContext is null)
                throw new HttpFailure(409, "ConnectionRequired", "Choose a Bench connection first, or select an existing connection for Add items.");
            if (body.Root is not null) await RequireAvailableRoot(panel, session, body.Root, context.RequestAborted);
            var opened = body.Root is null ? null
                : ConnectionObservation(await panel.OpenAsync(body.Root, context.RequestAborted), app.Logger);
            if (opened is not null) RequireAvailable(session, opened.Stamp.Connection);
            lock (session)
            {
                session.PopulationContext = opened;
                if (opened is { } chosen) session.DisplayContexts[chosen.Stamp.Connection] = chosen;
            }
            updates.Publish(0);
            await Write(context, new Ack());
        });
        app.MapPut("/api/v1/views/{id:guid}/benches/{benchId:guid}", async (HttpContext context, Guid id, Guid benchId) =>
        {
            var session = await Authorize(context, id, true);
            var body = await Body<SaveNativeBenchBody>(context, DefinitionBodyLimit);
            var selectors = BenchCodec.DeserializeSelectors(body.Selectors);
            var library = Require(await panel.ListAsync(context.RequestAborted));
            var previous = library.Benches.SingleOrDefault(bench => bench.Id == benchId);
            var known = (previous?.Selectors ?? []).Select(selector => BenchCodec.SerializeSelectors([selector])).ToHashSet(StringComparer.Ordinal);
            foreach (var selector in selectors.Where(selector => !known.Contains(BenchCodec.SerializeSelectors([selector]))))
                if (selector.Connection is { } connection) RequireAvailable(session, connection);
            var connections = (previous?.Connections ?? []).Union(selectors.Select(selector => selector.Connection).OfType<Connection>());
            var saved = BenchDefinitionResult(await panel.SaveAsync(new NativeBench(benchId, body.Name, selectors,
                connections: connections), body.ExpectedRevision, context.RequestAborted));
            updates.Publish(0);
            await Write(context, Definition(saved));
        });
        app.MapDelete("/api/v1/views/{id:guid}/benches/{benchId:guid}", async (HttpContext context, Guid id, Guid benchId) =>
        {
            _ = await Authorize(context, id, true);
            var body = await Body<DeleteNativeBenchBody>(context);
            _ = BenchDefinitionResult(await panel.DeleteAsync(benchId, body.ExpectedRevision, context.RequestAborted));
            updates.Publish(0);
            await Write(context, new Ack());
        });
        app.MapPost("/api/v1/views/{id:guid}/benches/{benchId:guid}/add", async (HttpContext context, Guid id, Guid benchId) =>
        {
            _ = await Authorize(context, id, true);
            var body = await Body<AddNativeBenchBody>(context, DefinitionBodyLimit);
            var selectors = BenchCodec.DeserializeSelectors(body.Selectors);
            var saved = BenchDefinitionResult(await panel.AddAsync(benchId, selectors.ToImmutableArray(), context.RequestAborted));
            updates.Publish(0);
            await Write(context, Definition(saved));
        });
        app.MapPost("/api/v1/views/{id:guid}/profiles", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var profile = session.ProfileGeneration;
            var body = await Body<IdentityProfilesBody>(context);
            if (body.Identities is null || body.Identities.Length is < 1 or > 16
                || body.Identities.Distinct(StringComparer.Ordinal).Count() != body.Identities.Length
                || body.Identities.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)))
                throw new HttpFailure(422, "InvalidInput", "Request 1-16 distinct native assignee identities.");
            NativePanelContext source;
            lock (session) source = session.DisplayContexts.Values.SingleOrDefault(value => value.Source.Id == body.SourceId)
                ?? throw new HttpFailure(409, "ContextRequired", "Choose this item's native connection before loading its profile.");
            var profiles = ConnectionObservation(await panel.ReadIdentityProfilesAsync(source, body.Identities.ToImmutableArray(),
                context.RequestAborted), app.Logger);
            lock (session)
                if (session.ProfileGeneration != profile || !session.DisplayContexts.Values.Contains(source))
                    throw new HttpFailure(409, "SourceChanged", "The profile connection changed while loading.");
            await Write(context, profiles.ToArray());
        });
        app.MapGet("/api/v1/views/{id:guid}/benches/{benchId:guid}", async (HttpContext context, Guid id, Guid benchId) =>
        {
            var session = await Authorize(context, id, true);
            await ResolveBenchConnections(panel, session, context.RequestAborted);
            NativePanelContext[] chosen;
            NativePanelContext? anchor;
            UserRepositoryBindings? repository;
            lock (session)
            {
                chosen = session.DisplayContexts.Values.ToArray();
                anchor = session.NativeContext;
                repository = session.RepositoryBindings;
            }
            var observed = await panel.ReadViewAsync(benchId, anchor, chosen, context.RequestAborted,
                repository: repository, includeSeedReferences: true);
            if (!observed.IsSuccess && observed.Error.Evidence.Contains("bench-definition-changed"))
                throw new HttpFailure(409, "BenchViewChanged", "The Bench changed while loading. Read its current definition again.");
            var captured = ConnectionObservation(observed, app.Logger);
            var mapped = ViewMapping.ViewLabeled(captured.View, captured.Labels);
            _ = ConnectionObservation(await panel.CheckWorkspaceAsync(captured, context.RequestAborted,
                recheckLocalSnapshot: false), app.Logger);
            lock (session)
                if (!ReferenceEquals(anchor, session.NativeContext) || !ReferenceEquals(repository, session.RepositoryBindings)
                    || chosen.Length != session.DisplayContexts.Count || chosen.Any(source =>
                        !session.DisplayContexts.TryGetValue(source.Stamp.Connection, out var current) || !ReferenceEquals(source, current)))
                    throw new HttpFailure(409, "SourceChanged", "The source changed during the Bench read.");
            await Write(context, ComposeBenchView(mapped, captured, session, PresentSource));
        });
        app.MapGet("/api/v1/views/{id:guid}/benches/{benchId:guid}/access", async (HttpContext context, Guid id, Guid benchId) =>
        {
            var session = await Authorize(context, id, true);
            var profile = session.ProfileGeneration;
            var anchor = Context(session);
            var library = Require(await panel.ListAsync(context.RequestAborted));
            var bench = library.Benches.SingleOrDefault(value => value.Id == benchId)
                ?? throw new HttpFailure(404, "NotFound", "The selected Bench is unavailable.");
            var origins = Require(await panel.ReadSeedReferencesAsync(benchId, context.RequestAborted))
                .Select(r => (r.Connection, r.StoreInstance)).ToHashSet();
            NativePanelContext[] chosen;
            lock (session) chosen = session.DisplayContexts.Values.Append(anchor).Distinct().Where(value => value == anchor
                || NativePanel.Dependencies(bench, session.RepositoryBindings).Contains(value.Stamp.Connection)
                || origins.Contains((value.Stamp.Connection, value.Stamp.StoreInstance))).ToArray();
            if (chosen.Length > 32) throw new HttpFailure(422, "Unsupported", "The Bench exceeds the bounded access-check source count.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var checks = await Task.WhenAll(chosen.Select(source => panel.CheckHistoricalSourceAsync(source, timeout.Token)));
            foreach (var check in checks) _ = ConnectionObservation(check, app.Logger);
            lock (session)
                if (session.ProfileGeneration != profile || !ReferenceEquals(session.NativeContext, anchor)
                    || chosen.Any(source => !ReferenceEquals(source, anchor)
                        && (!session.DisplayContexts.TryGetValue(source.Stamp.Connection, out var current) || !ReferenceEquals(current, source))))
                    throw new HttpFailure(409, "SourceChanged", "The source or profile changed during the access check.");
            if (session.CurrentBenchId != benchId)
                throw new HttpFailure(409, "Conflict", "The selected Bench changed during the access check.");
            await Write(context, new Ack());
        });
        app.MapGet("/api/v1/views/{id:guid}/picker", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var source = await Population(panel, session, context.RequestAborted);
            var capture = ConnectionObservation(await panel.CaptureAsync(source, context.RequestAborted), app.Logger);
            var picker = PresentPicker(Picker(capture));
            _ = ConnectionObservation(await panel.CheckCaptureAsync(capture, context.RequestAborted), app.Logger);
            EnsurePopulation(session, source);
            await Write(context, picker);
        });
        app.MapPost("/api/v1/views/{id:guid}/planning/catalogue", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var body = await Body<PlanningContextBody>(context);
            var source = await PlanningPopulation(panel, session, context.RequestAborted);
            var observed = ConnectionObservation(await panel.ReadPlanningSourcesAsync(source, body.Team, context.RequestAborted), app.Logger);
            EnsurePopulation(session, source);
            diagnostics.Report(source.Source.Id, NativeDiagnosticArea.Sources, observed.PlanningSources?.Gaps ?? []);
            await Write(context, new NativeCatalogueDto(observed.PlanningSources is { } choices ? choices with { Gaps = [] } : null,
                observed.PlanningSourcesObservedAt));
        });
        app.MapPost("/api/v1/views/{id:guid}/planning/sources", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var body = await Body<PlanningContextBody>(context);
            var source = await PlanningPopulation(panel, session, context.RequestAborted);
            var observed = ConnectionObservation(await panel.DiscoverPlanningSourcesAsync(source, body.Team, context.RequestAborted), app.Logger);
            EnsurePopulation(session, source);
            var choices = observed.PlanningSources
                ?? throw new HttpFailure(503, "Unavailable", "The native discovery returned no saved-query/backlog metadata.");
            diagnostics.Report(source.Source.Id, NativeDiagnosticArea.Sources, choices.Gaps);
            await Write(context, choices with { Gaps = [] });
        });
        app.MapPost("/api/v1/views/{id:guid}/planning/browse", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var body = await Body<PlanningBrowseBody>(context);
            var source = await PlanningPopulation(panel, session, context.RequestAborted);
            var capture = ConnectionObservation(await panel.BrowsePlanningAsync(source, body.Kind, body.Id, body.Team, context.RequestAborted), app.Logger);
            EnsurePopulation(session, source);
            var browsed = capture.Observation.PlanningBrowse
                ?? throw new HttpFailure(503, "Unavailable", "The native acquisition returned no complete membership observation.");
            var query = new Query(browsed.Source.Kind == PlanningSourceKind.SavedQuery ? QueryKind.SavedQuery : QueryKind.Backlog,
                value: FieldValue.String(browsed.Source.Id), planningTeam: browsed.Team);
            await Write(context, new NativePlanningResultDto(PresentPicker(Picker(capture, browsed.Items)), browsed.Source, browsed.Team,
                BenchCodec.SerializeSelectors([new(SelectorKind.Query, query: query, connection: capture.Context.Stamp.Connection)]), browsed.ObservedAt));
        });
        app.MapGet("/api/v1/views/{id:guid}/drafts", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var capture = ConnectionObservation(await panel.CaptureAsync(Context(session), context.RequestAborted), app.Logger);
            var mapped = Mapped(Require(NativePanel.ProtectedDrafts(capture)), capture);
            var drafts = mapped with { Sources = mapped.Sources.Select(PresentSource).ToArray() };
            _ = ConnectionObservation(await panel.CheckCaptureAsync(capture, context.RequestAborted), app.Logger);
            if (!ReferenceEquals(session.NativeContext, capture.Context))
                throw new HttpFailure(409, "SourceChanged", "The selected source changed while loading drafts.");
            await Write(context, drafts);
        });
        app.MapGet("/api/v1/views/{id:guid}/planning", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var source = await Population(panel, session, context.RequestAborted);
            var capture = ConnectionObservation(await panel.CaptureAsync(source, context.RequestAborted), app.Logger);
            await Write(context, PresentPlanning(capture.Context.Source.Id, capture.Observation));
        });
        app.MapGet("/api/v1/views/{id:guid}/planning/teams", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var source = await PlanningPopulation(panel, session, context.RequestAborted);
            var teams = ConnectionObservation(await panel.DiscoverPlanningTeamsAsync(source, context.RequestAborted), app.Logger);
            EnsurePopulation(session, source);
            await Write(context, teams.ToArray());
        });
        app.MapPost("/api/v1/views/{id:guid}/planning/read", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var body = await Body<PlanningContextBody>(context);
            var source = await PlanningPopulation(panel, session, context.RequestAborted);
            var capture = ConnectionObservation(await panel.CapturePlanningChoicesAsync(source, body.Team, context.RequestAborted), app.Logger);
            EnsurePopulation(session, source);
            await Write(context, PresentPlanning(source.Source.Id, capture.Observation));
        });
        app.MapGet("/api/v1/views/{id:guid}/events", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id);
            using var subscription = updates.Subscribe(out var events);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,
                app.Lifetime.ApplicationStopping);
            lifetime.CancelAfter(session.Expires - DateTimeOffset.UtcNow);
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("event: ready\ndata: {}\n\n", lifetime.Token);
            await context.Response.Body.FlushAsync(lifetime.Token);
            try
            {
                await foreach (var change in events.ReadAllAsync(lifetime.Token))
                {
                    await Authorize(context, id);
                    if (change.AgentViewId is { } target && target != id) continue;
                    await context.Response.WriteAsync(change.Resync
                        ? "event: changed\ndata: {\"resync\":true}\n\n" : change.AgentViewId is not null
                        ? "event: agent\ndata: {}\n\n" : change.PresentationOnly
                        ? "event: presentation\ndata: {}\n\n" : "event: changed\ndata: {}\n\n", lifetime.Token);
                    await context.Response.Body.FlushAsync(lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (HttpFailure) { }
        });
        app.MapPost("/api/v1/views/{id:guid}/planning/refresh", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var body = await Body<PlanningContextBody>(context);
            var source = await PlanningPopulation(panel, session, context.RequestAborted);
            var observed = ConnectionObservation(await panel.RefreshPlanningAsync(source, body.Team, context.RequestAborted), app.Logger);
            EnsurePopulation(session, source);
            await Write(context, PresentPlanning(source.Source.Id, observed));
        });
        app.MapPost("/api/v1/views/{id:guid}/planning/rule", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var body = await Body<PlanningRuleBody>(context);
            var source = await PlanningPopulation(panel, session, context.RequestAborted);
            var expression = body.Iteration is { } text ? new IterationExpression(text) : null;
            var capture = ConnectionObservation(await panel.CapturePlanningChoicesAsync(source, body.Team, context.RequestAborted), app.Logger);
            if (expression?.IsRelative == true)
            {
                if (body.Team is null || capture.Observation.Schema.Planning?.Team != body.Team)
                    throw new HttpFailure(409, "PlanningRequired", "Choose a team and refresh its calendar before pinning a relative iteration.");
                capture = ConnectionObservation(await panel.CapturePlanningAsync(source, body.Team, context.RequestAborted), app.Logger);
            }
            EnsurePopulation(session, source);
            if (capture.Context.Stamp.Connection.Provider.Value != "ado")
                throw new HttpFailure(422, "Unsupported", "Area and iteration rules require an ADO planning connection.");
            var conditions = new List<Query>();
            if (body.Area is { } area)
            {
                if (capture.Observation.Schema.Planning is not { } metadata || !metadata.Areas.Contains(area, StringComparer.OrdinalIgnoreCase))
                    throw new HttpFailure(409, "PlanningChanged", "Choose an area from the refreshed native planning metadata.");
                conditions.Add(new(body.IncludeChildren ? QueryKind.Under : QueryKind.PathEqual, "System.AreaPath", FieldValue.String(area)));
            }
            if (body.Iteration is { } iteration)
            {
                expression ??= new IterationExpression(iteration);
                if (expression.IsRelative && (body.Team is null || capture.Observation.Schema.Planning?.Team != body.Team))
                    throw new HttpFailure(409, "PlanningRequired", "Choose a team and refresh its calendar before pinning a relative iteration.");
                if (!expression.IsRelative && !(capture.Observation.Schema.Planning?.Iterations
                    .Any(value => value.Path.Equals(expression.Text, StringComparison.OrdinalIgnoreCase)) ?? false))
                    throw new HttpFailure(409, "PlanningChanged", "Choose an iteration from the refreshed planning choices.");
                conditions.Add(new(QueryKind.Iteration, "System.IterationPath", FieldValue.Strings([expression.Text]),
                    planningTeam: expression.IsRelative ? capture.Observation.Schema.Planning!.Team : null));
            }
            if (body.AssignedToMe)
            {
                if (!Planning(capture.Observation).CanFilterOwnership)
                    throw new HttpFailure(422, "Unsupported", "Canonical assignment identity is unavailable for this process.");
                conditions.Add(new(QueryKind.Equal, "System.AssignedTo", FieldValue.Identity(capture.Context.Stamp.PrincipalId)));
            }
            if (conditions.Count == 0) throw new HttpFailure(400, "InvalidInput", "Choose an area, iteration or canonical ownership condition.");
            var query = conditions.Count == 1 ? conditions[0] : new(QueryKind.And, children: conditions);
            await Write(context, new PlanningRuleDto(BenchCodec.SerializeSelectors(
                [new(SelectorKind.Query, query: query, connection: capture.Context.Stamp.Connection)])));
        });
        MapPersistentSessions(app, panel, sessions, (context, id) => Authorize(context, id, true), options.WindowsSignIn, PresentSource, ReadAccounts, options.ExecutionV2);
        MapEditor(app, panel, sessions, updates, (context, id) => Authorize(context, id, true));
        MapSeeds(app, panel, sessions, updates, (context, id) => Authorize(context, id, true));
        MapPlan(app, panel, sessions, updates, (context, id) => Authorize(context, id, true));
        MapAgent(app, panel, sessions, updates, (context, id) => Authorize(context, id, true), options);
        MapSetup(app, connectionSetup, panel, sessions, updates, (context, id) => Authorize(context, id, true));
        MapRepositories(app, panel, sessions, updates, connectionSetup, (context, id) => Authorize(context, id, true));
        var root = options.WebRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        foreach (var route in new[] { "/", "/canvas", "/app.js", "/style.css", "/view-model.js", "/item-detail.js", "/assignee-profiles.js", "/item-editor.js", "/editor-drafts.js", "/connection-setup.js",
            "/vendor/purify.es.mjs", "/vendor/marked.esm.js", "/icons.css" })
            app.MapMethods(route, ["GET", "HEAD"], async (HttpContext context) =>
            {
                var name = route is "/" or "/canvas" ? "native-index.html"
                    : route == "/app.js" ? "native-app.js" : route.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                if (options.CanvasInstallRoot is { } installation)
                {
                    try
                    {
                        var asset = CanvasAssets.Read(installation, name);
                        context.Response.Headers["X-Twig-Canvas-Version"] = asset.Version;
                        context.Response.Headers.CacheControl = "no-store";
                        context.Response.ContentType = ContentType();
                        context.Response.ContentLength = asset.Bytes.Length;
                        if (!HttpMethods.IsHead(context.Request.Method))
                            await context.Response.Body.WriteAsync(asset.Bytes, context.RequestAborted);
                    }
                    catch (FileNotFoundException)
                    {
                        await Write(context, new Failure("CanvasNotInstalled", "Install a compatible Twig canvas separately; the server API remains available."), 404);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        await Write(context, new Failure("CanvasNotInstalled", "Install a compatible Twig canvas separately; the server API remains available."), 404);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
                        or InvalidOperationException or ArgumentException or FormatException or OverflowException)
                    {
                        app.Logger.LogError(error, "Canvas package verification failed");
                        await Write(context, new Failure("CanvasPackageInvalid", "Canvas compatibility or integrity verification failed; no unverified content was served."), 503);
                    }
                    return;
                }
                var file = Path.Combine(root, name);
                if (!File.Exists(file))
                {
                    await Write(context, new Failure("CanvasNotInstalled", "Install a compatible Twig canvas separately, or use the server API."), 404);
                    return;
                }
                context.Response.ContentType = ContentType();
                if (HttpMethods.IsHead(context.Request.Method)) context.Response.ContentLength = new FileInfo(file).Length;
                else await context.Response.SendFileAsync(file, context.RequestAborted);
                string ContentType() => route.EndsWith(".js", StringComparison.Ordinal) || route.EndsWith(".mjs", StringComparison.Ordinal)
                    ? "text/javascript" : route.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/html";
            });
        return app;

        async Task<NativeAadEnrollment[]> ReadAccounts(CancellationToken ct)
        {
            if (connectionSetup is null) return [];
            var accounts = await connectionSetup.ListAadIdentitiesAsync(ct);
            if (accounts.IsSuccess) return accounts.Value;
            app.Logger.LogWarning("Attached connection account metadata is unavailable: {Code}", accounts.Error.Code);
            return [];
        }

        SourceViewDto PresentSource(SourceViewDto source)
        {
            diagnostics.Report(source.SourceId, NativeDiagnosticArea.Items, source.Limitations.Concat(source.TreeLimitations),
                source.ErrorCode is null ? null : Enum.Parse<ErrorCode>(source.ErrorCode),
                source.Mode is null ? null : Enum.Parse<ObservationMode>(source.Mode));
            return source with { Limitations = [], TreeLimitations = [] };
        }
        NativePickerDto PresentPicker(NativePickerDto picker) => picker with { Source = PresentSource(picker.Source) };
        NativePlanningDto PresentPlanning(Guid source, SourceObservation observation)
        {
            var value = Planning(observation);
            diagnostics.Report(source, NativeDiagnosticArea.Planning, value.Metadata?.Gaps ?? []);
            return value.Metadata is null ? value : value with { Metadata = value.Metadata with { Gaps = [] } };
        }

        async Task<ViewSession> Authorize(HttpContext context, Guid id, bool content = false)
        {
            var session = sessions.Content(id, Bearer(context), content);
            if (session.CatalogId != Require(await panel.ListAsync(context.RequestAborted)).InstanceId)
            { sessions.Revoke(session); throw new HttpFailure(409, "StoreReplaced", "The native Bench store changed. Reconnect."); }
            if (!session.OwnerAuthenticated)
                throw new HttpFailure(403, "OwnerRequired", "Connect with the same Windows owner. SDK layout attachment grants no content authority.");
            context.Items[typeof(ViewSession)] = session;
            if (!context.Request.Path.Value!.EndsWith("/repository-bindings", StringComparison.Ordinal)
                && !context.Request.Path.Value.EndsWith("/repository-context", StringComparison.Ordinal))
                await SynchronizeRepository(panel, sessions, session, context.RequestAborted);
            return session;
        }
    }

    private static WorkspaceDto? ConnectionSummary(NativePanelContext? context) => context is null ? null
        : new(context.Root, context.Stamp.Connection.Provider.Value, context.Source.Label, context.LabelAvailable)
            { Connection = context.Stamp.Connection };

    private static T BenchDefinitionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return result.Value;
        throw new HttpFailure(result.Error.Code switch
        {
            ErrorCode.InvalidInput => 400, ErrorCode.NotFound => 404, ErrorCode.Conflict => 409, _ => 503,
        }, result.Error.Code == ErrorCode.Conflict ? "BenchChanged" : result.Error.Code.ToString(),
            result.Error.Code switch
            {
                ErrorCode.Conflict => "This Bench changed while you were editing. Your choices are still here; refresh its settings before trying again.",
                ErrorCode.NotFound => "This Bench was deleted. Choose another Bench; no work items were changed.",
                ErrorCode.InvalidInput => result.Error.Message,
                _ => "The Bench change could not be confirmed. Reload its saved scope before trying again. No work items in Azure DevOps were changed.",
            });
    }

    private static T ConnectionObservation<T>(Result<T> result, ILogger logger)
    {
        if (result.IsSuccess) return result.Value;
        var error = result.Error;
        logger.LogWarning("Native connection observation refused ({Code}); no substitute identity or content was used.", error.Code);
        var unauthorized = error.Evidence.Contains("http:401");
        var forbidden = error.Evidence.Contains("http:403");
        var guidance = error.Code == ErrorCode.InvalidInput
            ? "Choose an existing connection, or provide its initialized workspace in Advanced."
            : unauthorized
                ? "The provider could not authenticate this account. Sign in to the intended account; your saved connection is still selected."
            : forbidden
                ? "This account does not have access to the requested source. Your saved connection and choices were kept."
                : error.Code == ErrorCode.PolicyViolation
                    ? "This request was refused by the source policy. Your connection is still selected; review the requested operation before trying again."
                : error.Code == ErrorCode.Conflict
                    ? "The requested source changed while loading. Refresh it before trying again; no changes were replayed."
                : error.Code == ErrorCode.StorageUnavailable
                    ? "The initialized workspace or its native cache is unavailable. Open that existing workspace in the Twig CLI and verify its connection; nothing was initialized or replaced."
                    : "This request could not finish. Try it again; your saved connection and choices have not been removed.";
        throw new HttpFailure(unauthorized || forbidden ? 403 : error.Code switch
        {
            ErrorCode.InvalidInput => 400, ErrorCode.NotFound => 404, ErrorCode.Conflict => 409,
            ErrorCode.PolicyViolation => 403, _ => 503,
        }, unauthorized ? "ProviderAuthenticationRequired" : forbidden ? "ProviderAccessDenied"
            : error.Code == ErrorCode.Conflict ? "SourceChanged" : error.Code.ToString(), guidance);
    }

    private static T Require<T>(Result<T> result)
    {
        if (result.IsSuccess) return result.Value;
        throw new HttpFailure(result.Error.Code switch
        {
            ErrorCode.InvalidInput => 400, ErrorCode.NotFound => 404, ErrorCode.Conflict => 409,
            ErrorCode.PolicyViolation => 403, ErrorCode.Unsupported => 422, _ => 503,
        }, result.Error.Code.ToString(), result.Error.Code switch
        {
            ErrorCode.Conflict => "This view changed. Refresh its current state before trying again; your choices were kept.",
            ErrorCode.InvalidInput => "Choose valid options for this operation.",
            ErrorCode.NotFound => "The requested item or Bench is no longer available.",
            ErrorCode.PolicyViolation => "The requested operation was refused. Your connection has not been removed.",
            ErrorCode.Unsupported => "This source does not support the requested operation.",
            _ => "This request could not be completed. Your choices were kept; try again.",
        });
    }

    private static NativePanelContext Context(ViewSession session) => session.NativeContext
        ?? throw new HttpFailure(409, "ContextRequired", "Choose an existing Twig context first.");
    private static async Task<NativePanelContext> Population(NativePanel panel, ViewSession session, CancellationToken ct)
    {
        if (session.PopulationContext is { } selected) return selected;
        var library = Require(await panel.ListAsync(ct));
        var bench = library.Benches.SingleOrDefault(value => value.Id == session.CurrentBenchId);
        if (bench is not null && NativePanel.Dependencies(bench, session.RepositoryBindings).Take(2).ToArray() is [var only]
            && session.NativeContext is { } context && context.Stamp.Connection == only
            && session.RepositoryBindings is not { DeclaredSourceStatus: not null, DeclaredConnection: null }) return context;
        throw new HttpFailure(409, "DestinationRequired", "Choose an explicit Add items connection. No default destination was inferred from the healthy sources.");
    }
    private static async Task<NativePanelContext> PlanningPopulation(NativePanel panel, ViewSession session, CancellationToken ct)
    {
        var source = await Population(panel, session, ct);
        return source.Stamp.Connection.Provider.Value == "ado" ? source
            : throw new HttpFailure(422, "Unsupported", "Teams, queries, backlogs and planning rules require an Azure DevOps source.");
    }
    private static void EnsurePopulation(ViewSession session, NativePanelContext captured)
    {
        if (!ReferenceEquals(session.PopulationContext ?? session.NativeContext, captured))
            throw new HttpFailure(409, "Conflict", "The Add items source changed. Load choices for the currently selected source.");
    }
    private static NativeBenchDto Definition(NativeBench bench)
        => new(bench.Id, bench.Name, bench.Revision, BenchCodec.SerializeSelectors(bench.Selectors));

    private static NativeBenchViewDto ComposeBenchView(BenchViewDto mapped, NativePanelView captured, ViewSession session,
        Func<SourceViewDto, SourceViewDto> presentSource)
        => new(mapped.CatalogId, mapped.CatalogRevision, mapped.Id, mapped.Name,
            mapped.Sources.Select(source => presentSource(source) with
            {
                NormalDraftCount = captured.NormalDraftCounts.GetValueOrDefault(source.SourceId),
                Items = source.Items.Select(item => item.IsLocal && !captured.Plan.Enabled
                    ? item with { Pin = null } : item).ToArray(),
            }).ToArray())
        { Plan = PlanStatus(session, captured.Plan) };

    private static NativeBenchViewDto Mapped(BenchView view, NativePanelCapture capture)
    {
        var projected = ViewMapping.View(view, new(capture.Definitions.InstanceId, capture.Definitions.Revision, [capture.Context.Source], [view.Definition]));
        return new(capture.Definitions.InstanceId, capture.Definitions.Revision, projected.Id, projected.Name, projected.Sources);
    }
    private static NativePlanningDto Planning(SourceObservation observation)
        => new(observation.Schema.Planning, observation.Binding.Connection.Provider.Value == "ado"
            && observation.Schema.Types.Any(type => type.Fields.Any(field => field.Reference == "System.AssignedTo" && field.Kind == FieldKind.Identity)));

    private static NativePickerDto Picker(NativePanelCapture capture, ImmutableArray<ItemKey>? selected = null)
    {
        var source = capture.Context.Source;
        var observation = capture.Observation;
        ItemKey Resolve(ItemKey key) => observation.Snapshot.Mappings.FirstOrDefault(mapping => mapping.Local == key)?.Remote ?? key;
        if (selected is { } acquired)
        {
            var page = observation.AcquiredPlanningPage;
            var keysInResult = acquired.ToHashSet();
            if (page is null || !page.Complete || page.Continuation is not null
                || page.Items.Length != keysInResult.Count || !keysInResult.SetEquals(page.Items.Select(item => item.Key))
                || page.Items.Any(item => !item.Key.BelongsTo(source.Binding.Connection)))
                throw new HttpFailure(503, "Unavailable", "The native acquisition returned no exact work-item page.");
            var outside = page.Relationships.Where(edge => !keysInResult.Contains(edge.From) || !keysInResult.Contains(edge.To))
                .SelectMany(edge => new[] { edge.From, edge.To }).ToHashSet();
            observation = observation with
            {
                Snapshot = new(observation.Snapshot.Version, page.Items.Select(item => outside.Contains(item.Key)
                    ? new WorkItem(item.Key, item.Type, item.Fields, item.Revision, false, item.Resources, item.ResourcesComplete)
                    : item),
                    page.Relationships.Where(edge => edge.Origin == RelationshipOrigin.Provider
                        && keysInResult.Contains(edge.From) && keysInResult.Contains(edge.To))),
                WithdrawnItems = []
            };
            capture = capture with { Observation = observation };
        }
        var selection = new SourceSelection(source.Id, source.Binding,
            [new(SelectorKind.Query, query: new(QueryKind.All), connection: source.Binding.Connection)]);
        var definition = new BenchDefinition(Guid.NewGuid(), "Find items", 1, [selection]);
        var projected = Require(SourceProjection.Create(definition, selection, observation with { WorkspaceExclusions = [] }));
        var mapped = Mapped(new(definition, capture.Definitions.InstanceId, capture.Definitions.Revision, [projected]), capture).Sources.Single();
        var keys = (selected ?? observation.Snapshot.Items.Select(item => item.Key)
            .Concat(observation.Snapshot.RetainedItems.Select(draft => draft.Key)).ToImmutableArray()).Select(Resolve).Distinct().ToArray();
        var identities = mapped.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var hierarchy = observation.Schema.Relations.Where(relation => relation.Directed && relation.Shape == RelationShape.Hierarchy).ToArray();
        var pins = keys.Where(key => identities.Contains(ViewMapping.Key(new(source.Id, key))))
            .Select(key => new PickerPinDto(ViewMapping.Key(new(source.Id, key)),
            BenchCodec.SerializeSelectors([new(SelectorKind.Pin, key, connection: source.Binding.Connection)]),
            selected is null && hierarchy.Length == 1
                && observation.Snapshot.Items.Any(item => item.Key == key && item.RelationshipsComplete)
                ? BenchCodec.SerializeSelectors([new(SelectorKind.Subtree, key, hierarchyRelation: hierarchy[0].Reference,
                    connection: source.Binding.Connection)]) : null)).ToArray();
        return new(mapped, pins);
    }
}
