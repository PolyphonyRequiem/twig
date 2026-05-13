using System.Collections.ObjectModel;
using Twig.Infrastructure.Config;

namespace Twig.Mcp.Services;

/// <summary>
/// Read-only view of discovered workspaces. Extracted for testability of <see cref="WorkspaceResolver"/>.
/// </summary>
public interface IWorkspaceRegistry
{
    /// <summary>All discovered workspace keys.</summary>
    IReadOnlyList<WorkspaceKey> Workspaces { get; }

    /// <summary>True when exactly one workspace is registered — enables backward-compat fast-path.</summary>
    bool IsSingleWorkspace { get; }
}

/// <summary>
/// Discovers available workspaces by scanning <c>.twig/{org}/{project}/config</c> on disk.
/// Immutable after construction — scanned once at startup (DD-5).
/// </summary>
public sealed class WorkspaceRegistry : IWorkspaceRegistry
{
    private readonly ReadOnlyDictionary<WorkspaceKey, TwigConfiguration> _workspaces;
    private readonly string _twigRoot;
    private readonly string? _launchCwd;

    /// <summary>
    /// All discovered workspace keys.
    /// </summary>
    public IReadOnlyList<WorkspaceKey> Workspaces { get; }

    /// <summary>
    /// True when exactly one workspace is registered — enables backward-compat fast-path.
    /// </summary>
    public bool IsSingleWorkspace => Workspaces.Count == 1;

    /// <summary>
    /// Gets the <see cref="TwigConfiguration"/> for a given workspace.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when <paramref name="key"/> is not a registered workspace. The message
    /// distinguishes "no workspaces discovered at all" (likely a launch-cwd problem)
    /// from "workspace not in the discovered set" so operators can self-diagnose.
    /// </exception>
    public TwigConfiguration GetConfig(WorkspaceKey key)
    {
        if (_workspaces.TryGetValue(key, out var config))
            return config;

        if (Workspaces.Count == 0)
        {
            var cwdHint = _launchCwd is null ? "" : $" (server launched from '{_launchCwd}')";

            if (!Directory.Exists(_twigRoot))
            {
                throw new KeyNotFoundException(
                    $"Workspace '{key}' is not registered. No twig workspaces were discovered: " +
                    $"no '.twig/' directory found above the MCP server's launch directory{cwdHint}. " +
                    $"The server discovers workspaces at startup by walking upward from its working " +
                    $"directory; sibling and child directories are not scanned. Restart the server " +
                    $"from inside a twig project (a directory with a '.twig/' ancestor), or run " +
                    $"'twig init' in your project root first.");
            }

            throw new KeyNotFoundException(
                $"Workspace '{key}' is not registered. Found '.twig/' at '{_twigRoot}' but it " +
                $"contains no valid workspace configs ('.twig/{{org}}/{{project}}/config'). " +
                $"Run 'twig init' to register a workspace.");
        }

        throw new KeyNotFoundException(
            $"Workspace '{key}' is not registered. Available (discovered under '{_twigRoot}'): " +
            $"{string.Join(", ", Workspaces)}.");
    }

    /// <summary>
    /// Creates a registry by scanning the given <c>.twig/</c> directory for workspace configs.
    /// </summary>
    /// <param name="twigRoot">
    /// The <c>.twig/</c> directory to scan (e.g., <c>/repo/.twig</c>).
    /// </param>
    /// <param name="launchCwd">
    /// Optional context: the directory the MCP server was launched from, used to produce a
    /// self-diagnosing error when no <c>.twig/</c> directory was discovered.
    /// </param>
    public WorkspaceRegistry(string twigRoot, string? launchCwd = null)
    {
        _twigRoot = twigRoot;
        _launchCwd = launchCwd;
        var discovered = new Dictionary<WorkspaceKey, TwigConfiguration>();

        if (Directory.Exists(twigRoot))
        {
            // Multi-workspace discovery: .twig/{org}/{project}/config
            foreach (var orgDir in GetDirectories(twigRoot))
            {
                foreach (var projectDir in GetDirectories(orgDir))
                {
                    TryRegister(projectDir, discovered);
                }
            }

            // Legacy fallback: .twig/config (top-level config, single workspace)
            if (discovered.Count == 0)
            {
                TryRegister(twigRoot, discovered);
            }
        }

        _workspaces = new ReadOnlyDictionary<WorkspaceKey, TwigConfiguration>(discovered);
        Workspaces = discovered.Keys.ToList().AsReadOnly();
    }

    private static void TryRegister(string directory, Dictionary<WorkspaceKey, TwigConfiguration> discovered)
    {
        var configPath = Path.Combine(directory, "config");
        if (!File.Exists(configPath))
            return;

        TwigConfiguration config;
        try
        {
            config = TwigConfiguration.Load(configPath);
        }
        catch (TwigConfigurationException)
        {
            // Skip configs that can't be loaded (corrupted JSON, permission issues)
            return;
        }

        if (string.IsNullOrWhiteSpace(config.Organization) || string.IsNullOrWhiteSpace(config.Project))
            return;

        var key = new WorkspaceKey(config.Organization, config.Project);

        // First registration wins — don't overwrite if duplicate
        discovered.TryAdd(key, config);
    }

    private static string[] GetDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
