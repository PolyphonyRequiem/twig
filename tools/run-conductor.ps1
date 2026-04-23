<#
.SYNOPSIS
    Launches a conductor workflow in a Job Object so all child processes
    (MCP servers, Copilot CLI, node, etc.) are killed on exit.

.DESCRIPTION
    Windows doesn't kill grandchild processes when a parent exits. Conductor
    spawns MCP servers (via npx) as grandchildren of the Copilot CLI process.
    When conductor exits, those MCP servers survive and lock worktree dirs.

    This wrapper creates a Windows Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    assigns the conductor process to it, and ensures all descendants are
    terminated when the wrapper exits (or is killed).

.PARAMETER WorkingDirectory
    The directory to run conductor from (typically a worktree).

.PARAMETER Arguments
    Arguments to pass to conductor (everything after 'conductor').

.EXAMPLE
    .\run-conductor.ps1 -WorkingDirectory "C:\projects\twig2-1234" -Arguments "--silent run twig-sdlc@twig --input work_item_id=1234 --web"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkingDirectory,
    [Parameter(Mandatory)][string]$Arguments
)

$ErrorActionPreference = 'Stop'

# Validate inputs
if (-not (Test-Path $WorkingDirectory)) {
    Write-Error "WorkingDirectory does not exist: $WorkingDirectory"
    exit 1
}
if ([string]::IsNullOrWhiteSpace($Arguments)) {
    Write-Error "Arguments parameter is empty or whitespace. Ensure the value is quoted as a single string when passed via Start-Process."
    exit 1
}

Write-Host "WorkingDirectory: $WorkingDirectory"
Write-Host "Arguments: $Arguments"

# Create a Job Object that kills all processes when closed
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class JobObject {
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(IntPtr hJob, int jobInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    public static IntPtr Create() {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Exception("CreateJobObject failed");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE

        int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr ptr = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(info, ptr, false);

        if (!SetInformationJobObject(job, 9, ptr, (uint)size)) // 9 = JobObjectExtendedLimitInformation
            throw new Exception("SetInformationJobObject failed");

        Marshal.FreeHGlobal(ptr);
        return job;
    }

    public static void Assign(IntPtr job, IntPtr process) {
        if (!AssignProcessToJobObject(job, process))
            throw new Exception("AssignProcessToJobObject failed");
    }
}
"@

# Create job object
$job = [JobObject]::Create()

# Start conductor process
$conductorPath = (Get-Command conductor -ErrorAction Stop).Source
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $conductorPath
$psi.Arguments = $Arguments
$psi.WorkingDirectory = $WorkingDirectory
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $false
$psi.RedirectStandardError = $false

$process = [System.Diagnostics.Process]::Start($psi)

# Assign to job object — all children inherit
[JobObject]::Assign($job, $process.Handle)

Write-Host "Conductor PID $($process.Id) assigned to Job Object"
Write-Host "All child processes will be killed on exit"

# Wait for conductor to finish
$process.WaitForExit()
$exitCode = $process.ExitCode

# Close job object — kills ALL remaining children
[JobObject]::CloseHandle($job)

exit $exitCode
