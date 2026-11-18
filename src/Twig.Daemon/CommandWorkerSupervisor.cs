using System.Diagnostics;

namespace Bench.Server;

internal static class CommandWorkerSupervisor
{
    internal const string WorkerMode = "--daemon-command-worker";

    private static ProcessStartInfo Worker()
    {
        var root = AppContext.BaseDirectory;
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Missing daemon host.");
        var dotnet = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var hosted = string.Equals(Path.GetFileName(host), dotnet,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var appHost = Path.Combine(root, OperatingSystem.IsWindows() ? "Bench.Server.exe" : "Bench.Server");
        var start = new ProcessStartInfo(hosted ? host : appHost)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Process.StandardInput is a StreamWriter. Never allow its encoding to add a BOM.
            StandardInputEncoding = new System.Text.UTF8Encoding(false)
        };
        if (hosted) start.ArgumentList.Add(Path.Combine(root, "Bench.Server.dll"));
        start.ArgumentList.Add(WorkerMode);
        return start;
    }

    internal static async Task RelayAsync(Stream client, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var worker = Process.Start(Worker()) ?? throw new IOException("Cannot start daemon worker.");
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = new List<Task>(3);
        try
        {
            tasks.Add(ForwardInputAsync(client, worker.StandardInput.BaseStream, relay.Token));
            tasks.Add(worker.StandardOutput.BaseStream.CopyToAsync(client, relay.Token));
            tasks.Add(DiscardErrorsAsync(worker.StandardError.BaseStream, relay.Token));
            // Do not wait for both directions: the worker may be busy and the client may
            // never send another byte. Either EOF, a relay fault, or the deadline ends the lease.
            var finished = await Task.WhenAny(tasks).WaitAsync(token);
            await finished;
        }
        finally
        {
            try
            {
                if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (worker.HasExited) { }
            finally
            {
                await relay.CancelAsync();
                await worker.WaitForExitAsync();
                await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private static async Task ForwardInputAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = new byte[16384];
        int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
            await destination.FlushAsync(token);
        }
    }

    private static async Task DiscardErrorsAsync(Stream errors, CancellationToken token)
    {
        await errors.CopyToAsync(Stream.Null, token);
        // A clean stderr close is not stdout EOF: let the response drain.
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
}
