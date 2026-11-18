using System.ComponentModel;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Bench.Server;

internal sealed class CommandListener : IAsyncDisposable
{
    private const int Workers = 4;
    private readonly CancellationTokenSource stopping;
    public Task Completion { get; }

    public CommandListener(string data, Guid daemonId, CancellationToken token = default)
    {
        var name = Twig.CommandEndpoint.PipeName(data, daemonId);
        // Bind before reporting readiness (or starting Kestrel). Do not join an existing server.
        var first = Open(name, first: true);
        stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        Completion = ListenAsync(name, first);
    }

    private async Task ListenAsync(string name, NamedPipeServerStream next)
    {
        var active = new List<Task>(Workers);
        var token = stopping.Token;
        try
        {
            while (true)
            {
                if (active.Count == Workers)
                {
                    var completed = await Task.WhenAny(active);
                    active.Remove(completed);
                    await completed;
                }
                await next.WaitForConnectionAsync(token);
                token.ThrowIfCancellationRequested();
                var connected = next;
                // Keep the endpoint bound even when the previous client disconnects immediately.
                next = Open(name, first: false);
                active.Add(Task.Run(() => ServeAsync(connected, token)));
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

    private static async Task ServeAsync(NamedPipeServerStream client, CancellationToken token)
    {
        using (client)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            // Includes an interactive editor session (the native client allows five minutes).
            lifetime.CancelAfter(TimeSpan.FromMinutes(10));
            try { await CommandWorkerSupervisor.RelayAsync(client, lifetime.Token); }
            // A disconnected or failed request has an unknown outcome. Close it without replay.
            catch (Exception e) when (e is IOException or OperationCanceledException or Win32Exception) { }
        }
    }

    private static NamedPipeServerStream Open(string name, bool first)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0);
        if (!OperatingSystem.IsWindows())
            return new(name, PipeDirection.InOut, Workers + 1, PipeTransmissionMode.Byte,
                options | PipeOptions.CurrentUserOnly);

        // CurrentUserOnly alone admits an SMB network logon for the same SID. The Windows
        // CurrentUserOnly constructor ignores custom ACLs, so express both rules in the DACL.
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Missing daemon owner.");
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(owner);
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, Workers + 1,
            PipeTransmissionMode.Byte, options, 0, 0, acl);
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
