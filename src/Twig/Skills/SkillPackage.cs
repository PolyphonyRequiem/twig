using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Twig.Skills;

/// <summary>Executable-local, version/build-bound procedural guidance. No checkout or network reads.</summary>
internal sealed class SkillPackage
{
    internal const string ResourcePrefix = "Twig.Skills.Package.";
    private readonly Dictionary<string, byte[]> files;
    internal string Version { get; }
    internal string Identity { get; }

    internal SkillPackage(string version, IReadOnlyDictionary<string, byte[]> source)
    {
        Version = version;
        files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var portablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in source.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            SkillPaths.ValidateManagedPath(path);
            if (!portablePaths.Add(path))
                throw new SkillLifecycleException($"Package path has a case-insensitive alias: {path}.");
            if (path == SkillLifecycle.SelectionPath)
                throw new SkillLifecycleException("The package must not embed configure-managed user-selections.json.");
            // Only text guidance is transformed; supporting binary assets remain byte-identical.
            byte[] content;
            if (path.EndsWith(".md", StringComparison.Ordinal))
            {
                var text = Encoding.UTF8.GetString(bytes).Replace("{{TWIG_VERSION}}", version, StringComparison.Ordinal);
                if (path.EndsWith("/SKILL.md", StringComparison.Ordinal))
                    text += $"\n<!-- Twig bundled guidance: {version} -->\n";
                content = Encoding.UTF8.GetBytes(text);
            }
            else content = bytes.ToArray();
            files.Add(path, content);
        }
        foreach (var path in files.Keys)
            for (var parent = path.LastIndexOf('/'); parent > 0; parent = path.LastIndexOf('/', parent - 1))
                if (portablePaths.Contains(path[..parent]))
                    throw new SkillLifecycleException($"Package file conflicts with a required directory: {path}.");
        foreach (var name in SkillPaths.Family)
            if (!files.ContainsKey(name + "/SKILL.md"))
                throw new SkillLifecycleException($"Bundled Twig package is incomplete: missing {name}/SKILL.md.");
        var index = string.Join("\n", files.Select(p => p.Key + ":" + Hash(p.Value)));
        Identity = version + ":sha256:" + Hash(Encoding.UTF8.GetBytes(index));
    }

    internal static SkillPackage Load()
    {
        var assembly = typeof(SkillPackage).Assembly;
        // VersionHelper intentionally strips +build metadata for self-update comparisons.
        // Preserve it here so guidance from different builds cannot masquerade as current.
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? VersionHelper.GetVersion();
        var source = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new SkillLifecycleException($"Missing bundled resource {name}.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            source.Add(name[ResourcePrefix.Length..], buffer.ToArray());
        }
        return new SkillPackage(version, source);
    }

    internal IReadOnlyDictionary<string, byte[]> GetFiles() => files;
    internal string ShowEntry() => $"Twig guidance package: {Identity}\n\n" + Encoding.UTF8.GetString(files["twig-cli/SKILL.md"]);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal sealed class SkillLifecycleException(string message) : Exception(message);

/// <summary>Portable lexical containment plus rejection of symlink/reparse-point ancestors.</summary>
internal static class SkillPaths
{
    internal static readonly string[] Family = ["twig-cli", "twig-changes"];
    internal static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static void ValidateManagedPath(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length < 2 || !Family.Contains(parts[0], StringComparer.Ordinal)
            || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            || parts.Any(p => p.EndsWith('.') || IsDeviceName(p)))
            throw new SkillLifecycleException($"Unsafe managed package path '{relative}'. Only flat Twig family paths are allowed.");
    }

    internal static bool IsExternalName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 64
        && name![0] != '-' && name[^1] != '-' && !Family.Contains(name, StringComparer.Ordinal) && !IsDeviceName(name)
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    internal static void ValidateExternalName(string? name)
    {
        if (!IsExternalName(name))
            throw new SkillLifecycleException($"Invalid external skill name '{name}'. Use 1-64 lowercase letters, digits or internal hyphens; separate from the Twig family names.");
    }

    internal static void ValidateExternalRelative(string name, string relative)
    {
        ValidateExternalName(name);
        var parts = relative.Split('/');
        if (parts.Length < 1 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            || parts.Any(p => p.EndsWith('.') || IsDeviceName(p)))
            throw new SkillLifecycleException($"Unsafe external file path '{name}/{relative}'.");
    }

    private static bool IsDeviceName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }

    internal static string Target(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new SkillLifecycleException("An explicit --target PATH skills root is required; no provider home/profile is inferred. If the provider is missing, select a custom target and register it with the provider later.");
        var full = Path.GetFullPath(target);
        RejectLinks(full);
        if (File.Exists(full)) throw new SkillLifecycleException($"Target is a file: {full}.");
        return full;
    }

    internal static string Under(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison))
            throw new SkillLifecycleException($"Path escapes target: {relative}.");
        RejectLinks(path);
        return path;
    }

    internal static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            // LinkTarget also detects dangling links, for which Exists returns false.
            var info = new FileInfo(current);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                || (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
                throw new SkillLifecycleException($"Refusing symlink/reparse point: {current}. Choose a real directory and preserve or relocate the link yourself.");
        }
    }
}
