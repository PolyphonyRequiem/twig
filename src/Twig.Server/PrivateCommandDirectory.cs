using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Twig.Server;

internal static class PrivateCommandDirectory
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static string Open(string path)
    {
        Twig.CommandEndpoint.RequireLocalPath(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full == Path.GetPathRoot(full)
            || path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(p => p is "." or "..")
            || (OperatingSystem.IsWindows() && path.AsSpan(2).Contains(':')))
            throw new InvalidDataException("A dedicated local directory without aliases is required.");
        var dir = new DirectoryInfo(full);
        for (var ancestor = dir; ancestor != null; ancestor = ancestor.Parent)
            RejectLink(ancestor);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("Missing server owner.");
            if (!dir.Exists)
            {
                var acl = new DirectorySecurity();
                acl.SetOwner(user);
                acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new(user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                dir.Create(acl);
            }
            CheckWindows(dir.GetAccessControl(), user);
            foreach (var file in Metadata(dir))
                CheckWindows(file.GetAccessControl(), user);
        }
        else if (OperatingSystem.IsLinux())
        {
            // A private leaf is insufficient if someone else can rename it. A sticky
            // root-owned /tmp is fine; an ordinary shared writable ancestor is not.
            for (var parent = dir.Parent; parent != null; parent = parent.Parent)
            {
                if (!parent.Exists) continue;
                var st = ReadStat(parent.FullName);
                if ((st.Mode & 0xf000) != 0x4000 || (st.Uid != 0 && st.Uid != EffectiveUser())
                    || ((st.Mode & 0x12) != 0 && (st.Mode & 0x200) == 0))
                    throw new InvalidDataException("Unsafe parent of the private server directory.");
            }
            Directory.CreateDirectory(full, Private);
            if (File.GetUnixFileMode(full) != Private)
                throw new InvalidDataException("The private server directory must have mode 0700.");
            CheckLinux(full, directory: true);
            foreach (var file in Metadata(dir)) CheckLinux(file.FullName, directory: false);
        }
        else throw new PlatformNotSupportedException("The command host requires Windows or Linux.");
        return full;
    }

    private static IEnumerable<FileInfo> Metadata(DirectoryInfo dir)
    {
        foreach (var name in new[] { "server.lock", "server.id" })
        {
            var file = new FileInfo(Path.Combine(dir.FullName, name));
            RejectLink(file);
            if (file.Exists) yield return file;
        }
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        // LinkTarget also detects dangling links, which Exists would treat as absent.
        if (entry.LinkTarget != null || (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new InvalidDataException("Reparse-point server paths are not supported.");
    }

    [SupportedOSPlatform("windows")]
    private static void CheckWindows(FileSystemSecurity acl, SecurityIdentifier user)
    {
        if (!user.Equals(acl.GetOwner(typeof(SecurityIdentifier))))
            throw new InvalidDataException("The private server path must be owned by the current user.");
        if (new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl == null)
            throw new InvalidDataException("The private server path must have an access-control list.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType == AccessControlType.Allow && !user.Equals(sid)
                && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                && !sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid))
                throw new InvalidDataException("The server path grants access to another principal.");
        }
    }

    // Linux statx has the same layout on all architectures. lstat semantics: never
    // accept a linked metadata file, special file, or another user's mode-0700 directory.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx")]
    private static extern int Stat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out Statx stat);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUser();

    private static Statx ReadStat(string path)
    {
        const uint required = 0x0f; // type, mode, nlink, uid
        if (Stat(-100, path, 0x100, required, out var stat) != 0 || (stat.Mask & required) != required)
            throw new InvalidDataException("Could not verify server path metadata.");
        return stat;
    }

    private static void CheckLinux(string path, bool directory)
    {
        var stat = ReadStat(path);
        if (stat.Uid != EffectiveUser() || (stat.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)
            || (!directory && (stat.Links != 1 || (stat.Mode & 0x12) != 0)))
            throw new InvalidDataException("Invalid private server path ownership, type or permissions.");
    }
}
