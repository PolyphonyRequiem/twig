using System.Globalization;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Cross-process token cache backed by <c>~/.twig/.token-cache</c>.
/// Atomic write (tmp + rename) so concurrent readers never see a partial file.
/// On Unix, the file is chmod'd to 600. Best-effort everywhere — failures are silent.
/// </summary>
internal sealed class TwigTokenFileCache
{
    private static readonly string DefaultCachePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".twig", ".token-cache");

    private readonly string _cachePath;

    public TwigTokenFileCache(string? cachePath = null)
    {
        _cachePath = cachePath ?? DefaultCachePath;
    }

    public string Path => _cachePath;

    /// <summary>
    /// Reads the cached token. Returns (null, default) if the file is missing,
    /// corrupt, or unreadable. Callers should also validate the token's audience.
    /// </summary>
    public (string? Token, DateTimeOffset Expiry) TryRead()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return (null, default);

            var lines = File.ReadAllLines(_cachePath);
            if (lines.Length < 2)
                return (null, default);

            if (!long.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                return (null, default);

            var expiry = new DateTimeOffset(ticks, TimeSpan.Zero);
            var token = lines[1];

            if (string.IsNullOrWhiteSpace(token))
                return (null, default);

            return (token, expiry);
        }
        catch
        {
            return (null, default);
        }
    }

    public void TryWrite(string token, DateTimeOffset expiry)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_cachePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            var tmpPath = _cachePath + ".tmp";
            File.WriteAllText(tmpPath,
                $"{expiry.UtcTicks.ToString(CultureInfo.InvariantCulture)}\n{token}\n");
            File.Move(tmpPath, _cachePath, overwrite: true);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_cachePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort — in-memory cache is still functional.
        }
    }

    public void TryDelete()
    {
        try
        {
            if (File.Exists(_cachePath))
                File.Delete(_cachePath);
        }
        catch
        {
            // Best effort.
        }
    }
}
