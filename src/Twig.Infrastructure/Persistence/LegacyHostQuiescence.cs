using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Twig.Infrastructure.Persistence;

/// <summary>Lifetime capabilities are not operation leases. Legacy providers must actually close.</summary>
internal sealed class LegacyHostCapability : IDisposable
{
    private readonly FileStream? _handle;
    private readonly string? _home;
    private bool _disposed;

    internal LegacyHostCapability(string directory, bool authentication = false)
    {
        directory = Path.GetFullPath(directory);
        _home = authentication ? directory : null;
        if (authentication) RefuseRetiredAuthentication(directory);
        Directory.CreateDirectory(directory);
        _handle = new FileStream(Path.Combine(directory, ".legacy-host-capability"), FileMode.OpenOrCreate,
            FileAccess.Read, FileShare.Read);
    }

    internal void Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_home is not null) RefuseRetiredAuthentication(_home);
    }

    private static void RefuseRetiredAuthentication(string home)
    {
        var database = Path.Combine(home, "system.db");
        if (!File.Exists(database)) return;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='connection_migrations';";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return;
        cmd.CommandText = "SELECT count(*) FROM connection_migrations;";
        if (Convert.ToInt64(cmd.ExecuteScalar()) != 0)
            throw new InvalidOperationException("legacy-auth-retired: this metadata home has a native migration intent. Reconnect through the explicit connection binding; legacy/global authentication cannot resume work HTTP.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle?.Dispose();
    }
}

/// <summary>Inventories OS hosts and actual open store/capability handles; assertions are not evidence.</summary>
internal static class LegacyHostQuiescence
{
    internal static IReadOnlyList<string> InspectProcesses(Func<Process, string>? commandLineReader = null)
    {
        var blockers = new List<string>();
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return ["host-inventory-unavailable: supported host closure cannot be verified on this platform; close hosts and use a supported migration platform."];
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ["host-inventory-unavailable: OS process enumeration refused; restore inspection permissions and rerun. No activation occurred."];
        }
        foreach (var process in processes)
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    var name = process.ProcessName;
                    if (name.StartsWith("twig", StringComparison.OrdinalIgnoreCase))
                    {
                        blockers.Add($"legacy-host-open: {name} pid={process.Id}; explicitly close this CLI/TUI/MCP/Herdr subprocess, then rerun.");
                        continue;
                    }
                    if (!name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) continue;
                    var commandLine = (commandLineReader ?? ReadCommandLine)(process);
                    if (commandLine.Contains("twig.dll", StringComparison.OrdinalIgnoreCase)
                        || commandLine.Contains("twig-mcp.dll", StringComparison.OrdinalIgnoreCase)
                        || commandLine.Contains("Twig.Tui.dll", StringComparison.OrdinalIgnoreCase))
                        blockers.Add($"legacy-host-open: dotnet pid={process.Id}; explicitly close its Twig CLI/TUI/MCP subprocess, then rerun.");
                }
                catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    var exited = false;
                    try { exited = process.HasExited; }
                    catch (Exception inspectionError) when (inspectionError is Win32Exception or InvalidOperationException) { }
                    if (!exited)
                        blockers.Add($"host-inventory-inconclusive: pid={process.Id}; inspection failed. Establish closure/inspection permissions before activation.");
                }
            }
        }
        return blockers;
    }

    internal static List<FileStream> AcquireExclusiveHandles(IEnumerable<string> paths)
    {
        var handles = new List<FileStream>();
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var affected = paths.Where(File.Exists).Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
                foreach (var process in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(process), out _)) continue;
                    try
                    {
                        foreach (var fd in Directory.EnumerateFiles(Path.Combine(process, "fd")))
                        {
                            var target = File.ResolveLinkTarget(fd, false)?.FullName;
                            if (target is not null && affected.Contains(target))
                                throw new InvalidOperationException($"legacy-store-open: {target}; explicitly close the owning host/store before activation.");
                        }
                    }
                    catch (DirectoryNotFoundException) { }
                    catch (UnauthorizedAccessException)
                    {
                        throw new InvalidOperationException("store-inventory-inconclusive: /proc handle inspection denied; restore permissions before activation.");
                    }
                }
            }
            foreach (var path in paths.Distinct(SqlitePlanJournalRepository.CreateDefaultSourcePathComparer()))
            {
                if (!File.Exists(path)) continue;
                // Deny readers and writers while allowing our own rename into the fenced layout.
                handles.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete));
            }
            return handles;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var handle in handles) handle.Dispose();
            throw new InvalidOperationException("legacy-store-open: an affected database, credential or capability is still open. Explicitly close its host/provider/store and rerun; no force bypass exists.", ex);
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }
    }

    private static string ReadCommandLine(Process process)
    {
        if (OperatingSystem.IsLinux()) return File.ReadAllText($"/proc/{process.Id}/cmdline");
        var status = NtQueryInformationProcess(process.Handle, 60, IntPtr.Zero, 0, out var size);
        if (size <= 0) throw new Win32Exception(status);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = NtQueryInformationProcess(process.Handle, 60, buffer, size, out _);
            if (status < 0) throw new Win32Exception(status);
            var length = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);
            return Marshal.PtrToStringUni(text, length / 2) ?? throw new InvalidOperationException("Missing host command line.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int informationClass,
        IntPtr information, int informationLength, out int returnLength);
}
