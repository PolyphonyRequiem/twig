using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Negotiate;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bench.Application;
using Bench.Domain;
using Bench.Infrastructure;
using Twig.Core;

namespace Bench.Server;

public sealed record ServerOptions(int Port, string DataDirectory, string? WebRoot = null,
    string DataMode = "Local browser; canvas defaults to exact-source denial", X509Certificate2? Certificate = null,
    bool WindowsSignIn = false, string? CanvasInstallRoot = null, Guid DaemonId = default);

public static class BenchHost
{
    internal static string WindowsOwner()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current Windows user identity is unavailable.");
    }
    internal static bool IsWindowsOwner(ClaimsPrincipal principal, string owner)
        => OperatingSystem.IsWindows() && principal.Identity is WindowsIdentity identity && identity.User?.Value == owner;

    public static WebApplication Build(ServerOptions options, ICatalogStore store, ISourceReader reader,
        CatalogChanges changes, ViewSessions sessions, DeviceEnrollments? devices = null, CanvasApproval? approval = null)
    {
        if (devices is not null && (options.Certificate is null || options.Port == 0 || approval is null))
            throw new ArgumentException("Device enrollment requires a stable HTTPS endpoint and exact canvas approval.");
        if (options.WindowsSignIn && (!OperatingSystem.IsWindows() || devices is null))
            throw new ArgumentException("Windows sign-in requires the secure, exact-source approved device endpoint on Windows.");
        var application = new BenchCatalog(store, reader, changes);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        if (options.WindowsSignIn)
        {
            var owner = WindowsOwner();
            builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
            builder.Services.AddAuthorization(authorization => authorization.AddPolicy("WindowsOwner", policy =>
                policy.AddAuthenticationSchemes(NegotiateDefaults.AuthenticationScheme).RequireAuthenticatedUser()
                    .RequireAssertion(context => IsWindowsOwner(context.User, owner))));
        }
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Logging.AddFilter((category, level) => category == "Bench.SourceDiagnostics" && level >= LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(server =>
        {
            if (options.Certificate is null) server.Listen(IPAddress.Loopback, options.Port);
            else server.ListenLocalhost(options.Port, endpoint => endpoint.UseHttps(options.Certificate));
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 16384;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.Limits.MaxConcurrentConnections = 32;
        });
        var app = builder.Build();
        var diagnosticsLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Bench.SourceDiagnostics");
        var reportedDiagnostics = new Dictionary<Guid, string>();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            // Browser document cannot be embedded. /canvas is the same UI with native data hard-blocked.
            var framing = context.Request.Path == "/" ? "; frame-ancestors 'none'" : "";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'" + framing;
            var host = $"{(options.Certificate is null ? "127.0.0.1" : "localhost")}:{context.Connection.LocalPort}";
            var expectedOrigin = $"{context.Request.Scheme}://{host}";
            var origin = context.Request.Headers.Origin.ToString();
            var bootstrap = (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && context.Request.Path.Value is "/" or "/canvas" or "/app.js" or "/device.js" or "/style.css" or "/view-model.js" or "/item-detail.js"
                    or "/vendor/purify.es.mjs" or "/vendor/marked.esm.js" or "/icons.css" or "/fonts/fluent-regular-v1.1.293.woff2";
            if (context.Request.Host.Value != host || !bootstrap && (origin.Length != 0 && origin != expectedOrigin
                || context.Request.Headers["Sec-Fetch-Site"] == "cross-site"))
            { await Write(context, new Failure("OriginRejected", "Use the exact local service origin."), 403); return; }
            if (context.Request.Path.StartsWithSegments("/api")
                && (context.Request.QueryString.HasValue || context.Request.Method != "GET" && context.Request.Headers["X-Twig-Bench"] != "1"))
            { await Write(context, new Failure("InvalidTransport", "Use capability headers and bounded JSON, never URL secrets."), 403); return; }
            try { await next(context); }
            catch (HttpFailure error) when (!context.Response.HasStarted)
            { await Write(context, new Failure(error.Code, error.Message), error.Status); }
            catch (Exception error) when (!context.Response.HasStarted && error is JsonException or BadHttpRequestException or ArgumentException)
            { await Write(context, new Failure("InvalidInput", "The request does not match the bounded API contract."), 400); }
            catch (OperationCanceledException) when (!context.Response.HasStarted)
            { await Write(context, new Failure("Indeterminate", "Request cancelled or timed out. Reconcile the same creation UUID before retrying."), 504); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (!context.Response.HasStarted)
                    await Write(context, new Failure("Unavailable", "Local service data is unavailable; nothing was reset."), 503);
            }
        });
        if (options.WindowsSignIn)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }
        app.MapGet("/api/v1/status", (HttpContext context) => Write(context,
            new ServiceStatus("twig-bench-service-v1", options.DataMode, true, options.DaemonId,
                CanvasAssets.DaemonVersion, ["legacy-read-only"])));
        app.MapPost("/api/v1/canvas-views", async (HttpContext context) =>
        {
            _ = await Body<EmptyBody>(context);
            var catalog = Require(await store.ReadAsync(context.RequestAborted));
            await Write(context, sessions.RegisterCanvas(catalog));
        });
        app.MapPost("/api/v1/pair", async (HttpContext context) =>
        {
            var body = await Body<PairBody>(context);
            if (options.WindowsSignIn && body.ViewId is not null)
                throw new HttpFailure(403, "PairingRetired", "This secure canvas uses Windows sign-in, not pairing codes.");
            var catalog = Require(await store.ReadAsync(context.RequestAborted));
            await Write(context, sessions.Pair(body, catalog));
            changes.Publish(0);
        });
        app.MapGet("/api/v1/device-enrollment", (HttpContext context) =>
            Write(context, new EnrollmentAvailability(devices is not null, options.WindowsSignIn)));
        if (options.WindowsSignIn)
            app.MapPost("/api/v1/windows-enroll", async (HttpContext context) =>
            {
                var body = await Body<WindowsEnrollment>(context);
                if (body.ProfileId == Guid.Empty || body.ViewId == Guid.Empty || body.DeviceId == Guid.Empty)
                    throw new HttpFailure(400, "InvalidInput", "A valid view and local device are required.");
                var catalog = Require(await store.ReadAsync(context.RequestAborted));
                var paired = Devices().EnrollWindows(body, approval, catalog, sessions);
                await Write(context, paired);
                changes.Publish(0);
            }).RequireAuthorization("WindowsOwner");
        app.MapPost("/api/v1/views/{id:guid}/device", async (HttpContext context, Guid id) =>
        {
            var enrollments = Devices();
            var session = await Authorize(context, id, true);
            var captured = await Capture(session, context.RequestAborted);
            await Write(context, enrollments.Register(session, await Body<DeviceRegistration>(context), captured));
        });
        app.MapPost("/api/v1/device-challenge", async (HttpContext context) =>
        {
            var enrollments = Devices();
            var body = await Body<DeviceChallengeRequest>(context);
            var captured = Require(await store.ReadAsync(context.RequestAborted));
            await Write(context, enrollments.Challenge(body, approval, captured));
        });
        app.MapPost("/api/v1/device-resume", async (HttpContext context) =>
        {
            var enrollments = Devices();
            var body = await Body<DeviceProof>(context);
            var captured = Require(await store.ReadAsync(context.RequestAborted));
            await Write(context, enrollments.Resume(body, approval, captured, sessions));
            changes.Publish(0);
        });
        app.MapDelete("/api/v1/views/{id:guid}/device", async (HttpContext context, Guid id) =>
        {
            var enrollments = Devices();
            var session = await Authorize(context, id, true);
            var captured = await Capture(session, context.RequestAborted);
            sessions.ForgetDevice(enrollments.Forget(session, captured));
            changes.Publish(0);
            await Write(context, new Ack());
        });
        DeviceEnrollments Devices() => devices
            ?? throw new HttpFailure(403, "DeviceEnrollmentDisabled", "Remembering devices requires a configured stable HTTPS endpoint.");
        app.MapPost("/api/v1/views/{id:guid}/attachment", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, false);
            _ = await Capture(session, context.RequestAborted);
            _ = await Body<EmptyBody>(context);
            sessions.AttachPresentation(id, Bearer(context));
            changes.Publish(0);
            await Write(context, new Ack());
        });
        app.MapPut("/api/v1/views/{id:guid}/layout", async (HttpContext context, Guid id) =>
        {
            var session = sessions.Presentation(id, Bearer(context));
            _ = await Capture(session, context.RequestAborted);
            sessions.SetLayout(id, Bearer(context), await Body<LayoutBody>(context));
            changes.Publish(0);
            await Write(context, new Ack());
        });
        app.MapDelete("/api/v1/views/{id:guid}", async (HttpContext context, Guid id) =>
        {
            sessions.Close(id, Bearer(context)); changes.Publish(0);
            await Write(context, new Ack());
        });
        app.MapGet("/api/v1/views/{id:guid}/session", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, false);
            await Write(context, new SessionInfo(session.CatalogId, session.NativeContentBlocked, session.Preferences));
        });
        app.MapPut("/api/v1/views/{id:guid}/session", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var body = await Body<ViewBody>(context);
            sessions.Update(session, new(body.ExpectedRevision, body.BenchId, body.Layout, body.GroupSource, body.GroupField), body.ExpectedRevision);
            changes.Publish(0);
            await Write(context, session.Preferences);
        });
        app.MapPut("/api/v1/views/{id:guid}/presentation", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, false);
            var body = await Body<LayoutBody>(context);
            sessions.Update(session, session.Preferences with { Layout = body.Layout });
            changes.Publish(0);
            await Write(context, new Ack());
        });
        app.MapGet("/api/v1/views/{id:guid}/presentation", async (HttpContext context, Guid id) =>
        {
            var session = sessions.Presentation(id, Bearer(context));
            _ = await Capture(session, context.RequestAborted);
            await Write(context, new Ack());
        });
        app.MapDelete("/api/v1/views/{id:guid}/session", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, false);
            sessions.Revoke(session); changes.Publish(0);
            await Write(context, new Ack());
        });
        app.MapGet("/api/v1/views/{id:guid}/catalog", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, true);
            var captured = await Capture(session, context.RequestAborted);
            await Write(context, ViewMapping.Catalog(session.Approval?.Filter(captured) ?? captured));
        });
        app.MapPost("/api/v1/views/{id:guid}/sources", async (HttpContext context, Guid id) =>
        {
            var session = await Browser(context, id);
            var body = await Body<SourceBody>(context);
            if (body.CatalogId != session.CatalogId) throw new HttpFailure(409, "Conflict", "Catalog capture changed.");
            using var timeout = Deadline(context);
            _ = Require(await application.RegisterAsync(body.Id, body.Label, body.WorkspaceRoot, body.AllowOffline,
                body.CatalogId, body.ExpectedRevision, timeout.Token));
            await Write(context, new Ack());
        });
        app.MapPost("/api/v1/views/{id:guid}/sources/{sourceId:guid}/recheck", async (HttpContext context, Guid id, Guid sourceId) =>
        {
            var session = await Browser(context, id);
            _ = await Body<EmptyBody>(context);
            var source = (await Capture(session, context.RequestAborted)).Sources.SingleOrDefault(s => s.Id == sourceId)
                ?? throw new HttpFailure(404, "NotFound", "Source not registered.");
            if (reader is not NativeSourceAdapter native) throw new HttpFailure(422, "Unsupported", "Recheck unavailable for this source adapter.");
            using var timeout = Deadline(context);
            _ = Require(await native.RecheckAsync(source, timeout.Token));
            await Write(context, new Ack());
        });
        app.MapGet("/api/v1/views/{id:guid}/sources/{sourceId:guid}/native-benches", async (HttpContext context, Guid id, Guid sourceId) =>
        {
            var session = await Browser(context, id);
            var source = (await Capture(session, context.RequestAborted)).Sources.SingleOrDefault(s => s.Id == sourceId)
                ?? throw new HttpFailure(404, "NotFound", "Source not registered.");
            using var timeout = Deadline(context);
            var observed = Require(await reader.ReadAsync(source, timeout.Token));
            _ = await Capture(session, timeout.Token);
            await Write(context, observed.Snapshot.Benches.Select(b => new NativeBenchSummary(b.Id, b.Name, b.Selectors.Length)).ToArray());
        });
        app.MapPost("/api/v1/views/{id:guid}/creations/preview", async (HttpContext context, Guid id) =>
        {
            var session = await Browser(context, id);
            var body = await Body<PrepareBody>(context);
            using var timeout = Deadline(context);
            var command = Require(await application.PrepareCreateAsync(body.Id, body.Name, session.CatalogId, body.ExpectedRevision,
                body.Seeds.Select(s => new NativeBenchSeed(s.SourceId, s.NativeBenchId)).ToImmutableArray(), timeout.Token));
            lock (session) session.PendingCreation = command;
            await Write(context, new CreatePreview(command.Definition.Id, command.Definition.Name, command.Definition.Sources.Length,
                command.Definition.Sources.Sum(s => s.Selectors.Length), "Independent selector copy. Later native Bench edits do not change this definition. No selector editor is included."));
        });
        app.MapPost("/api/v1/views/{id:guid}/creations/confirm", async (HttpContext context, Guid id) =>
        {
            var session = await Browser(context, id);
            var body = await Body<ConfirmBody>(context);
            CreateBenchCommand command;
            lock (session) command = session.PendingCreation is { } pending && pending.Definition.Id == body.Id
                ? pending : throw new HttpFailure(409, "PreviewRequired", "Preview this exact creation before confirming.");
            using var timeout = Deadline(context);
            var receipt = Require(await application.CreateAsync(command, timeout.Token));
            await Write(context, new Created(receipt.Definition.Id, receipt.Definition.Name, receipt.CatalogId, receipt.CatalogRevision));
        });
        app.MapGet("/api/v1/views/{id:guid}/creations/{creationId:guid}", async (HttpContext context, Guid id, Guid creationId) =>
        {
            var session = await Browser(context, id);
            using var timeout = Deadline(context);
            var receipt = Require(await application.FindCreationAsync(session.CatalogId, creationId, timeout.Token));
            await Write(context, new Created(receipt.Definition.Id, receipt.Definition.Name, receipt.CatalogId, receipt.CatalogRevision));
        });
        app.MapGet("/api/v1/views/{id:guid}/benches/{benchId:guid}", async (HttpContext context, Guid id, Guid benchId) =>
        {
            var session = await Authorize(context, id, true);
            if (session.Approval is { } approval && benchId != approval.BenchId)
                throw new HttpFailure(403, "CanvasScopeDenied", "This Bench is outside the approved display scope.");
            using var timeout = Deadline(context);
            var view = Require(await application.ReadAsync(benchId, timeout.Token, session.Approval?.BenchFingerprint));
            var current = Require(await store.ReadAsync(timeout.Token));
            if (view.CatalogId != session.CatalogId || current.Id != session.CatalogId)
            { sessions.Revoke(session); throw new HttpFailure(409, "CatalogReplaced", "Catalog changed during capture; pair again."); }
            if (session.Approval is { } scope && !scope.Matches(current))
            { sessions.Revoke(session); throw new HttpFailure(403, "CanvasScopeChanged", "Approved source/definition changed during capture."); }
            foreach (var source in view.Sources)
            {
                var diagnostics = string.Join(Environment.NewLine, source.Limitations.Concat(source.Hierarchy.Limitations).Distinct());
                if (diagnostics.Length == 0) continue;
                lock (reportedDiagnostics)
                {
                    if (reportedDiagnostics.TryGetValue(source.SourceId, out var previous) && previous == diagnostics) continue;
                    reportedDiagnostics[source.SourceId] = diagnostics;
                }
                diagnosticsLogger.LogWarning("Source {SourceId}: {Diagnostics}", source.SourceId, diagnostics);
            }
            await Write(context, ViewMapping.View(view, current));
        });
        app.MapGet("/api/v1/views/{id:guid}/events", async (HttpContext context, Guid id) =>
        {
            var session = await Authorize(context, id, false);
            using var subscription = changes.Subscribe(out var events);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            lifetime.CancelAfter(session.Expires - DateTimeOffset.UtcNow);
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("event: ready\ndata: {}\n\n", lifetime.Token);
            await context.Response.Body.FlushAsync(lifetime.Token);
            try
            {
                await foreach (var revision in events.ReadAllAsync(lifetime.Token))
                {
                    _ = await Authorize(context, id, false);
                    await context.Response.WriteAsync("event: changed\ndata: {}\n\n", lifetime.Token);
                    await context.Response.Body.FlushAsync(lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (HttpFailure) { }
        });
        var root = options.WebRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        foreach (var route in new[] { "/", "/canvas", "/app.js", "/device.js", "/style.css", "/view-model.js", "/item-detail.js",
            "/vendor/purify.es.mjs", "/vendor/marked.esm.js", "/icons.css", "/fonts/fluent-regular-v1.1.293.woff2" })
            app.MapMethods(route, ["GET", "HEAD"], async (HttpContext context) =>
            {
                var name = route is "/" or "/canvas" ? "index.html" : route.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var path = Path.Combine(root, name);
                context.Response.ContentType = route.EndsWith(".js", StringComparison.Ordinal) || route.EndsWith(".mjs", StringComparison.Ordinal) ? "text/javascript"
                    : route.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : route.EndsWith(".woff2", StringComparison.Ordinal) ? "font/woff2" : "text/html";
                if (HttpMethods.IsHead(context.Request.Method)) context.Response.ContentLength = new FileInfo(path).Length;
                else await context.Response.SendFileAsync(path, context.RequestAborted);
            });
        return app;

        async Task<ViewSession> Authorize(HttpContext context, Guid id, bool native)
        {
            var session = sessions.Content(id, Bearer(context), native);
            _ = await Capture(session, context.RequestAborted);
            return session;
        }
        async Task<ViewSession> Browser(HttpContext context, Guid id)
        {
            var session = await Authorize(context, id, false);
            if (session.Canvas)
                throw new HttpFailure(403, "BrowserOnly", "Catalog/source administration and receipts are available only in the local browser.");
            return session;
        }
        async Task<CatalogSnapshot> Capture(ViewSession session, CancellationToken ct)
        {
            var current = Require(await store.ReadAsync(ct));
            if (current.Id != session.CatalogId)
            { sessions.Revoke(session); throw new HttpFailure(409, "CatalogReplaced", "Catalog identity changed. Pair again."); }
            if (session.Approval is { } approval && !approval.Matches(current))
            { sessions.Revoke(session); throw new HttpFailure(403, "CanvasScopeChanged", "Approved source or Bench identity changed. Pair again after explicit approval."); }
            return current;
        }
    }

    private static CancellationTokenSource Deadline(HttpContext context)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(25)); return timeout;
    }
    internal static string Bearer(HttpContext context) => context.Request.Headers.Authorization.ToString() is { } value
        && value.StartsWith("Bearer ", StringComparison.Ordinal) ? value[7..] : throw new HttpFailure(403, "CapabilityRejected", "Pair this view first.");
    internal static T Require<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new HttpFailure(result.Error.Code switch
    {
        ErrorCode.InvalidInput => 400, ErrorCode.NotFound => 404, ErrorCode.Conflict => 409, ErrorCode.PolicyViolation => 403, _ => 503,
    }, result.Error.Code.ToString(), $"Operation unavailable ({result.Error.Code}). No substitute data was returned; preserve the same operation UUID if outcome is unknown.");
    internal static async Task<T> Body<T>(HttpContext context, int maximumBytes = 16384)
    {
        if (!context.Request.HasJsonContentType()) throw new HttpFailure(415, "JsonRequired", "Send JSON.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var buffer = new MemoryStream();
        var bytes = new byte[2048];
        int read;
        while ((read = await context.Request.Body.ReadAsync(bytes, timeout.Token)) != 0)
        { if (buffer.Length + read > maximumBytes) throw new HttpFailure(413, "BodyLimit", "Request exceeds bound."); buffer.Write(bytes, 0, read); }
        using var doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        Unique(doc.RootElement);
        return doc.Deserialize((JsonTypeInfo<T>)TransportJson.Default.GetTypeInfo(typeof(T))!) ?? throw new JsonException();
    }
    internal static async Task Write<T>(HttpContext context, T value, int status = 200)
    {
        using var buffer = new LimitedBuffer();
        JsonSerializer.Serialize(buffer, value, (JsonTypeInfo<T>)TransportJson.Default.GetTypeInfo(typeof(T))!);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)), context.RequestAborted);
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in element.EnumerateObject())
            { if (!names.Add(member.Name)) throw new JsonException(); Unique(member.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
            { if (child.ValueKind == JsonValueKind.Null) throw new JsonException(); Unique(child); }
    }
    private sealed class LimitedBuffer : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Bound(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Bound(buffer.Length); base.Write(buffer); }
        private void Bound(int count)
        { if (Length + count > 8 * 1024 * 1024) throw new HttpFailure(503, "Partial", "Projection exceeds the output bound."); }
    }
}
