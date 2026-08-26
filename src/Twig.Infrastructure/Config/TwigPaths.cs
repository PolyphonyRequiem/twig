namespace Twig.Infrastructure.Config;

/// <summary>
/// Paths to the .twig directory and its contents.
/// <para>
/// AB#736 T1 §4.2.4: the SQLite cache lives at <c>.twig/cache/twig.db</c> —
/// a single per-worktree DB. The pre-T1 <c>.twig/{org}/{project}/twig.db</c>
/// nested layout is retired without a compatibility shim; call sites reach
/// the DB only through <see cref="DbPath"/>.
/// </para>
/// </summary>
public sealed class TwigPaths
{
    /// <summary>Root .twig directory (e.g., <c>/repo/.twig</c>).</summary>
    public string TwigDir { get; }

    /// <summary>
    /// The directory where twig was invoked (CWD at process start).
    /// Unlike <see cref="TwigDir"/>, this is never walked-up — it always
    /// reflects the user's actual working directory. Used by <c>twig init</c>
    /// to create a workspace in the current directory rather than reusing
    /// an ancestor's <c>.twig/</c>.
    /// </summary>
    public string StartDir { get; }

    /// <summary>Path to the per-user config file: <c>.twig/config</c>. AB#3296: gitignored, holds preferences only.</summary>
    public string ConfigPath { get; }

    /// <summary>
    /// Path to the committed repo coordinates file at the repo root: <c>&lt;repo-root&gt;/twig.json</c>.
    /// Derived as the parent of <see cref="TwigDir"/>. AB#3296: this file is committed and reviewed;
    /// every contributor needs these coordinates to talk to the same ADO project.
    /// </summary>
    public string RepoConfigPath { get; }

    /// <summary>
    /// The repo root directory (parent of <see cref="TwigDir"/>). Useful for placing
    /// committed manifest files (<c>twig.json</c>) and for editing <c>.gitignore</c>.
    /// </summary>
    public string RepoRoot { get; }

    /// <summary>Path to the SQLite database — AB#736 §4.2.4: <c>.twig/cache/twig.db</c>.</summary>
    public string DbPath { get; }

    /// <summary>Path to the status-fields configuration file: <c>.twig/status-fields</c>.</summary>
    public string StatusFieldsPath => Path.Combine(TwigDir, "status-fields");

    /// <summary>Path to the tracking file: <c>.twig/tracking.json</c>.</summary>
    public string TrackingFilePath => Path.Combine(TwigDir, "tracking.json");

    public TwigPaths(string twigDir, string configPath, string dbPath, string? startDir = null)
    {
        TwigDir = twigDir;
        ConfigPath = configPath;
        DbPath = dbPath;
        StartDir = startDir ?? Directory.GetCurrentDirectory();
        RepoRoot = Path.GetDirectoryName(twigDir) ?? twigDir;
        RepoConfigPath = Path.Combine(RepoRoot, "twig.json");
    }

    /// <summary>
    /// Characters that are unsafe in file-system path segments.
    /// </summary>
    private static readonly char[] InvalidChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Replaces filesystem-unsafe characters (<c>/ \ : * ? " &lt; &gt; |</c>) with underscores.
    /// Leading/trailing whitespace and dots are trimmed to prevent issues on Windows.
    /// Empty or whitespace-only input returns <c>"_"</c>.
    /// </summary>
    public static string SanitizePathSegment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "_";

        var result = name;
        foreach (var c in InvalidChars)
            result = result.Replace(c, '_');

        // Trim leading/trailing whitespace and dots (Windows disallows trailing dots/spaces in dir names)
        result = result.Trim().Trim('.');

        return string.IsNullOrEmpty(result) ? "_" : result;
    }

    /// <summary>
    /// AB#736 T1 §4.2.4: the SQLite cache path is <c>.twig/cache/twig.db</c>,
    /// per-worktree, opaque to org/project. The <paramref name="org"/> and
    /// <paramref name="project"/> parameters are accepted for signature
    /// compatibility but no longer segment the path; the T1 clean cutover
    /// retires the nested <c>.twig/{org}/{project}/twig.db</c> layout
    /// completely.
    /// </summary>
    public static string GetContextDbPath(string twigDir, string org, string project)
    {
        _ = org;
        _ = project;
        return Path.Combine(twigDir, "cache", "twig.db");
    }

    /// <summary>The T1 §4.2.4 canonical cache DB path. Prefer this on new
    /// call sites; <see cref="GetContextDbPath"/> is retained for legacy
    /// callers that still pass org/project.</summary>
    public static string GetCacheDbPath(string twigDir) =>
        Path.Combine(twigDir, "cache", "twig.db");

    /// <summary>
    /// Creates a <see cref="TwigPaths"/> for the given worktree. The org and
    /// project arguments are accepted for signature stability but no longer
    /// affect the DB path (T1 clean cutover, §4.2.4).
    /// </summary>
    public static TwigPaths ForContext(string twigDir, string org, string project, string? startDir = null)
    {
        _ = org;
        _ = project;
        return new TwigPaths(twigDir, Path.Combine(twigDir, "config"), GetCacheDbPath(twigDir), startDir);
    }

    /// <summary>
    /// Builds a <see cref="TwigPaths"/> from a <paramref name="twigDir"/>.
    /// <paramref name="config"/> is retained for signature stability but no
    /// longer selects between layouts — T1 fixes a single per-worktree DB
    /// path at <c>.twig/cache/twig.db</c>.
    /// </summary>
    public static TwigPaths BuildPaths(string twigDir, TwigConfiguration config, string? startDir = null)
    {
        _ = config;
        return new TwigPaths(twigDir, Path.Combine(twigDir, "config"), GetCacheDbPath(twigDir), startDir);
    }

    /// <summary>
    /// Path where the legacy flat database lived before multi-context support.
    /// Retained so legacy-layout archival can find pre-T1 residue.
    /// </summary>
    public static string GetLegacyDbPath(string twigDir) => Path.Combine(twigDir, "twig.db");
}
