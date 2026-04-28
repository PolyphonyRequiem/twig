using System.Text.Json;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Config;

/// <summary>
/// POCO representing the .twig/config JSON file.
/// Supports LoadAsync, SaveAsync, and SetValue for known configuration paths.
/// </summary>
public sealed class TwigConfiguration
{
    public string Organization { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Team { get; set; } = string.Empty;
    public string ProcessTemplate { get; set; } = string.Empty;
    public AuthConfig Auth { get; set; } = new();
    public DefaultsConfig Defaults { get; set; } = new();
    public SeedConfig Seed { get; set; } = new();
    public DisplayConfig Display { get; set; } = new();
    public UserConfig User { get; set; } = new();
    public GitConfig Git { get; set; } = new();
    public WorkspaceConfig Workspace { get; set; } = new();
    public TrackingConfig Tracking { get; set; } = new();
    public AreasConfig Areas { get; set; } = new();

    /// <summary>
    /// Returns the project to use for git/PR API calls.
    /// Falls back to the root <see cref="Project"/> if <see cref="GitConfig.Project"/> is not set.
    /// </summary>
    public string GetGitProject() => !string.IsNullOrWhiteSpace(Git.Project) ? Git.Project : Project;
    public List<TypeAppearanceConfig>? TypeAppearances { get; set; }

    /// <summary>
    /// Loads configuration synchronously from a JSON file. Preferred for CLI bootstrap
    /// where an async context is unavailable. Returns defaults for missing optional properties.
    /// </summary>
    public static TwigConfiguration Load(string path)
    {
        if (!File.Exists(path))
            return new TwigConfiguration();

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new TwigConfigurationException(
                $"Cannot read config file '{path}': permission denied. Check file permissions.", ex);
        }
        catch (IOException ex)
        {
            throw new TwigConfigurationException(
                $"Cannot read config file '{path}': {ex.Message}", ex);
        }

        try
        {
            return JsonSerializer.Deserialize(bytes, TwigJsonContext.Default.TwigConfiguration)
                ?? new TwigConfiguration();
        }
        catch (JsonException ex)
        {
            throw new TwigConfigurationException(
                $"Config file '{path}' contains invalid JSON. Delete the file or fix the syntax. Details: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Loads configuration from a JSON file. Returns defaults for missing optional properties.
    /// </summary>
    public static async Task<TwigConfiguration> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return new TwigConfiguration();
        }

        FileStream stream;
        try
        {
            stream = File.OpenRead(path);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new TwigConfigurationException(
                $"Cannot read config file '{path}': permission denied. Check file permissions.", ex);
        }
        catch (IOException ex)
        {
            throw new TwigConfigurationException(
                $"Cannot read config file '{path}': {ex.Message}", ex);
        }

        try
        {
            await using (stream)
            {
                var config = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.TwigConfiguration, ct);
                return config ?? new TwigConfiguration();
            }
        }
        catch (JsonException ex)
        {
            throw new TwigConfigurationException(
                $"Config file '{path}' contains invalid JSON. Delete the file or fix the syntax. Details: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Saves configuration to a JSON file.
    /// </summary>
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, TwigJsonContext.Default.TwigConfiguration, ct);
    }

    /// <summary>
    /// Sets a configuration value by dot-separated path (e.g., "seed.staleDays", "display.hints").
    /// Returns true if the path was recognized, false otherwise.
    /// Reflection-free — uses a switch on known paths.
    /// </summary>
    public bool SetValue(string dotPath, string value)
    {
        switch (dotPath.ToLowerInvariant())
        {
            case "organization":
                Organization = value;
                return true;
            case "project":
                Project = value;
                return true;
            case "team":
                Team = value;
                return true;
            case "processtemplate":
                ProcessTemplate = value;
                return true;
            case "auth.method":
                var authLower = value.ToLowerInvariant();
                if (authLower is not ("pat" or "azcli"))
                    return false;
                Auth.Method = authLower;
                return true;
            case "defaults.areapath":
                Defaults.AreaPath = value;
                return true;
            case "defaults.areapaths":
                Defaults.AreaPaths = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                return true;
            case "defaults.iterationpath":
                Defaults.IterationPath = value;
                return true;
            case "seed.staledays":
                if (int.TryParse(value, out var days))
                {
                    Seed.StaleDays = days;
                    return true;
                }
                return false;
            case "display.hints":
                if (bool.TryParse(value, out var hints))
                {
                    Display.Hints = hints;
                    return true;
                }
                return false;
            case "display.treedepth":
                if (int.TryParse(value, out var depth))
                {
                    Display.TreeDepth = depth;
                    return true;
                }
                return false;
            case "display.treedepthup":
                if (int.TryParse(value, out var depthUp) && depthUp >= 0)
                {
                    Display.TreeDepthUp = depthUp;
                    return true;
                }
                return false;
            case "display.treedepthdown":
                if (int.TryParse(value, out var depthDown) && depthDown >= 0)
                {
                    Display.TreeDepthDown = depthDown;
                    return true;
                }
                return false;
            case "display.treedepthsideways":
                if (int.TryParse(value, out var depthSideways) && depthSideways >= 0)
                {
                    Display.TreeDepthSideways = depthSideways;
                    return true;
                }
                return false;
            case "display.icons":
                var lower = value.ToLowerInvariant();
                if (lower is "unicode" or "nerd")
                {
                    Display.Icons = lower;
                    return true;
                }
                return false;
            case "display.cachestaleminutes":
                if (int.TryParse(value, out var staleMinutes) && staleMinutes > 0)
                {
                    Display.CacheStaleMinutes = staleMinutes;
                    return true;
                }
                return false;
            case "user.name":
                User.DisplayName = value;
                return true;
            case "user.email":
                User.Email = value;
                return true;
            case "git.branchpattern":
                Git.BranchPattern = value;
                return true;
            case "git.project":
                Git.Project = value;
                return true;
            case "git.repository":
                Git.Repository = value;
                return true;
            case "display.fillratethreshold":
                if (double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var threshold) && threshold >= 0.0 && threshold <= 1.0)
                {
                    Display.FillRateThreshold = threshold;
                    return true;
                }
                return false;
            case "display.maxextracolumns":
                if (int.TryParse(value, out var maxCols) && maxCols >= 0)
                {
                    Display.MaxExtraColumns = maxCols;
                    return true;
                }
                return false;
            case "display.columns.workspace":
                Display.Columns ??= new DisplayColumnsConfig();
                Display.Columns.Workspace = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                return true;
            case "display.columns.sprint":
                Display.Columns ??= new DisplayColumnsConfig();
                Display.Columns.Sprint = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                return true;
            case "workspace.working_level":
                Workspace.WorkingLevel = string.IsNullOrWhiteSpace(value) ? null : value;
                return true;
            case "tracking.cleanuppolicy":
                var policyLower = value.ToLowerInvariant();
                if (policyLower is "none" or "on-complete" or "on-complete-and-past")
                {
                    Tracking.CleanupPolicy = policyLower;
                    return true;
                }
                return false;
            case "defaults.mode":
                var modeLower = value.ToLowerInvariant();
                if (modeLower is not ("sprint" or "workspace"))
                    return false;
                Defaults.Mode = modeLower;
                return true;
            case "defaults.areapathentries":
            case "areas.paths":
                var entries = new List<AreaPathEntry>();
                foreach (var raw in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string pathPart;
                    bool includeChildren;
                    if (raw.EndsWith(":exact", StringComparison.OrdinalIgnoreCase))
                    {
                        pathPart = raw[..^":exact".Length];
                        includeChildren = false;
                    }
                    else
                    {
                        pathPart = raw;
                        includeChildren = true;
                    }

                    var parsed = AreaPath.Parse(pathPart);
                    if (!parsed.IsSuccess)
                        return false;

                    entries.Add(new AreaPathEntry { Path = pathPart, IncludeChildren = includeChildren });
                }
                if (entries.Count == 0)
                    return false;
                Defaults.AreaPathEntries = entries;
                return true;
            case "areas.mode":
                var areaModeLower = value.ToLowerInvariant();
                if (areaModeLower is not ("under" or "exact"))
                    return false;
                Areas.Mode = areaModeLower;
                return true;
            default:
                return false;
        }
    }
}

