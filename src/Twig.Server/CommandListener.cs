using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Twig.Server;

internal sealed class CommandListener : IAsyncDisposable
{
    private const int Workers = 4;
    private readonly CancellationTokenSource stopping;
    public Task Completion { get; }

    public CommandListener(string data, Guid serverId, CancellationToken token = default)
        : this(Twig.CommandEndpoint.PipeName(data, serverId), CommandWorkerSupervisor.RelayAsync, token) { }

    internal CommandListener(string name, Func<Stream, CancellationToken, Task> serve, CancellationToken token, int workers = Workers)
    {
        // Bind before reporting readiness (or starting Kestrel). Do not join an existing server.
        var first = Open(name, first: true, workers);
        stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        Completion = ListenAsync(name, first, serve, workers);
    }

    private async Task ListenAsync(string name, NamedPipeServerStream next, Func<Stream, CancellationToken, Task> serve, int workers)
    {
        var active = new List<Task>(workers);
        var token = stopping.Token;
        try
        {
            while (true)
            {
                if (active.Count == workers)
                {
                    var completed = await Task.WhenAny(active);
                    active.Remove(completed);
                    await completed;
                }
                await next.WaitForConnectionAsync(token);
                token.ThrowIfCancellationRequested();
                var connected = next;
                // Keep the endpoint bound even when the previous client disconnects immediately.
                next = Open(name, first: false, workers);
                active.Add(Task.Run(() => ServeAsync(connected, serve, token)));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            next.Dispose();
            await stopping.CancelAsync();
            await Task.WhenAll(active);
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream client, Func<Stream, CancellationToken, Task> serve, CancellationToken token)
    {
        using (client)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            // Includes an interactive editor session (the native client allows five minutes).
            lifetime.CancelAfter(TimeSpan.FromMinutes(10));
            try { await serve(client, lifetime.Token); }
            // A disconnected or failed request has an unknown outcome. Close it without replay.
            catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or System.Text.DecoderFallbackException
                or OperationCanceledException or Win32Exception) { }
        }
    }

    internal static NamedPipeServerStream Open(string name, bool first, int workers = Workers)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0);
        if (!OperatingSystem.IsWindows())
            return new(name, PipeDirection.InOut, workers + 1, PipeTransmissionMode.Byte,
                options | PipeOptions.CurrentUserOnly);

        // CurrentUserOnly alone admits an SMB network logon for the same SID. The Windows
        // CurrentUserOnly constructor ignores custom ACLs, so express both rules in the DACL.
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Missing server user.");
        var owner = identity.Owner ?? throw new InvalidOperationException("Missing server owner.");
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, workers + 1,
            PipeTransmissionMode.Byte, options, 0, 0, WindowsSecurity(user, owner));
    }

    [SupportedOSPlatform("windows")]
    internal static PipeSecurity WindowsSecurity(SecurityIdentifier user, SecurityIdentifier owner)
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        // .NET's client CurrentUserOnly compares Token.Owner; access remains Token.User-only.
        acl.SetOwner(owner);
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return acl;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await stopping.CancelAsync();
            await Completion;
        }
        finally { stopping.Dispose(); }
    }
}
