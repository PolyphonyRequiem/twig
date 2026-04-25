using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Twig.Mcp.Services;

/// <summary>
/// Monitors the parent process and triggers graceful shutdown when it exits.
/// Prevents orphaned twig-mcp processes when the host (VS Code, Copilot CLI)
/// terminates without cleanly closing the stdio pipe.
/// </summary>
sealed partial class ParentProcessWatchdog(
    IHostApplicationLifetime lifetime,
    ILogger<ParentProcessWatchdog> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var parentPid = GetParentProcessId();
        if (parentPid <= 0)
        {
            logger.LogWarning("Could not determine parent process ID; watchdog disabled.");
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(parentPid);
        }
        catch
        {
            logger.LogInformation("Parent process {ParentPid} already exited; shutting down.", parentPid);
            lifetime.StopApplication();
            return;
        }

        logger.LogDebug("Watching parent process {ParentPid}.", parentPid);

        using (parent)
        {
            await WaitForParentExitAsync(parent, stoppingToken);
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Parent process {ParentPid} exited; shutting down.", parentPid);
            lifetime.StopApplication();
        }
    }

    private static async Task WaitForParentExitAsync(Process parent, CancellationToken ct)
    {
        try
        {
            await parent.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown — don't fall through to polling
        }
        catch
        {
            // WaitForExitAsync may fail on some platforms; fall back to polling
            await PollForExitAsync(parent, ct);
        }
    }

    private static async Task PollForExitAsync(Process parent, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            try
            {
                if (parent.HasExited)
                    return;
            }
            catch
            {
                return;
            }
        }
    }

    internal static int GetParentProcessId()
    {
        if (OperatingSystem.IsWindows())
            return GetParentPidWindows();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return Getppid();
        return -1;
    }

    // --- Windows: NtQueryInformationProcess ---

    private static int GetParentPidWindows()
    {
        var pbi = new PROCESS_BASIC_INFORMATION();
        int status = NtQueryInformationProcess(
            Process.GetCurrentProcess().Handle,
            0, // ProcessBasicInformation
            ref pbi,
            Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
            out _);
        return status == 0 ? (int)pbi.InheritedFromUniqueProcessId : -1;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        nint processHandle,
        int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public nint Reserved1;
        public nint PebBaseAddress;
        public nint Reserved2_0;
        public nint Reserved2_1;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    // --- Unix: libc getppid() ---

    [LibraryImport("libc", EntryPoint = "getppid")]
    private static partial int Getppid();
}