public sealed class AuthConfig
{
    public string Method { get; set; } = "azcli";
}

public sealed class DefaultsConfig
{
    public string? AreaPath { get; set; }
    public List<string>? AreaPaths { get; set; }
    public List<AreaPathEntry>? AreaPathEntries { get; set; }
    public string? IterationPath { get; set; }

    /// <summary>
    /// Workspace mode: "sprint" (iteration-scoped) or "workspace" (query-scoped).
    /// Defaults to "sprint".
    /// </summary>
    public string Mode { get; set; } = "sprint";

    /// <summary>
    /// Resolves the configured area paths using a 3-tier fallback:
    /// <c>AreaPathEntries</c> (structured) → <c>AreaPaths</c> (list) → <c>AreaPath</c> (single).
    /// Returns <c>null</c> when no area paths are configured.
    /// </summary>
    public IReadOnlyList<(string Path, bool IncludeChildren)>? ResolveAreaPaths()
    {
        if (AreaPathEntries is { Count: > 0 })
            return AreaPathEntries.Select(e => (e.Path, e.IncludeChildren)).ToList();

        var paths = AreaPaths;
        if (paths is null || paths.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(AreaPath))
                paths = [AreaPath];
        }

        return paths is { Count: > 0 }
            ? paths.Select(p => (p, true)).ToList()
            : null;
    }
}

