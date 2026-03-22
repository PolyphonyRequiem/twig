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
    public AuthConfig Auth { get; set; } = new();
    public DefaultsConfig Defaults { get; set; } = new();
    public SeedConfig Seed { get; set; } = new();
    public DisplayConfig Display { get; set; } = new();
    public UserConfig User { get; set; } = new();
    public GitConfig Git { get; set; } = new();
    public FlowConfig Flow { get; set; } = new();

    /// <summary>
    /// Returns the project to use for git/PR API calls.
    /// Falls back to the root <see cref="Project"/> if <see cref="GitConfig.Project"/> is not set.
    /// </summary>
    public string GetGitProject() => !string.IsNullOrWhiteSpace(Git.Project) ? Git.Project : Project;
    public List<TypeAppearanceConfig>? TypeAppearances { get; set; }

    /// <summary>
    /// Loads configuration from a JSON file. Returns defaults for missing optional properties.
    /// </summary>
    public static async Task<TwigConfiguration> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return new TwigConfiguration();
        }

        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.TwigConfiguration, ct);
        return config ?? new TwigConfiguration();
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
            case "git.branchtemplate":
                Git.BranchTemplate = value;
                return true;
            case "git.branchpattern":
                Git.BranchPattern = value;
                return true;
            case "git.committemplate":
                Git.CommitTemplate = value;
                return true;
            case "git.defaulttarget":
                Git.DefaultTarget = value;
                return true;
            case "git.autolink":
                if (bool.TryParse(value, out var autoLink))
                {
                    Git.AutoLink = autoLink;
                    return true;
                }
                return false;
            case "git.autotransition":
                if (bool.TryParse(value, out var autoTransition))
                {
                    Git.AutoTransition = autoTransition;
                    return true;
                }
                return false;
            case "git.project":
                Git.Project = value;
                return true;
            case "git.repository":
                Git.Repository = value;
                return true;
            case "git.hooks.preparecommitmsg":
                if (bool.TryParse(value, out var prepareCommitMsg))
                {
                    Git.Hooks.PrepareCommitMsg = prepareCommitMsg;
                    return true;
                }
                return false;
            case "git.hooks.commitmsg":
                if (bool.TryParse(value, out var commitMsg))
                {
                    Git.Hooks.CommitMsg = commitMsg;
                    return true;
                }
                return false;
            case "git.hooks.postcheckout":
                if (bool.TryParse(value, out var postCheckout))
                {
                    Git.Hooks.PostCheckout = postCheckout;
                    return true;
                }
                return false;
            case "flow.autoassign":
                var autoAssignLower = value.ToLowerInvariant();
                if (autoAssignLower is "if-unassigned" or "always" or "never")
                {
                    Flow.AutoAssign = autoAssignLower;
                    return true;
                }
                return false;
            case "flow.autosaveondone":
                if (bool.TryParse(value, out var autoSave))
                {
                    Flow.AutoSaveOnDone = autoSave;
                    return true;
                }
                return false;
            case "flow.offerprondone":
                if (bool.TryParse(value, out var offerPr))
                {
                    Flow.OfferPrOnDone = offerPr;
                    return true;
                }
                return false;
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
}

public sealed class AreaPathEntry
{
    public string Path { get; set; } = string.Empty;
    public bool IncludeChildren { get; set; } = true;
}

public sealed class SeedConfig
{
    public int StaleDays { get; set; } = 14;
    public Dictionary<string, string>? DefaultChildType { get; set; }
}

public sealed class DisplayConfig
{
    public bool Hints { get; set; } = true;
    public int TreeDepth { get; set; } = 10;
    public string Icons { get; set; } = "unicode";
    public int CacheStaleMinutes { get; set; } = 5;
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
    public string BranchTemplate { get; set; } = "feature/{id}-{title}";
    public string BranchPattern { get; set; } = BranchNameTemplate.DefaultPattern;

    /// <summary>
    /// Template for git commit messages used by <c>twig commit</c>.
    /// Tokens: <c>{type}</c> (conventional-commit prefix), <c>#{id}</c> (work item ID),
    /// <c>{message}</c> (user-supplied commit body).
    /// Example: <c>{type}(#{id}): {message}</c>
    /// </summary>
    public string CommitTemplate { get; set; } = "{type}(#{id}): {message}";

    public string DefaultTarget { get; set; } = "main";

    /// <summary>
    /// When true, automatically add ADO artifact links for branches created by
    /// <c>twig branch</c> and commits created by <c>twig commit</c>.
    /// </summary>
    public bool AutoLink { get; set; } = true;

    /// <summary>
    /// When true, automatically transition work item state on branch creation via
    /// <c>twig branch</c> (moves Proposed items to In Progress).
    /// </summary>
    public bool AutoTransition { get; set; } = true;

    /// <summary>
    /// Maps ADO work item types to conventional commit / branch name prefixes used by
    /// <c>twig branch</c> and <c>twig commit</c>.
    /// Overrides the default type map (User Story→feature, Bug→bug, Task→task, etc.).
    /// </summary>
    public Dictionary<string, string>? TypeMap { get; set; }

    /// <summary>
    /// Controls which git hooks are installed by <c>twig hooks install</c>.
    /// Each boolean toggles installation of the corresponding hook script.
    /// </summary>
    public HooksConfig Hooks { get; set; } = new();

    public string? Project { get; set; }
    public string? Repository { get; set; }
}

public sealed class HooksConfig
{
    public bool PrepareCommitMsg { get; set; } = true;
    public bool CommitMsg { get; set; } = true;
    public bool PostCheckout { get; set; } = true;
}

public sealed class FlowConfig
{
    public string AutoAssign { get; set; } = "if-unassigned";
    public bool AutoSaveOnDone { get; set; } = true;
    public bool OfferPrOnDone { get; set; } = true;
}
