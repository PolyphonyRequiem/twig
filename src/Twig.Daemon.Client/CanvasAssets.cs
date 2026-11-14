using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;

namespace Bench.Server;

public sealed record CanvasAsset(byte[] Bytes, string Version);

/// <summary>Shared verification contract for independently hosted immutable presentation packages.</summary>
public static partial class CanvasAssets
{
    public const string Protocol = "twig-bench-service-v1";
    public const string Application = "twig-canvas-assets";
    public static string DaemonVersion => typeof(CanvasAssets).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    public static readonly string[] RequiredFiles =
    [
        @"wwwroot\native-index.html", @"wwwroot\native-app.js", @"wwwroot\device.js",
        @"wwwroot\view-model.js", @"wwwroot\item-detail.js", @"wwwroot\assignee-profiles.js",
        @"wwwroot\item-editor.js", @"wwwroot\editor-drafts.js", @"wwwroot\connection-setup.js",
        @"wwwroot\style.css", @"wwwroot\icons.css", @"wwwroot\fonts\fluent-regular-v1.1.293.woff2",
        @"wwwroot\vendor\purify.es.mjs", @"wwwroot\vendor\marked.esm.js",
        @"licenses\azure-devops-ui.LICENSE", @"licenses\fluentui-system-icons.LICENSE",
        @"licenses\octicons.LICENSE", @"licenses\NOTICE.md", @"licenses\DOMPurify.LICENSE", @"licenses\marked.LICENSE"
    ];

    public static CanvasAsset Read(string installRoot, string asset)
    {
        LocalPath(installRoot);
        var selection = Path.Combine(installRoot, "current.json");
        using var pointer = Document(selection, 4096);
        var record = pointer.RootElement;
        var schema = record.GetProperty("schema").GetInt32();
        if (schema == 2)
        {
            Fields(record, "application", "schema", "version", "manifestHash", "hostManifestHash");
            if (!HashPattern().IsMatch(record.GetProperty("hostManifestHash").GetString()!))
                throw new InvalidDataException("Invalid presentation host digest.");
        }
        else Fields(record, "application", "schema", "version", "manifestHash");
        if (record.GetProperty("application").GetString() != Application || schema is not (1 or 2))
            throw new InvalidDataException("Foreign canvas selection.");
        var version = record.GetProperty("version").GetString()!;
        var manifestHash = record.GetProperty("manifestHash").GetString()!;
        if (!VersionPattern().IsMatch(version) || !HashPattern().IsMatch(manifestHash))
            throw new InvalidDataException("Invalid canvas version or digest.");
        var package = Path.Combine(installRoot, "versions", version);
        LocalPath(package);
        var manifestPath = Path.Combine(package, "assets.json");
        var manifestBytes = PackageBytes(manifestPath, 256 * 1024);
        if (Hash(manifestBytes) != manifestHash) throw new InvalidDataException("Canvas manifest integrity failed.");
        using var document = JsonDocument.Parse(manifestBytes);
        var manifest = document.RootElement;
        Fields(manifest, "application", "schema", "version", "protocol", "files");
        if (manifest.GetProperty("application").GetString() != Application || manifest.GetProperty("schema").GetInt32() != 1
            || manifest.GetProperty("version").GetString() != version || manifest.GetProperty("protocol").GetString() != Protocol)
            throw new InvalidDataException("Canvas package is incompatible with this daemon protocol.");
        var files = manifest.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is < 1 or > 256)
            throw new InvalidDataException("Canvas manifest file bounds exceeded.");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[]? requested = null;
        long total = 0;
        foreach (var file in files.EnumerateArray())
        {
            Fields(file, "path", "sha256");
            var relative = file.GetProperty("path").GetString()!;
            var hash = file.GetProperty("sha256").GetString()!;
            if (!RelativePattern().IsMatch(relative) || relative.Split('\\').Any(part => part is "." or "..")
                || !relative.StartsWith(@"wwwroot\", StringComparison.Ordinal) && !relative.StartsWith(@"licenses\", StringComparison.Ordinal)
                || !listed.Add(relative) || !HashPattern().IsMatch(hash))
                throw new InvalidDataException("Unsafe or duplicate canvas manifest path/digest.");
            var path = Path.Combine(package, relative.Replace('\\', Path.DirectorySeparatorChar));
            var bytes = PackageBytes(path, 16 * 1024 * 1024);
            total += bytes.Length;
            if (total > 64 * 1024 * 1024 || Hash(bytes) != hash) throw new InvalidDataException("Canvas file integrity failed.");
            if (relative == @"wwwroot\" + asset.Replace(Path.DirectorySeparatorChar, '\\')) requested = bytes;
        }
        if (RequiredFiles.Any(required => !listed.Contains(required)))
            throw new InvalidDataException("Canvas assets or required license notices are incomplete.");
        var pending = new Stack<string>();
        pending.Push(package);
        var actual = 0;
        var directories = 0;
        while (pending.TryPop(out var directory))
        {
            if (++directories > 512) throw new InvalidDataException("Canvas package directory bounds exceeded.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Canvas reparse points are forbidden.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else
                {
                    var relative = Path.GetRelativePath(package, path).Replace(Path.DirectorySeparatorChar, '\\');
                    if (relative != "assets.json" && !listed.Contains(relative)) throw new InvalidDataException("Unlisted canvas file.");
                    if (++actual > 257) throw new InvalidDataException("Canvas package file bounds exceeded.");
                }
            }
        }
        if (actual != listed.Count + 1) throw new InvalidDataException("Canvas package file inventory changed.");
        return new(requested ?? throw new FileNotFoundException("Allowlisted canvas asset is absent."), version);
    }

    private static byte[] Bytes(string path, int maximum)
    {
        LocalPath(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > maximum) throw new InvalidDataException("Canvas metadata/file exceeds its bound.");
        using var reader = new BinaryReader(stream);
        var bytes = reader.ReadBytes(maximum + 1);
        if (bytes.Length > maximum) throw new InvalidDataException("Canvas file grew beyond its bound.");
        return bytes;
    }
    private static byte[] PackageBytes(string path, int maximum)
    {
        try { return Bytes(path, maximum); }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidDataException("Selected canvas package content is missing.", error);
        }
    }
    private static JsonDocument Document(string path, int maximum) => JsonDocument.Parse(Bytes(path, maximum));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Fields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(fields.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Invalid canvas metadata fields.");
    }
    private static void LocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Canvas needs an absolute same-machine path.");
        for (var cursor = Path.GetFullPath(path); cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor))
                && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Canvas path contains a reparse point.");
    }
    [GeneratedRegex("^[a-f0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex("^[A-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+(\\[A-Za-z0-9_.-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativePattern();
}
