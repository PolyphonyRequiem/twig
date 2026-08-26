using System.Text.Json;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// AB#736 §4.3 system-local storage root layout materializer. The system
/// tier is REQUIRED to contain <c>layout.json</c>, <c>system.db</c>, and
/// <c>tmp/</c>. <c>system.db</c> is created lazily by
/// <see cref="SqliteSystemWorktreeRegistry"/> on first open, but the
/// marker and temp directory are materialized here so the tier is
/// complete before any consumer touches the DB.
/// <para>
/// The marker file is written atomically via a temp file rename; if a
/// marker with a different (newer) version exists the current binary
/// leaves it untouched and refuses to downgrade — the caller decides
/// whether the mismatch is fatal.
/// </para>
/// </summary>
internal static class SystemStoreLayout
{
    internal const string LayoutFileName = "layout.json";
    internal const string TmpDirName = "tmp";

    /// <summary>
    /// Ensures <paramref name="systemRoot"/> contains <c>layout.json</c> and
    /// <c>tmp/</c>. Idempotent — a valid marker at the current version is
    /// preserved verbatim. Best-effort I/O: a materialization failure is
    /// non-fatal; the caller sees a subsequent registry open failure with
    /// the named identifier instead.
    /// </summary>
    public static void EnsureRoot(string systemRoot, TimeProvider clock)
    {
        if (string.IsNullOrEmpty(systemRoot)) return;
        try
        {
            Directory.CreateDirectory(systemRoot);
            Directory.CreateDirectory(Path.Combine(systemRoot, TmpDirName));

            var markerPath = Path.Combine(systemRoot, LayoutFileName);
            if (File.Exists(markerPath))
            {
                // Preserve an existing marker — a newer-shape marker means a
                // future binary wrote it, and this binary must not downgrade
                // it silently. The registry open path will fail closed if
                // schema disagrees; the marker itself is left intact.
                return;
            }

            var doc = new LayoutMarkerDocument(
                Schema: LayoutMarkerDocument.CurrentSchema,
                Version: LayoutMarkerDocument.CurrentVersion,
                InitializedAt: clock.GetUtcNow().ToString("o"),
                CreatedBy: "twig-cli/system");
            var tmpPath = markerPath + $".{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, doc, TwigJsonContext.Default.LayoutMarkerDocument);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmpPath, markerPath, overwrite: true);
        }
        catch (IOException) { /* best-effort; downstream registry open will surface a named failure */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }
}
