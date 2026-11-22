using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Twig.Server;

[SupportedOSPlatform("linux")]
internal sealed class LinuxServerControl : IAsyncDisposable
{
    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task service;

    [DllImport("libc")]
    private static extern uint geteuid();

    public static LinuxServerControl Start(string data, Guid serverId, Action stop) => new(data, serverId, stop);

    private LinuxServerControl(string data, Guid serverId, Action stop)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint("\0twig-control-v1-" + digest));
            listener.Listen(8);
            service = ServeAsync(Encoding.ASCII.GetBytes($"TWIG-CONTROL/1 {serverId:D}\n"), stop);
        }
        catch { listener.Dispose(); lifetime.Dispose(); throw; }
    }

    private async Task ServeAsync(byte[] greeting, Action stop)
    {
        while (!lifetime.IsCancellationRequested)
        {
            using var peer = await listener.AcceptAsync(lifetime.Token);
            // Linux SOL_SOCKET/SO_PEERCRED: struct ucred { pid_t pid; uid_t uid; gid_t gid; }.
            byte[] credentials = new byte[12];
            if (peer.GetRawSocketOption(1, 17, credentials) != credentials.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(credentials.AsSpan(4)) != geteuid()) continue;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using var stream = new NetworkStream(peer);
                await stream.WriteAsync(greeting, deadline.Token);
                var command = new byte[1];
                if (await stream.ReadAsync(command, deadline.Token) == 1 && command[0] == (byte)'S')
                {
                    await stream.WriteAsync("OK\n"u8.ToArray(), deadline.Token);
                    stop();
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { await service; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { listener.Dispose(); lifetime.Dispose(); }
    }
}
