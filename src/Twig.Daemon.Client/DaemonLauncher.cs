using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Bench.Server;

public static class DaemonLauncher
{
    public const string InstanceHeader = "X-Twig-Daemon-Instance";

    public static async Task EnsureRunningAsync(string data, int port, X509Certificate2 certificate,
        Action start, CancellationToken cancellationToken)
    {
        using var deadline = Deadline(cancellationToken);
        using var client = OwnerClient(certificate);
        await EnsureWithProbeAsync(data, async () => await ProbeAsync(client, data, port, deadline.Token) != null,
            start, deadline.Token);
    }

    public static async Task<bool> IsRunningAsync(string data, int port, X509Certificate2 certificate, CancellationToken token)
    {
        using var deadline = Deadline(token);
        using var client = OwnerClient(certificate);
        if (await ProbeAsync(client, data, port, deadline.Token) != null) return true;
        if (InstanceLocked(data)) throw new InvalidOperationException("The selected daemon is running but unavailable.");
        return false;
    }

    [SupportedOSPlatform("windows")]
    public static async Task StopAsync(string data, int port, X509Certificate2 certificate, CancellationToken token)
    {
        using var deadline = Deadline(token);
        using var client = OwnerClient(certificate);
        await StopWithProbeAsync(data, () => ProbeAsync(client, data, port, deadline.Token, requireCanvasProtocol: false), deadline.Token);
    }

    [SupportedOSPlatform("windows")]
    internal static async Task StopWithProbeAsync(string data, Func<Task<Guid?>> probe, CancellationToken token)
    {
        using var lease = await AcquireLeaseAsync(data, token);
        var instance = await probe();
        if (instance is null)
        {
            if (InstanceLocked(data)) throw new InvalidOperationException("The selected daemon is unavailable; stop was not requested.");
            return;
        }
        if (instance == Guid.Empty)
            throw new InvalidOperationException("The selected daemon does not support CLI stop. Use its operator console.");
        using var signal = EventWaitHandle.OpenExisting(StopEventName(instance.Value), OwnerOnly);
        signal.Set();
        while (InstanceLocked(data)) await Task.Delay(100, token);
        if (await probe() != null)
            throw new InvalidOperationException("A daemon is still listening. Stop was not repeated.");
    }

    // Per-process identity prevents a delayed stop for a previous run reaching its replacement.
    private static string StopEventName(Guid instance) => "Twig.Daemon.Stop." + instance.ToString("N");
    private static NamedWaitHandleOptions OwnerOnly => new() { CurrentUserOnly = true, CurrentSessionOnly = false };

    [SupportedOSPlatform("windows")]
    public static EventWaitHandle CreateStopSignal(Guid instance)
    {
        var signal = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName(instance), OwnerOnly, out var created);
        if (created) return signal;
        signal.Dispose();
        throw new InvalidOperationException("Daemon stop signal already exists.");
    }

    private static bool InstanceLocked(string data)
    {
        try
        {
            using var instance = new FileStream(Path.Combine(data, "daemon.lock"), FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (IOException error) when (IsSharingViolation(error)) { return true; }
    }

    private static bool IsSharingViolation(IOException error) => (error.HResult & 0xffff) is 11 or 32 or 33;

    private static CancellationTokenSource Deadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return deadline;
    }

    private static HttpClient OwnerClient(X509Certificate2 certificate) =>
        new(new HttpClientHandler
        {
            UseDefaultCredentials = true, UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, peer, _, errors) =>
                errors == SslPolicyErrors.None && peer?.Thumbprint == certificate.Thumbprint
        }) { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 4096 };

    private static async Task<Guid?> ProbeAsync(HttpClient client, string data, int port, CancellationToken token,
        bool requireCanvasProtocol = true)
    {
        async Task<bool> Refused(IPAddress address)
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(address, port, token); return false; }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused)
            { return true; }
        }
        // Once either family accepts TCP, an uncertain TLS/identity result is never absence.
        if ((await Task.WhenAll(Refused(IPAddress.Loopback), Refused(IPAddress.IPv6Loopback))).All(refused => refused))
            return null;
        using var response = await client.GetAsync($"https://localhost:{port}/api/v1/owner-status", token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("The listener refused same-owner daemon identity verification.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        var status = document.RootElement;
        var identityPath = Path.Combine(data, "daemon.id");
        if (!File.Exists(identityPath) || new FileInfo(identityPath).Length > 64
            || !Guid.TryParseExact(await File.ReadAllTextAsync(identityPath, token), "D", out var expected)
            || expected == Guid.Empty || status.GetProperty("daemonId").GetGuid() != expected
            || status.GetProperty("canvasDefaultDeny").GetBoolean() != true)
            throw new InvalidOperationException("The listener is not the selected daemon identity.");
        if (requireCanvasProtocol && status.GetProperty("protocol").GetString() != CanvasAssets.Protocol)
            throw new InvalidOperationException("The selected daemon is running an incompatible Canvas protocol. Stop it and start the selected version.");
        if (!response.Headers.TryGetValues(InstanceHeader, out var values)) return Guid.Empty;
        if (!Guid.TryParseExact(values.Single(), "D", out var instance) || instance == Guid.Empty)
            throw new InvalidOperationException("Invalid daemon process identity.");
        return instance;
    }

    internal static async Task EnsureWithProbeAsync(string data, Func<Task<bool>> ready,
        Action start, CancellationToken token)
    {
        if (await ready()) return;
        using (await AcquireLeaseAsync(data, token))
        {
            if (await ready()) return;
            try
            {
                using var instance = new FileStream(Path.Combine(data, "daemon.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when (IsSharingViolation(error))
            { throw new InvalidOperationException("The selected daemon is already running but unavailable; it was not restarted.", error); }
            token.ThrowIfCancellationRequested();
            start();
            while (!await ready()) await Task.Delay(150, token);
        }
    }

    private static async Task<FileStream> AcquireLeaseAsync(string data, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(data, "launch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (IsSharingViolation(error)) { await Task.Delay(100, token); }
        }
    }

    public static void StartInstalled(string executable, IEnumerable<string> arguments)
    {
        var process = new ProcessStartInfo(executable) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in arguments) process.ArgumentList.Add(argument);
        using var started = Process.Start(process) ?? throw new IOException("The installed daemon did not start.");
    }
}
