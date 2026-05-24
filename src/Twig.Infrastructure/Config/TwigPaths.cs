namespace Twig.Infrastructure.Config;

/// <summary>
/// Paths to the .twig directory and its contents.
/// Supports multi-context directory layout: <c>.twig/{org}/{project}/twig.db</c>.
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

    /// <summary>Path to the context-specific SQLite database.</summary>
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
    /// Derives the DB file path for a given org/project context:
    /// <c>{twigDir}/{sanitized-org}/{sanitized-project}/twig.db</c>.
    /// </summary>
    public static string GetContextDbPath(string twigDir, string org, string project)
    {
        return Path.Combine(twigDir, SanitizePathSegment(org), SanitizePathSegment(project), "twig.db");
    }

    /// <summary>
    /// Creates a <see cref="TwigPaths"/> scoped to a specific org/project context.
    /// </summary>
    public static TwigPaths ForContext(string twigDir, string org, string project, string? startDir = null)
    {
        var configPath = Path.Combine(twigDir, "config");
        var dbPath = GetContextDbPath(twigDir, org, project);
        return new TwigPaths(twigDir, configPath, dbPath, startDir);
    }

    /// <summary>
    /// Builds a <see cref="TwigPaths"/> from a <paramref name="twigDir"/> and
    /// <paramref name="config"/>, selecting the context-scoped layout when
    /// both Organization and Project are configured, or the flat layout otherwise.
    /// Shared by bootstrap (<c>Program.cs</c>) and DI registration
    /// (<see cref="TwigServiceRegistration"/>) so the path-selection logic
    /// lives in exactly one place.
    /// </summary>
    public static TwigPaths BuildPaths(string twigDir, TwigConfiguration config, string? startDir = null)
    {
        return (!string.IsNullOrWhiteSpace(config.Organization) && !string.IsNullOrWhiteSpace(config.Project))
            ? ForContext(twigDir, config.Organization, config.Project, startDir)
            : new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"), startDir);
    }

    /// <summary>
    /// Path where the legacy flat database lived before multi-context support.
    /// </summary>
    public static string GetLegacyDbPath(string twigDir) => Path.Combine(twigDir, "twig.db");
}