public sealed class AreaPathEntry
{
    public string Path { get; set; } = string.Empty;
    public bool IncludeChildren { get; set; } = true;
    public string SemanticsLabel => IncludeChildren ? "under" : "exact";
}

public sealed class SeedConfig
{
    public int StaleDays { get; set; } = 14;
    public Dictionary<string, string>? DefaultChildType { get; set; }
}

public sealed class DisplayConfig
{
    public bool Hints { get; set; } = true;
    public int TreeDepth { get; set; } = 5;
    public int TreeDepthUp { get; set; } = 2;
    public int TreeDepthDown { get; set; } = 10;
    public int TreeDepthSideways { get; set; } = 1;
    public string Icons { get; set; } = "unicode";
    public int CacheStaleMinutes { get; set; } = 5;
    public int CacheStaleMinutesReadOnly { get; set; } = 15;
    public Dictionary<string, string>? TypeColors { get; set; }
    public DisplayColumnsConfig? Columns { get; set; }
    public double FillRateThreshold { get; set; } = 0.4;
    public int MaxExtraColumns { get; set; } = 3;
}

public sealed class TypeAppearanceConfig
{
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string? IconId { get; set; }
}

/// <summary>
/// Per-view column overrides for dynamic table rendering (EPIC-004).
/// </summary>
public sealed class DisplayColumnsConfig
{
    public List<string>? Workspace { get; set; }
    public List<string>? Sprint { get; set; }
}

public sealed class UserConfig
{
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
}

public sealed class GitConfig
{
    public string BranchPattern { get; set; } = BranchNameTemplate.DefaultPattern;

    public string? Project { get; set; }
    public string? Repository { get; set; }
}

/// <summary>
/// Workspace-level configuration — working level, mode preferences, etc.
/// </summary>
public sealed class WorkspaceConfig
{
    /// <summary>
    /// The work item type name that represents the user's day-to-day working level.
    /// Items above this level in the backlog hierarchy are dimmed in tree views.
    /// Example values: "Task", "Issue", "User Story".
    /// When null or empty, no dimming is applied.
    /// </summary>
    public string? WorkingLevel { get; set; }
}

/// <summary>
/// Configuration for area-path filtering: paths and default match mode.
/// </summary>
public sealed class AreasConfig
{
    /// <summary>
    /// Default match mode for area entries: "under" (include children) or "exact".
    /// </summary>
    public string Mode { get; set; } = "under";
}

/// <summary>
/// Configuration for manual tracking overlay behavior.
/// </summary>
public sealed class TrackingConfig
{
    /// <summary>
    /// Controls automatic cleanup of tracked items.
    /// Valid values: "none", "on-complete", "on-complete-and-past".
    /// </summary>
    public string CleanupPolicy { get; set; } = "none";
}
