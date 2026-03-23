namespace Twig.Infrastructure.Config;

/// <summary>
/// Paths to the .twig directory and its contents.
/// Supports multi-context directory layout: <c>.twig/{org}/{project}/twig.db</c>.
/// </summary>
public sealed class TwigPaths
{
    /// <summary>Root .twig directory (e.g., <c>/repo/.twig</c>).</summary>
    public string TwigDir { get; }

    /// <summary>Path to the config file: <c>.twig/config</c>.</summary>
    public string ConfigPath { get; }

    /// <summary>Path to the context-specific SQLite database.</summary>
    public string DbPath { get; }

    /// <summary>Path to the status-fields configuration file: <c>.twig/status-fields</c>.</summary>
    public string StatusFieldsPath => Path.Combine(TwigDir, "status-fields");

    public TwigPaths(string twigDir, string configPath, string dbPath)
    {
        TwigDir = twigDir;
        ConfigPath = configPath;
        DbPath = dbPath;
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
    public static TwigPaths ForContext(string twigDir, string org, string project)
    {
        var configPath = Path.Combine(twigDir, "config");
        var dbPath = GetContextDbPath(twigDir, org, project);
        return new TwigPaths(twigDir, configPath, dbPath);
    }

    /// <summary>
    /// Builds a <see cref="TwigPaths"/> from a <paramref name="twigDir"/> and
    /// <paramref name="config"/>, selecting the context-scoped layout when
    /// both Organization and Project are configured, or the flat layout otherwise.
    /// Shared by bootstrap (<c>Program.cs</c>) and DI registration
    /// (<see cref="TwigServiceRegistration"/>) so the path-selection logic
    /// lives in exactly one place.
    /// </summary>
    public static TwigPaths BuildPaths(string twigDir, TwigConfiguration config)
    {
        return (!string.IsNullOrWhiteSpace(config.Organization) && !string.IsNullOrWhiteSpace(config.Project))
            ? ForContext(twigDir, config.Organization, config.Project)
            : new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));
    }

    /// <summary>
    /// Path where the legacy flat database lived before multi-context support.
    /// </summary>
    public static string GetLegacyDbPath(string twigDir) => Path.Combine(twigDir, "twig.db");
}
