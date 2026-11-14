using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Bench.Server;

public static class DaemonLauncher
{
    public static async Task EnsureRunningAsync(string data, int port, X509Certificate2 certificate,
        Action start, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true, UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, peer, _, errors) =>
                errors == SslPolicyErrors.None && peer?.Thumbprint == certificate.Thumbprint
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 4096 };
        async Task<bool> Refused(IPAddress address)
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(address, port, token); return false; }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused)
            { return true; }
        }
        async Task<bool> Ready()
        {
            // Windows can take two seconds per refused address. Check both families concurrently,
            // and never turn a timeout, filtered socket or reachable foreign listener into absence.
            if ((await Task.WhenAll(Refused(IPAddress.Loopback), Refused(IPAddress.IPv6Loopback))).All(refused => refused))
                return false;
            HttpResponseMessage response;
            try { response = await client.GetAsync($"https://localhost:{port}/api/v1/owner-status", token); }
            catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.ConnectionError
                && error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            { return false; }
            using (response)
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("The listener refused same-owner daemon identity verification; it was not restarted.");
                using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
                var status = document.RootElement;
                var identityPath = Path.Combine(data, "daemon.id");
                if (!File.Exists(identityPath) || new FileInfo(identityPath).Length > 64
                    || !Guid.TryParseExact(await File.ReadAllTextAsync(identityPath, token), "D", out var expected)
                    || expected == Guid.Empty || status.GetProperty("daemonId").GetGuid() != expected
                    || status.GetProperty("protocol").GetString() != CanvasAssets.Protocol
                    || status.GetProperty("canvasDefaultDeny").GetBoolean() != true)
                    throw new InvalidOperationException("The listener is not the selected daemon identity; nothing was started or replaced.");
                return true;
            }
        }
        await EnsureWithProbeAsync(data, Ready, start, token);
    }

    internal static async Task EnsureWithProbeAsync(string data, Func<Task<bool>> ready,
        Action start, CancellationToken token)
    {
        if (await ready()) return;
        FileStream? lease = null;
        while (lease is null)
        {
            token.ThrowIfCancellationRequested();
            try { lease = new FileStream(Path.Combine(data, "launch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
            { await Task.Delay(100, token); }
        }
        using (lease)
        {
            if (await ready()) return;
            try
            {
                using var instance = new FileStream(Path.Combine(data, "daemon.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
            { throw new InvalidOperationException("The selected daemon is already running but unavailable; it was not restarted.", error); }
            token.ThrowIfCancellationRequested();
            start();
            while (!await ready()) await Task.Delay(150, token);
        }
    }

    public static void StartInstalled(string executable, IEnumerable<string> arguments)
    {
        var process = new ProcessStartInfo(executable) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in arguments) process.ArgumentList.Add(argument);
        using var started = Process.Start(process) ?? throw new IOException("The installed daemon did not start.");
    }
}
