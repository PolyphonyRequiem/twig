using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Twig.Commands;

[SupportedOSPlatform("windows")]
internal static partial class ServerEndpointProcess
{
    internal static async Task<string> RunAsync(string launcher, string root, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var job = CreateJob();
        var gateName = @"Local\Twig.Endpoint." + Guid.NewGuid().ToString("N");
        using var gate = new EventWaitHandle(false, EventResetMode.ManualReset, gateName);
        // Only trusted bootstrap code runs before assignment. The selected script cannot spawn outside the job.
        var command = "$ErrorActionPreference='Stop'; $gate=[Threading.EventWaitHandle]::OpenExisting('"
            + gateName + "'); try { if (-not $gate.WaitOne(5000)) { throw 'Endpoint ownership gate expired.' } }"
            + " finally { $gate.Dispose() }; & '" + launcher.Replace("'", "''") + "' -Endpoint";
        var start = ServerLifecycle.WindowsOperatorStartInfo(root);
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            start.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("The endpoint discovery operator did not run.");
        var assigned = false;
        var reads = new List<Task<string>>(2);
        try
        {
            if (!AssignProcessToJobObject(job, process.SafeHandle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            assigned = true;
            deadline.Token.ThrowIfCancellationRequested();
            gate.Set();
            process.StandardInput.Close();
            var output = ServerLifecycle.ReadEndpointOutputAsync(process.StandardOutput, deadline.Token);
            var errors = ServerLifecycle.ReadEndpointOutputAsync(process.StandardError, deadline.Token);
            reads.Add(output);
            reads.Add(errors);
            var exit = process.WaitForExitAsync(deadline.Token);
            var pending = new List<Task> { output, errors, exit };
            while (pending.Count != 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(deadline.Token);
                await completed;
                pending.Remove(completed);
                if (completed == exit) await StopJobAsync(job, deadline.Token);
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The server launcher exited with code {process.ExitCode}: {await errors}");
            return await output;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new IOException("Server endpoint discovery exceeded its 20-second deadline.", error);
        }
        finally
        {
            await deadline.CancelAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (assigned) await StopJobAsync(job, cleanup.Token);
                else
                {
                    // Assignment failed while the trusted bootstrap was still gated, so it has no descendants.
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
                await process.WaitForExitAsync(cleanup.Token);
                Task drained = Task.WhenAll(reads);
                await Task.WhenAny(drained, Task.Delay(Timeout.InfiniteTimeSpan, cleanup.Token)).WaitAsync(cleanup.Token);
                await drained.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            catch (OperationCanceledException error)
            {
                throw new IOException("Endpoint discovery process-tree cleanup was not confirmed within two seconds.", error);
            }
        }
    }

    private static unsafe SafeFileHandle CreateJob()
    {
        var job = CreateJobObject(0, null);
        if (job.IsInvalid)
        {
            var failure = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(failure);
        }
        var limits = new ExtendedLimits { Basic = new() { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE, no breakaway.
        if (SetInformationJobObject(job, 9, ref limits, (uint)sizeof(ExtendedLimits))) return job;
        var error = Marshal.GetLastPInvokeError();
        job.Dispose();
        throw new Win32Exception(error);
    }

    private static async Task StopJobAsync(SafeFileHandle job, CancellationToken token)
    {
        if (ActiveProcesses(job) == 0) return;
        if (!TerminateJobObject(job, 1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        while (ActiveProcesses(job) != 0) await Task.Delay(10, token);
    }

    private static unsafe uint ActiveProcesses(SafeFileHandle job)
    {
        if (!QueryInformationJobObject(job, 1, out var accounting, (uint)sizeof(Accounting), 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return accounting.Active;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveLimit;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong Reads, Writes, Others, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long User, Kernel, PeriodUser, PeriodKernel;
        public uint Faults, Total, Active, Terminated;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits limits, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out Accounting accounting, uint length, nint returnedLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
