using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Twig;

public static class CommandEndpoint
{
    public static string PipeName(string data, Guid serverId)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(data));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{path}\n{serverId:D}"));
        return "twig-command-v1-" + Convert.ToHexString(hash.AsSpan(0, 20));
    }

    public static string SelectedPipe()
    {
        var root = Environment.GetEnvironmentVariable("TWIG_SERVER_INSTALL_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Twig", "Server");
        RequireLocalPath(root);
        if (OperatingSystem.IsWindows() && !Path.GetFullPath(root).StartsWith(
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected installation must be under the user's local application data.");
        using var selection = JsonDocument.Parse(ReadSmall(Path.Combine(root, "current.json"), 16384));
        var data = selection.RootElement.GetProperty("privateDataDir").GetString();
        RequireLocalPath(data);
        if (!Guid.TryParseExact(ReadSmall(Path.Combine(data!, "server.id"), 64), "D", out var id) || id == Guid.Empty)
            throw new InvalidDataException("Invalid selected server identity.");
        return PipeName(data!, id);
    }

    public static void RequireLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || (OperatingSystem.IsWindows() && (path.StartsWith('\\') || path.StartsWith('/'))))
            throw new InvalidDataException("A local absolute path is required.");
    }

    private static string ReadSmall(string file, int maximum)
    {
        for (FileSystemInfo? entry = new FileInfo(Path.GetFullPath(file)); entry != null;
            entry = entry is FileInfo f ? f.Directory : ((DirectoryInfo)entry).Parent)
            if (entry.LinkTarget != null || (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw new InvalidDataException("Linked server selection paths are not supported.");
        using var input = File.OpenRead(file);
        if (input.Length > maximum) throw new InvalidDataException("Oversized server selection record.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }
}
