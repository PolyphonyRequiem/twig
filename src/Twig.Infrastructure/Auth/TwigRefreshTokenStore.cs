using System.Text.Json;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Owns <c>~/.twig/.refresh-token</c>: twig's private refresh-token store. After a one-time
/// bootstrap from the MSAL cache, every subsequent token mint reads from here instead of
/// <c>~/.azure/msal_token_cache.json</c> — eliminating the foreign-mutation hazard surface.
/// Atomic write (tmp + rename), chmod 600 on Unix. Best-effort everywhere — failures are silent.
/// </summary>
internal sealed class TwigRefreshTokenStore
{
    private static readonly string DefaultPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".twig", ".refresh-token");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _path;

    public TwigRefreshTokenStore(string? path = null)
    {
        _path = path ?? DefaultPath;
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
            // Best effort — provider falls back to bootstrap-on-next-call.
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
