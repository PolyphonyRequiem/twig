using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Twig.Browser;

/// <summary>The control registry is a private current-user capability, not a display-ID authority.</summary>
internal static partial class BrowserPrivateDirectory
{
    internal static string Open(string? isolatedRoot = null)
    {
        var configured = isolatedRoot ?? Environment.GetEnvironmentVariable("TWIG_BROWSER_STATE_DIR");
        var root = Path.GetFullPath(configured ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Twig", "browser-aot", "control"));
        if (OperatingSystem.IsWindows()) OpenWindows(root);
        else OpenUnix(root);
        return root;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Browser control state must not be a symbolic link or reparse point.");
    }

    private static void OpenWindows(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Cannot establish current-user control ownership.");
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(security);
        }
        RejectLink(root);
        var current = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!user.Equals(current.GetOwner(typeof(SecurityIdentifier))) || !current.AreAccessRulesProtected)
            throw new IOException("Browser control directory is not privately owned by the current user.");
        foreach (FileSystemAccessRule rule in current.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !user.Equals(rule.IdentityReference))
                throw new IOException("Browser control directory grants access to another principal; registration refused.");
    }

    private static void OpenUnix(string root)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("POSIX control permissions require Unix.");
        if (!((OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64) ||
            (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)))
            throw new PlatformNotSupportedException("Private native browser control supports linux-x64 and osx-arm64.");
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RejectLink(root);
        if ((File.GetUnixFileMode(root) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("Browser control directory is not private (0700 required).");
        ValidateUnixOwner(root);
    }

    internal static void ValidateFile(string path)
    {
        RejectLink(path);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new IOException("Cannot establish current-user control ownership.");
            var current = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (!user.Equals(current.GetOwner(typeof(SecurityIdentifier)))) throw new IOException("Registration belongs to another user.");
            foreach (FileSystemAccessRule rule in current.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !user.Equals(rule.IdentityReference))
                    throw new IOException("Registration is not private to the current user.");
        }
        else
        {
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new IOException("Browser control registration is not private (0600 required).");
            ValidateUnixOwner(path);
        }
    }

    private static unsafe void ValidateUnixOwner(string path)
    {
        // Qualified ABIs only: Linux x64 uid offset28, Darwin arm64 uid offset16.
        var buffer = stackalloc byte[256];
        if (Stat(path, buffer) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot verify control owner.");
        var owner = *(uint*)(buffer + (OperatingSystem.IsMacOS() ? 16 : 28));
        if (owner != GetEffectiveUserId()) throw new IOException("Browser control state belongs to another user.");
    }

    internal static FileStream CreateExclusive(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int Stat(string path, byte* buffer);
    [LibraryImport("libc", EntryPoint = "geteuid")] private static partial uint GetEffectiveUserId();
}
