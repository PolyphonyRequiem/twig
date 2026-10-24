using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Twig.Infrastructure.GitHub;

internal enum BoundedProcessOutcome
{
    Exited,
    TimedOut,
    NotStarted,
    OutputLimitExceeded,
}

/// <summary>
/// Result of a bounded child process. Standard error is drained and discarded, never stored,
/// so a credential helper's diagnostics cannot leak through this type.
/// </summary>
internal sealed record BoundedProcessResult(BoundedProcessOutcome Outcome, int ExitCode, string StandardOutput);

/// <summary>Runs a child process with an argument array, no shell, and bounded time and output.</summary>
internal interface IBoundedProcessRunner
{
    Task<BoundedProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maxOutputChars,
        CancellationToken ct);
}

internal sealed class BoundedProcessRunner : IBoundedProcessRunner
{
    public async Task<BoundedProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maxOutputChars,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            return new BoundedProcessResult(BoundedProcessOutcome.NotStarted, -1, string.Empty);
        }
        if (process is null)
            return new BoundedProcessResult(BoundedProcessOutcome.NotStarted, -1, string.Empty);

        using (process)
        {
            try { process.StandardInput.Close(); } catch (IOException) { }

            var stderrDrain = DrainAsync(process.StandardError);
            var stdoutRead = ReadBoundedAsync(process.StandardOutput, maxOutputChars);

            // Pipe reads are not reliably cancellable on every platform, so one deadline races
            // the work rather than being passed into the reads.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var expiry = Task.Delay(timeout, deadline.Token);
            try
            {
                if (await Task.WhenAny(stdoutRead, expiry).ConfigureAwait(false) != stdoutRead)
                    return TimedOut(process, ct);

                var (text, overflowed) = await stdoutRead.ConfigureAwait(false);
                if (overflowed)
                {
                    Kill(process);
                    return new BoundedProcessResult(BoundedProcessOutcome.OutputLimitExceeded, -1, string.Empty);
                }

                var exit = Task.WhenAll(stderrDrain, process.WaitForExitAsync(CancellationToken.None));
                if (await Task.WhenAny(exit, expiry).ConfigureAwait(false) != exit)
                    return TimedOut(process, ct);

                return new BoundedProcessResult(BoundedProcessOutcome.Exited, process.ExitCode, text);
            }
            finally
            {
                deadline.Cancel();
            }
        }
    }

    private static BoundedProcessResult TimedOut(Process process, CancellationToken ct)
    {
        Kill(process);
        ct.ThrowIfCancellationRequested();
        return new BoundedProcessResult(BoundedProcessOutcome.TimedOut, -1, string.Empty);
    }

    private static void Kill(Process process)
    {
        // Do not await the streams afterwards: grandchildren can still hold the pipe handles.
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) > 0) { }
    }

    private static async Task<(string Text, bool Overflowed)> ReadBoundedAsync(StreamReader reader, int maxChars)
    {
        var builder = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            if (builder.Length + read > maxChars)
            {
                builder.Clear();
                return (string.Empty, true);
            }
            builder.Append(buffer, 0, read);
        }
        return (builder.ToString(), false);
    }
}
