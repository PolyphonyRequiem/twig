using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Bench.Application;
using Bench.Infrastructure;
using Bench.Server;
using Twig.Infrastructure.Services;

if (args is [CommandWorkerSupervisor.WorkerMode])
{
    try
    {
        // Unix ConsolePal tracks the cursor under lock(Console.Out). A worker rendering through
        // Console.Out already holds that lock when it writes a frame on another thread.
        // Write the binary protocol directly to the inherited stdout pipe, not to the terminal.
        using Stream wire = OperatingSystem.IsLinux()
            ? new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)1, ownsHandle: false),
                FileAccess.Write, bufferSize: 1)
            : Console.OpenStandardOutput();
        await Twig.DaemonCommandWorker.RunAsync(Console.OpenStandardInput(), wire);
    }
    catch (Exception) { Environment.ExitCode = 1; } // Never log a request or replay a failed worker.
    return;
}
if (args is ["--commands-only", "--data-dir", var privateData])
{
    await CommandQualificationHost.RunAsync(privateData);
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

var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Twig", "DaemonData");
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
        case "--mcp": mcp = true; break;
        default: throw new ArgumentException("Options: --data-dir <private absolute directory> --port <stable localhost port> --https-certificate <existing trusted localhost certificate thumbprint> [--canvas-root <verified canvas installation root>] [--mcp]. --mcp is a read-only stdio bridge to the already-running selected daemon. Benches use the native per-user store.");
    }
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
if (mcp)
{
    if (lifecycle != null) throw new ArgumentException("Run daemon lifecycle operations separately from the stdio MCP transport.");
    try
    {
        if (!await DaemonLauncher.IsRunningAsync(data, port, certificate, default))
            throw new InvalidOperationException("Start the selected daemon with its supported launcher before connecting MCP.");
        HttpClient Client(bool credentials) => new(new HttpClientHandler
        {
            UseDefaultCredentials = credentials, UseCookies = false, UseProxy = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, peer, _, errors) =>
                errors == SslPolicyErrors.None && peer?.Thumbprint == certificate.Thumbprint
        }) { Timeout = TimeSpan.FromSeconds(35), MaxResponseContentBufferSize = 524288 };
        using var owner = Client(true);
        using var plain = Client(false);
        await DaemonMcpBridge.RunAsync(new Uri($"https://localhost:{port}"), owner, plain,
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
            await DaemonLauncher.EnsureRunningAsync(data, port, certificate, () =>
                DaemonLauncher.StartInstalled(Environment.ProcessPath ?? throw new InvalidOperationException("Missing installed executable."),
                    args.Where(argument => argument != "--ensure-running")), default);
            running = true;
        }
        else if (lifecycle == "--status")
            running = await DaemonLauncher.IsRunningAsync(data, port, certificate, default);
        else
        {
            await DaemonLauncher.StopAsync(data, port, certificate, default);
            running = false;
        }
        Console.WriteLine(running ? "{\"running\":true}" : "{\"running\":false}");
    }
    catch (Exception error) when (error is IOException or InvalidOperationException or HttpRequestException
        or OperationCanceledException or JsonException or KeyNotFoundException or WaitHandleCannotBeOpenedException
        or UnauthorizedAccessException or FormatException or System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine("The requested daemon state could not be confirmed. " + error.Message);
        Environment.ExitCode = 1;
    }
    return;
}
NativeConnectionSetupGateway setup;
try { setup = new(new NativeConnectionSetup(NativeConnectionSetup.ResolveUserHome())); }
catch (Exception error) when (error is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine("Native connection setup configuration is invalid. No metadata-home fallback was used. " + error.Message);
    Environment.ExitCode = 1;
    return;
}
Directory.CreateDirectory(data);
using var instance = DaemonInstance.Acquire(data);
var daemonId = instance.Id;
var definitions = UserBenchStore.ForCurrentUser();
var changes = new CatalogChanges();
var native = NativeSourceAdapter.ForLocalUser(new NativeSourceGateway(), changes);
var panel = new NativePanel(definitions, new NativeSourceGateway(), native);
var sessions = new ViewSessions(Path.Combine(data, "presentation"));
var processInstance = Guid.NewGuid();
using var stopSignal = DaemonLauncher.CreateStopSignal(processInstance);
await using var app = NativeBenchHost.Build(new(port, data, Certificate: certificate, WindowsSignIn: true,
    CanvasInstallRoot: canvasRoot, DaemonId: daemonId, ProcessInstance: processInstance), panel, sessions, changes, setup);
var stopWait = ThreadPool.RegisterWaitForSingleObject(stopSignal, (_, _) => app.Lifetime.StopApplication(),
    null, Timeout.Infinite, executeOnlyOnce: true);
await using var commands = new CommandListener(data, daemonId, app.Lifetime.ApplicationStopping);
await app.StartAsync();
var endpoint = $"https://localhost:{port}";
await File.WriteAllTextAsync(Path.Combine(data, "endpoint.json"),
    JsonSerializer.Serialize(new EndpointInfo(endpoint, CanvasAssets.Protocol, daemonId,
        CanvasAssets.DaemonVersion), EndpointJson.Default.EndpointInfo));
Console.WriteLine($"Twig Bench local browser: {endpoint}");
Console.WriteLine("Native user-wide Benches shared with the CLI. Each existing Twig context keeps its own Bench selection.");
Console.WriteLine("Save/Apply are human-only. Agent reads and drafts require exact view-scoped admission; membership requires separate opt-in.");
Console.WriteLine("The panel uses same-owner Windows sign-in; SDK controls remain acknowledgement-only. Command: stop.");
_ = Task.Run(() =>
{
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        var command = Console.ReadLine();
        if (command is null) return;
        if (command.Trim() == "stop") { app.Lifetime.StopApplication(); return; }
        Console.WriteLine("Command: stop");
    }
});
var shutdown = app.WaitForShutdownAsync();
if (await Task.WhenAny(shutdown, commands.Completion) == commands.Completion)
    app.Lifetime.StopApplication();
await shutdown;
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

internal sealed record EndpointInfo(string Url, string Protocol, Guid DaemonId, string Version);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EndpointInfo))]
internal sealed partial class EndpointJson : JsonSerializerContext;
