using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography.X509Certificates;
using Bench.Application;
using Bench.Infrastructure;
using Bench.Server;
using Twig.Infrastructure.Services;

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

var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwigBench");
var port = 0;
var migrateLegacy = false;
string? certificateThumbprint = null;
string? webRoot = null;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--data-dir" when ++index < args.Length: data = args[index]; break;
        case "--port" when ++index < args.Length && int.TryParse(args[index], out var value) && value is >= 0 and <= 65535: port = value; break;
        case "--https-certificate" when ++index < args.Length: certificateThumbprint = args[index]; break;
        case "--web-root" when ++index < args.Length: webRoot = args[index]; break;
        case "--migrate-legacy": migrateLegacy = true; break;
        default: throw new ArgumentException("Options: --data-dir <private absolute directory> --port <stable localhost port> --https-certificate <existing trusted localhost certificate thumbprint> [--web-root <independently installed canvas directory>]. Benches use the native per-user store.");
    }
}
if (certificateThumbprint is null || port is 0 or 443 || !OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The panel requires same-owner Windows sign-in on an explicit stable HTTPS localhost port with an existing trusted certificate. No pairing-code or anonymous fallback is available.");
    Environment.ExitCode = 1;
    return;
}
if (!Path.IsPathFullyQualified(data)) throw new ArgumentException("An absolute private per-user data directory is required.");
if (webRoot is not null && (!Path.IsPathFullyQualified(webRoot) || !Directory.Exists(webRoot)))
    throw new ArgumentException("The selected canvas directory must be an existing absolute path.");
X509Certificate2 certificate;
try { certificate = LocalCertificate(certificateThumbprint); }
catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
    return;
}
using var certificateLease = certificate;
NativeConnectionSetupGateway setup;
try { setup = new(new NativeConnectionSetup(NativeConnectionSetup.ResolveUserHome())); }
catch (Exception error) when (error is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine("Native connection setup configuration is invalid. No metadata-home fallback was used. " + error.Message);
    Environment.ExitCode = 1;
    return;
}
Directory.CreateDirectory(data);
using var instance = new FileStream(Path.Combine(data, "daemon.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var definitions = UserBenchStore.ForCurrentUser();
var migration = await LegacyBenchMigration.CheckAsync(data, definitions, migrateLegacy, default);
if (!migration.IsSuccess) throw new InvalidOperationException(migration.Error.Message);
var changes = new CatalogChanges();
var native = new NativeSourceAdapter(Path.Combine(data, "native-evidence"), new NativeSourceGateway(), changes);
var panel = new NativePanel(definitions, new NativeSourceGateway(), native);
var sessions = new ViewSessions(Path.Combine(data, "presentation"));
await using var app = NativeBenchHost.Build(new(port, data, WebRoot: webRoot, Certificate: certificate, WindowsSignIn: true), panel, sessions, changes, setup);
await app.StartAsync();
var endpoint = $"https://localhost:{port}";
await File.WriteAllTextAsync(Path.Combine(data, "endpoint.json"),
    JsonSerializer.Serialize(new EndpointInfo(endpoint, "twig-bench-service-v1"), EndpointJson.Default.EndpointInfo));
Console.WriteLine($"Twig Bench local browser: {endpoint}");
Console.WriteLine("Native user-wide Benches shared with the CLI. Each existing Twig context keeps its own Bench selection.");
Console.WriteLine("No item publishing, synchronization or agent content-return capability is exposed.");
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
await app.WaitForShutdownAsync();

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

internal sealed record EndpointInfo(string Url, string Protocol);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EndpointInfo))]
internal sealed partial class EndpointJson : JsonSerializerContext;
