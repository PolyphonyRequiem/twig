using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Bench.Application;
using Bench.Infrastructure;
using Twig.Server;
using Twig.Infrastructure.Services;

var fixtureRequested = Environment.GetEnvironmentVariable("TWIG_OWNER_FIXTURE_ID") is not null;
if (args.FirstOrDefault() is CommandWorkerSupervisor.WorkerMode or "--commands-only")
{
    Console.Error.WriteLine("Only the isolated authority owner may compose command services. " + Twig.OwnerFixtureSelection.CutoverHeld);
    Environment.ExitCode = 2;
    return;
}

if (args is ["--check-certificate", var selectedCertificate])
{
    try
    {
        using var checkedCertificate = LocalCertificate(selectedCertificate);
        Console.WriteLine("Existing localhost HTTPS certificate is valid and trusted; no certificate or trust setting was changed.");
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
    {
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }
    return;
}

if (!fixtureRequested)
{
    Console.Error.WriteLine(Twig.OwnerFixtureSelection.CutoverHeld);
    Environment.ExitCode = 2;
    return;
}
var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Twig", "ServerData");
var port = 0;
string? lifecycle = null;
var mcp = false;
string? certificateThumbprint = null;
var canvasRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Twig", "Canvas");
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--data-dir" when ++index < args.Length: data = args[index]; break;
        case "--port" when ++index < args.Length && int.TryParse(args[index], out var value) && value is >= 0 and <= 65535: port = value; break;
        case "--https-certificate" when ++index < args.Length: certificateThumbprint = args[index]; break;
        case "--canvas-root" when ++index < args.Length: canvasRoot = args[index]; break;
        case "--ensure-running" or "--status" or "--stop" when lifecycle is null: lifecycle = args[index]; break;
        case "--owner-stop-v2" when fixtureRequested && lifecycle is null: lifecycle = args[index]; break;
        case "--mcp": mcp = true; break;
        default: throw new ArgumentException("Options: --data-dir <private absolute directory> --port <stable localhost port> --https-certificate <existing trusted localhost certificate thumbprint> [--canvas-root <verified canvas installation root>] [--mcp]. --mcp is a read-only stdio bridge to the already-running selected server. Benches use the native per-user store.");
    }
}
Twig.OwnerFixtureSelection? fixture;
try
{
    fixture = Twig.OwnerFixtureSelection.Read() ?? throw new InvalidDataException(Twig.OwnerFixtureSelection.CutoverHeld);
    fixture?.VerifyLaunch(data, port, certificateThumbprint);
    if (fixture is not null)
    {
        data = PrivateCommandDirectory.Open(fixture.Home, executionOwner: true);
        if (mcp) throw new InvalidDataException("The isolated owner selection excludes the MCP bridge.");
        if (lifecycle == "--stop") throw new InvalidDataException("Legacy Stop is not available for this execution owner.");
    }
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
    return;
}
if (certificateThumbprint is null || port is 0 or 443 || !OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The panel requires same-owner Windows sign-in on an explicit stable HTTPS localhost port with an existing trusted certificate. No pairing-code or anonymous fallback is available.");
    Environment.ExitCode = 1;
    return;
}
if (!Path.IsPathFullyQualified(data)) throw new ArgumentException("An absolute private per-user data directory is required.");
if (!Path.IsPathFullyQualified(canvasRoot))
    throw new ArgumentException("The canvas installation root must be an absolute same-machine path; it need not be installed.");
