using System.Text.Json;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Owns the explicitly selected identity's private refresh-token store.
/// Atomic write (tmp + rename), chmod 600 on Unix. Best-effort everywhere — failures are silent.
/// </summary>
internal sealed class TwigRefreshTokenStore
{
    private readonly string _path;

    public TwigRefreshTokenStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path => _path;

    public bool Exists() => File.Exists(_path);

    /// <summary>
    /// Reads the stored refresh token entry. Returns null if the file is missing,
    /// corrupt, or unreadable. Callers must be defensive — never throws.
    /// </summary>
    public TwigRefreshTokenStoreEntry? TryRead()
    {
        try
        {
            if (!File.Exists(_path)) return null;

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize(stream, TwigJsonContext.Default.TwigRefreshTokenStoreEntry);
        }
        catch
        {
            return null;
        }
    }

    public void TryWrite(TwigRefreshTokenStoreEntry entry)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            var tmpPath = _path + ".tmp";
            var json = JsonSerializer.Serialize(entry, TwigJsonContext.Default.TwigRefreshTokenStoreEntry);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _path, overwrite: true);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort; a later refresh still uses only this selected credential.
        }
    }

    public void TryDelete()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch
        {
            // Best effort.
        }
    }
}
