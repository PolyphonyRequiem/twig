using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Twig;

[SupportedOSPlatform("windows")]
internal static partial class WindowsCommandPeer
{
    internal static void Verify(SafePipeHandle pipe, Func<SafePipeHandle, SecurityIdentifier?>? readUser = null)
    {
        using var current = WindowsIdentity.GetCurrent();
        var user = current.User ?? throw new UnauthorizedAccessException("Cannot verify the current command user.");
        var serverUser = (readUser ?? ReadServerUser)(pipe);
        if (serverUser == null || serverUser != user)
            throw new UnauthorizedAccessException("The command pipe server is not verified as the current user.");
    }

    private static SecurityIdentifier? ReadServerUser(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var pid))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot identify the command pipe server process.");
        if (pid == 0) throw new UnauthorizedAccessException("Invalid command pipe server process.");
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot query the command pipe server process.");
        var opened = OpenProcessToken(process, 0x0008, out var token); // TOKEN_QUERY
        using (token)
        {
            if (!opened)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot query the command pipe server token.");
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            return identity.User;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