X509Certificate2 certificate;
try { certificate = LocalCertificate(certificateThumbprint); }
catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
    return;
}
using var certificateLease = certificate;
var buildId = typeof(ServerInstance).Module.ModuleVersionId;
Func<Guid>? executionOwner = fixture is null ? null : () => fixture.VerifyRun(buildId).Boot;
if (mcp)
{
    if (lifecycle != null) throw new ArgumentException("Run server lifecycle operations separately from the stdio MCP transport.");
    try
    {
        if (!await ServerLauncher.IsRunningAsync(data, port, certificate, default))
            throw new InvalidOperationException("Start the selected server with its supported launcher before connecting MCP.");
        HttpClient Client(bool credentials) => new(new HttpClientHandler
        {
            UseDefaultCredentials = credentials, UseCookies = false, UseProxy = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, peer, _, errors) =>
                errors == SslPolicyErrors.None && peer?.Thumbprint == certificate.Thumbprint
        }) { Timeout = TimeSpan.FromSeconds(35), MaxResponseContentBufferSize = 524288 };
        using var owner = Client(true);
        using var plain = Client(false);
        await ServerMcpBridge.RunAsync(new Uri($"https://localhost:{port}"), owner, plain,
            Console.In, Console.Out, default);
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or HttpRequestException
        or OperationCanceledException or JsonException or KeyNotFoundException)
    {
        Console.Error.WriteLine("Twig MCP stopped without replaying a request: " + error.Message);
        Environment.ExitCode = 1;
    }
    return;
}
if (lifecycle != null)
{
    try
    {
        bool running;
        if (lifecycle == "--ensure-running")
        {
            await ServerLauncher.EnsureRunningAsync(data, port, certificate, () =>
                ServerLauncher.StartInstalled(Environment.ProcessPath ?? throw new InvalidOperationException("Missing installed executable."),
                    args.Where(argument => argument != "--ensure-running")), default, executionOwner);
            running = true;
        }
        else if (lifecycle == "--status")
            running = await ServerLauncher.IsRunningAsync(data, port, certificate, default, executionOwner);
        else
        {
            await ServerLauncher.StopAsync(data, port, certificate, default, executionOwner);
            running = false;
        }
        Console.WriteLine(running ? "{\"running\":true}" : "{\"running\":false}");
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or HttpRequestException
        or OperationCanceledException or JsonException or KeyNotFoundException or WaitHandleCannotBeOpenedException
        or UnauthorizedAccessException or FormatException or ArgumentException or InvalidDataException
        or System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine("The requested server state could not be confirmed. " + error.Message);
        Environment.ExitCode = 1;
    }
    return;
}
Directory.CreateDirectory(data);
ServerInstance lease;
try { lease = ServerInstance.Acquire(data, fixture is null ? null : Guid.Parse(Path.GetFileName(fixture.Root))); }
catch (IOException error) when (fixture is not null)
{
    Console.Error.WriteLine("The selected authority lease was not acquired. " + error.Message);
    Environment.ExitCode = 2;
    return;
}
using var instance = lease;
using var pinnedSelection = fixture?.HoldSelection();
var nativeDesktop = fixture?.NativeDesktop == true;
// A developer boot must not inherit pages, grants or recovery work from a general provider fixture.
if (nativeDesktop && (!instance.Created || File.Exists(Path.Combine(data, "released-benches.db"))
    || Directory.Exists(Path.Combine(data, "presentation-v2"))))
{
    Console.Error.WriteLine("Developer desktop requires a fresh native fixture.");
    Environment.ExitCode = 2;
    return;
}
FileStream? desktopWorkspace;
try { desktopWorkspace = nativeDesktop ? fixture!.HoldNativeWorkspace() : null; }
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
{
    Console.Error.WriteLine("Developer workspace admission failed: " + error.Message);
    Environment.ExitCode = 2;
    return;
}
using var pinnedWorkspace = desktopWorkspace;
NativeConnectionSetupGateway? setup;
try { setup = fixture is null ? new(new NativeConnectionSetup(NativeConnectionSetup.ResolveUserHome())) : null; }
catch (Exception error) when (error is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine("Native connection setup configuration is invalid. No metadata-home fallback was used. " + error.Message);
    Environment.ExitCode = 1;
    return;
}
var serverId = instance.Id;
var definitions = UserBenchStore.ForCurrentUser();
var changes = new CatalogChanges();
var native = fixture is null ? NativeSourceAdapter.ForLocalUser(new NativeSourceGateway(), changes)
    : new NativeSourceAdapter(Path.Combine(data, "native-evidence"), new NativeSourceGateway(), changes);
var panel = new NativePanel(definitions, new NativeSourceGateway(), native);
var processInstance = Guid.NewGuid();
var sessions = new ViewSessions(Path.Combine(data, fixture is null ? "presentation" : "presentation-v2"));
using var stopSignal = ServerLauncher.CreateStopSignal(processInstance, fixture is not null);
// Kestrel's timeout does not cover the pipe listeners, detached operations or disposal.
// Exit this owner with the OS lease still held rather than releasing a nonquiescent claim.
using var drainDeadline = fixture is null ? null : new Timer(_ => Environment.Exit(1));
await using var app = NativeBenchHost.Build(new(port, data, Certificate: certificate, WindowsSignIn: true,
    CanvasInstallRoot: canvasRoot, ServerId: serverId, ProcessInstance: processInstance, ExecutionV2: fixture is not null,
    NativeDesktop: nativeDesktop), panel, sessions, changes, setup);
using var armDeadline = app.Lifetime.ApplicationStopping.Register(() =>
    drainDeadline?.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan));
