namespace Twig.Infrastructure.Config;

/// <summary>Workspace paths; the current Core locator does not grant store admission.</summary>
public sealed class TwigPaths
{
    public string TwigDir { get; }
    public string StartDir { get; }
    public string ConfigPath { get; }
    public string RepoConfigPath { get; }
    public string RepoRoot { get; }
    public string DbPath { get; }
    public string GlobalDisplayPath { get; }
    public string PolicyPath => Path.Combine(RepoRoot, "twig.policy.json");

    public TwigPaths(string twigDir, string configPath, string dbPath, string? startDir = null)
        : this(twigDir, configPath, dbPath, startDir, null) { }

    internal TwigPaths(string twigDir, string configPath, string dbPath, string? startDir, string? globalDisplayPath)
    {
        TwigDir = twigDir;
        ConfigPath = configPath;
        DbPath = dbPath;
        StartDir = startDir ?? Directory.GetCurrentDirectory();
        RepoRoot = Path.GetDirectoryName(twigDir) ?? twigDir;
        RepoConfigPath = Path.Combine(RepoRoot, "twig.json");
        GlobalDisplayPath = globalDisplayPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".twig", "display.json");
    }

    public static TwigPaths BuildPaths(string twigDir, string? startDir = null)
        => new(twigDir, Path.Combine(twigDir, "config"), Auth.CoreConnectionAdmission.ResolveDatabasePath(twigDir), startDir);
}