var stopWait = ThreadPool.RegisterWaitForSingleObject(stopSignal, (_, _) => app.Lifetime.StopApplication(),
    null, Timeout.Infinite, executeOnlyOnce: true);
var stopping = app.Lifetime.ApplicationStopping;
if (fixture is not null)
{
    try
    {
        if (instance.Created && File.Exists(Path.Combine(data, "owner.run")))
            throw new InvalidDataException("A recorded owner lost its authority identity.");
        if (!instance.Created && !File.Exists(Path.Combine(data, "released-benches.db")))
            throw new InvalidDataException("The recorded owner's publication inventory is missing.");
        await definitions.CheckOwnerClaimsAsync(!instance.Created, stopping);
    }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
        Microsoft.Data.Sqlite.SqliteException or JsonException or InvalidOperationException or ArgumentException or FormatException)
    {
        Console.Error.WriteLine("Owner claim inventory could not be established; no requests admitted. " + error.Message);
        Environment.ExitCode = 2;
        return;
    }
    var display = app.Services.GetRequiredService<ReleasedBenchDisplay>();
    var root = Path.Combine(fixture.Root, "work");
    display.RecoverWorkspace(Path.Combine(root, ".twig", "core.db"));
    if (!nativeDesktop) await display.RecoverPlans(root, definitions, stopping);
    fixture.RecordRun(processInstance, buildId);
}
var execution = fixture is null ? null : app.Services.GetRequiredService<OwnerCommandEndpoint>();
await using var commands = fixture is null ? new CommandListener(data, serverId, stopping)
    : new CommandListener(Twig.CommandEndpoint.ExecutionPipe(serverId, processInstance),
        execution!.ServeAsync, stopping, workers: 12); // Four in-flight effects must not consume the eight acquisition slots.
await using var legacy = fixture is null ? null : new CommandListener(Twig.CommandEndpoint.PipeName(data, serverId),
    OwnerCommandEndpoint.RefuseLegacyAsync, stopping);
await app.StartAsync();
var endpoint = $"https://localhost:{port}";
await File.WriteAllTextAsync(Path.Combine(data, "endpoint.json"),
    JsonSerializer.Serialize(new EndpointInfo(endpoint, CanvasAssets.Protocol, serverId,
        CanvasAssets.ServerVersion), EndpointJson.Default.EndpointInfo));
Console.WriteLine($"Twig Bench local browser: {endpoint}");
Console.WriteLine("Native user-wide Benches shared with the CLI. Each existing Twig context keeps its own Bench selection.");
Console.WriteLine("Save/Apply are human-only. Agent reads and drafts require exact view-scoped admission; membership requires separate opt-in.");
Console.WriteLine("The panel uses same-owner Windows sign-in; SDK controls remain acknowledgement-only. Command: stop.");
_ = Task.Run(() =>
{
    if (fixture is not null) return;
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        var command = Console.ReadLine();
        if (command is null) return;
        if (command.Trim() == "stop") { app.Lifetime.StopApplication(); return; }
        Console.WriteLine("Command: stop");
    }
});
var shutdown = app.WaitForShutdownAsync();
if (await Task.WhenAny(shutdown, commands.Completion, legacy?.Completion ?? shutdown) != shutdown)
    app.Lifetime.StopApplication();
await shutdown;
if (execution is not null)
{
    await commands.Completion;
    await execution.DrainAsync();
}
stopWait.Unregister(null);

static X509Certificate2 LocalCertificate(string thumbprint)
{
    if (!OperatingSystem.IsWindows() || thumbprint.Length != 40 || thumbprint.Any(value => !char.IsAsciiHexDigit(value)))
        throw new ArgumentException("Supply an existing Windows current-user localhost HTTPS certificate thumbprint.");
    using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
    store.Open(OpenFlags.ReadOnly);
    var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
    if (matches.Count != 1) throw new InvalidOperationException("The selected current-user HTTPS certificate is missing or ambiguous.");
    var certificate = matches[0];
    using var chain = new X509Chain();
    chain.ChainPolicy.DisableCertificateDownloads = true;
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.ApplicationPolicy.Add(new("1.3.6.1.5.5.7.3.1"));
    if (!certificate.HasPrivateKey || certificate.GetNameInfo(X509NameType.DnsName, false) != "localhost" || !chain.Build(certificate))
    {
        certificate.Dispose();
        throw new InvalidOperationException("An already trusted, valid localhost server certificate with a private key is required. No certificate was created or trusted.");
    }
    return certificate;
}

internal sealed record EndpointInfo(string Url, string Protocol, Guid ServerId, string Version);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EndpointInfo))]
internal sealed partial class EndpointJson : JsonSerializerContext;
