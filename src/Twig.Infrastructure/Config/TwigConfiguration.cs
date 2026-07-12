using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Config;

/// <summary>
/// POCO representing the .twig/config JSON file.
/// <para>
/// As of AB#3296 PR-1, internally composed of two sub-configs
/// (<see cref="TwigRepoConfig"/> and <see cref="TwigUserConfig"/>) that
/// establish a partition between repo coordinates (committed) and user
/// preferences (gitignored). PR-1 only introduces the partition —
/// serialization still produces the same flat single-file shape on disk.
/// PR-2 splits the on-disk shape into <c>twig.json</c> + <c>.twig/config</c>.
/// </para>
/// <para>
/// Every public top-level property here delegates to one of the sub-configs.
/// The sub-configs themselves are <see cref="JsonIgnoreAttribute"/>'d so
/// source-gen emits the same JSON shape as before the refactor.
/// </para>
/// Supports LoadAsync, SaveAsync, and SetValue for known configuration paths.
/// </summary>
public sealed class TwigConfiguration
{
    /// <summary>
    /// Repo-scoped coordinates that every contributor needs. Internal partition
    /// container for AB#3296. Not serialized in PR-1 (the on-disk shape stays a
    /// single flat file); PR-2 will serialize this independently to a
    /// committed <c>twig.json</c> at the repo root.
    /// </summary>
    [JsonIgnore]
    public TwigRepoConfig RepoCoords { get; set; } = new();

    /// <summary>
    /// Per-user preferences that vary by machine. Internal partition container
    /// for AB#3296. Not serialized in PR-1. See <see cref="RepoCoords"/>.
    /// </summary>
    [JsonIgnore]
    public TwigUserConfig UserPrefs { get; set; } = new();

    /// <summary>
    /// AB#3296: <c>true</c> when this configuration was loaded from a single
    /// legacy <c>.twig/config</c> file (no <c>twig.json</c> manifest present).
    /// Legacy mode preserves the original write behavior — <see cref="SaveAsync(TwigPaths, CancellationToken)"/>
    /// writes back to the single file so polyphony worktrees and other un-migrated
    /// repos keep working until an explicit <c>twig migrate-config</c> runs.
    /// </summary>
    [JsonIgnore]
    public bool IsLegacyMode { get; internal set; }

    public string Organization { get => RepoCoords.Organization; set => RepoCoords.Organization = value; }
    public string Project { get => RepoCoords.Project; set => RepoCoords.Project = value; }
    public string Team { get => RepoCoords.Team; set => RepoCoords.Team = value; }
    public string ProcessTemplate { get => RepoCoords.ProcessTemplate; set => RepoCoords.ProcessTemplate = value; }
    public AuthConfig Auth { get => UserPrefs.Auth; set => UserPrefs.Auth = value; }
    public DefaultsConfig Defaults { get => RepoCoords.Defaults; set => RepoCoords.Defaults = value; }
    public SeedConfig Seed { get => RepoCoords.Seed; set => RepoCoords.Seed = value; }
    public DisplayConfig Display { get => UserPrefs.Display; set => UserPrefs.Display = value; }
    public UserConfig User { get => UserPrefs.User; set => UserPrefs.User = value; }
    public GitConfig Git { get => RepoCoords.Git; set => RepoCoords.Git = value; }
    public WorkspaceConfig Workspace { get => RepoCoords.Workspace; set => RepoCoords.Workspace = value; }
    public TrackingConfig Tracking { get => UserPrefs.Tracking; set => UserPrefs.Tracking = value; }
    public AreasConfig Areas { get => RepoCoords.Areas; set => RepoCoords.Areas = value; }

    /// <summary>
    /// Returns the project to use for git/PR API calls.
    /// Falls back to the root <see cref="Project"/> if <see cref="GitConfig.Project"/> is not set.
    /// </summary>
    public string GetGitProject() => !string.IsNullOrWhiteSpace(Git.Project) ? Git.Project : Project;
    /// <summary>
    /// AB#3296 PR-3: hydrated at bootstrap from the SQLite <c>process_types</c>
    /// cache; never serialized to disk. See <see cref="TwigUserConfig.TypeAppearances"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<TypeAppearanceConfig>? TypeAppearances { get => UserPrefs.TypeAppearances; set => UserPrefs.TypeAppearances = value; }

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
    /// <para>
    /// Short-circuits to a no-op when the serialized bytes are byte-identical to the
    /// existing file. This keeps <c>.twig/config</c> stable across read-style paths that
    /// nevertheless flow through a save (notably <c>twig sync</c>'s refresh, which
    /// re-fetches <see cref="TypeAppearances"/> and rewrites them even when nothing
    /// substantive changed). Without this guard, repeated syncs leave a committed file
    /// dirty and break any tooling that asserts a clean working tree (ADO #3237).
    /// </para>
    /// <para>
    /// Identity is canonical-byte identity: a file with hand-formatting, BOM, CRLF, or
    /// stale property ordering will still be rewritten once into the source-gen
    /// canonical UTF-8/no-BOM/compact form, and stabilize after that single write.
    /// </para>
    /// </summary>
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(buffer, this, TwigJsonContext.Default.TwigConfiguration, ct);
        var newBytes = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);

        if (File.Exists(path))
        {
            try
            {
                var existing = await File.ReadAllBytesAsync(path, ct);
                if (existing.AsSpan().SequenceEqual(newBytes.Span))
                    return;
            }
            catch (IOException)
            {
                // Existing file unreadable — fall through and overwrite.
            }
        }

        await File.WriteAllBytesAsync(path, newBytes.ToArray(), ct);
    }

    /// <summary>
    /// AB#3296: split-aware loader. Reads the committed <c>twig.json</c> manifest
    /// (repo coords) and the gitignored <c>.twig/config</c> (user prefs) as separate
    /// files when the manifest exists. Falls back to the legacy single-file load
    /// when only <c>.twig/config</c> is present, setting <see cref="IsLegacyMode"/>
    /// so subsequent saves preserve the un-migrated shape.
    /// </summary>
    public static async Task<TwigConfiguration> LoadSplitAsync(TwigPaths paths, CancellationToken ct = default)
    {
        if (File.Exists(paths.RepoConfigPath))
        {
            var repo = await LoadJsonAsync(paths.RepoConfigPath, TwigJsonContext.Default.TwigRepoConfig, ct)
                ?? new TwigRepoConfig();
            var user = File.Exists(paths.ConfigPath)
                ? await LoadJsonAsync(paths.ConfigPath, TwigJsonContext.Default.TwigUserConfig, ct) ?? new TwigUserConfig()
                : new TwigUserConfig();
            return new TwigConfiguration
            {
                RepoCoords = repo,
                UserPrefs = user,
                IsLegacyMode = false,
            };
        }

        // Legacy single-file shape — load via the existing flat path. The delegating
        // accessors route each top-level property into the right container, so
        // RepoCoords and UserPrefs both end up populated.
        var legacy = await LoadAsync(paths.ConfigPath, ct);
        legacy.IsLegacyMode = File.Exists(paths.ConfigPath);
        return legacy;
    }

    /// <summary>
    /// AB#3296: synchronous split-aware loader. Mirrors <see cref="LoadSplitAsync"/>
    /// for CLI bootstrap paths that run before any async context exists.
    /// </summary>
    public static TwigConfiguration LoadSplit(TwigPaths paths)
    {
        if (File.Exists(paths.RepoConfigPath))
        {
            var repo = LoadJson(paths.RepoConfigPath, TwigJsonContext.Default.TwigRepoConfig) ?? new TwigRepoConfig();
            var user = File.Exists(paths.ConfigPath)
                ? LoadJson(paths.ConfigPath, TwigJsonContext.Default.TwigUserConfig) ?? new TwigUserConfig()
                : new TwigUserConfig();
            return new TwigConfiguration
            {
                RepoCoords = repo,
                UserPrefs = user,
                IsLegacyMode = false,
            };
        }

        var legacy = Load(paths.ConfigPath);
        legacy.IsLegacyMode = File.Exists(paths.ConfigPath);
        return legacy;
    }

    private static async Task<T?> LoadJsonAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
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
                return await JsonSerializer.DeserializeAsync(stream, typeInfo, ct);
            }
        }
        catch (JsonException ex)
        {
            throw new TwigConfigurationException(
                $"Config file '{path}' contains invalid JSON. Delete the file or fix the syntax. Details: {ex.Message}", ex);
        }
    }

    private static T? LoadJson<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
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
            return JsonSerializer.Deserialize(bytes, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new TwigConfigurationException(
                $"Config file '{path}' contains invalid JSON. Delete the file or fix the syntax. Details: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// AB#3296: writes only the repo-coords portion to <paramref name="repoPath"/>
    /// using the indented JSON context (human-reviewable). Byte-identity
    /// short-circuit, same as <see cref="SaveAsync(string, CancellationToken)"/>.
    /// </summary>
    public async Task SaveRepoAsync(string repoPath, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(repoPath);
        if (directory is not null && directory.Length > 0 && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var newBytes = await GetRepoBytesAsync(ct);

        if (File.Exists(repoPath))
        {
            try
            {
                var existing = await File.ReadAllBytesAsync(repoPath, ct);
                if (existing.AsSpan().SequenceEqual(newBytes))
                    return;
            }
            catch (IOException)
            {
                // fall through and overwrite
            }
        }

        await File.WriteAllBytesAsync(repoPath, newBytes, ct);
    }

    internal async Task<byte[]> GetRepoBytesAsync(CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(buffer, RepoCoords, TwigRepoJsonContext.Default.TwigRepoConfig, ct);
        return buffer.ToArray();
    }

    /// <summary>
    /// AB#3296: writes only the user-prefs portion to <paramref name="userPath"/>
    /// using the compact JSON context. This is the write that <c>twig sync</c> /
    /// <c>twig refresh</c> are constrained to — the load-bearing invariant is
    /// "sync never modifies tracked files."
    /// </summary>
    public async Task SaveUserAsync(string userPath, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(userPath);
        if (directory is not null && directory.Length > 0 && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var newBytes = await GetUserBytesAsync(ct);

        if (File.Exists(userPath))
        {
            try
            {
                var existing = await File.ReadAllBytesAsync(userPath, ct);
                if (existing.AsSpan().SequenceEqual(newBytes))
                    return;
            }
            catch (IOException)
            {
                // fall through and overwrite
            }
        }

        await File.WriteAllBytesAsync(userPath, newBytes, ct);
    }

    internal async Task<byte[]> GetUserBytesAsync(CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(buffer, UserPrefs, TwigJsonContext.Default.TwigUserConfig, ct);
        return buffer.ToArray();
    }

    /// <summary>
    /// AB#3296: dispatcher. Writes the split shape (<c>twig.json</c> + <c>.twig/config</c>)
    /// when not in legacy mode; falls back to the single-file legacy shape when
    /// <see cref="IsLegacyMode"/> is true so un-migrated repos keep working unchanged.
    /// </summary>
    public Task SaveSplitAsync(TwigPaths paths, CancellationToken ct = default)
    {
        if (IsLegacyMode)
        {
            return SaveAsync(paths.ConfigPath, ct);
        }

        return SaveBothAsync(paths, ct);
    }

    private async Task SaveBothAsync(TwigPaths paths, CancellationToken ct)
    {
        await SaveRepoAsync(paths.RepoConfigPath, ct);
        await SaveUserAsync(paths.ConfigPath, ct);
    }

    /// <summary>
    /// AB#3296: returns the scope (<see cref="ConfigScope.Repo"/> or
    /// <see cref="ConfigScope.User"/>) that owns the given dot-path. Returns
    /// <see cref="ConfigScope.Unknown"/> for unrecognized keys so callers can
    /// emit a descriptive error. Single source of truth for write-routing
    /// decisions, mirrored by <see cref="SetValue"/>.
    /// </summary>
    public static ConfigScope GetConfigScope(string dotPath)
    {
        return dotPath.ToLowerInvariant() switch
        {
            "organization" or "project" or "team" or "processtemplate" => ConfigScope.Repo,
            "defaults.areapath" or "defaults.areapaths" or "defaults.iterationpath"
                or "defaults.mode" or "defaults.areapathentries"
                or "defaults.inheritparentarea" or "defaults.inheritparentiteration" => ConfigScope.Repo,
            "areas.mode" or "areas.paths" => ConfigScope.Repo,
            "seed.staledays" => ConfigScope.Repo,
            "git.branchpattern" or "git.project" or "git.repository" => ConfigScope.Repo,
            "workspace.workinglevel" or "workspace.sprints" => ConfigScope.Repo,
            "auth.method" => ConfigScope.User,
            "user.displayname" or "user.email" => ConfigScope.User,
            "display.hints" or "display.treedepth" or "display.treedepthup"
                or "display.treedepthdown" or "display.treedepthsideways"
                or "display.icons" or "display.cachestaleminutes"
                or "display.cachestaleminutesreadonly"
                or "display.fillratethreshold" or "display.maxextracolumns"
                or "display.columns.workspace" or "display.columns.sprint" => ConfigScope.User,
            "tracking.cleanuppolicy" or "tracking.mode" => ConfigScope.User,
            _ => ConfigScope.Unknown,
        };
    }

    /// <summary>
    /// Gets a configuration value by dot-separated path (e.g., "seed.staleDays", "display.hints").
    /// Returns <c>(value, true)</c> if the path was recognized, <c>(null, false)</c> otherwise.
    /// Reflection-free — uses a switch on known paths, mirroring <see cref="SetValue"/>.
    /// </summary>
    public (string? Value, bool Found) GetValue(string dotPath)
    {
        switch (dotPath.ToLowerInvariant())
        {
            case "organization":
                return (Organization, true);
            case "project":
                return (Project, true);
            case "team":
                return (Team, true);
            case "processtemplate":
                return (ProcessTemplate, true);
            case "auth.method":
                return (Auth.Method, true);
            case "defaults.areapath":
                return (Defaults.AreaPath ?? "", true);
            case "defaults.areapaths":
                return (Defaults.AreaPaths is { Count: > 0 }
                    ? string.Join(";", Defaults.AreaPaths)
                    : "", true);
            case "defaults.iterationpath":
                return (Defaults.IterationPath ?? "", true);
            case "defaults.mode":
                return (Defaults.Mode, true);
            case "defaults.inheritparentarea":
                return (Defaults.InheritParentArea.ToString().ToLowerInvariant(), true);
            case "defaults.inheritparentiteration":
                return (Defaults.InheritParentIteration.ToString().ToLowerInvariant(), true);
            case "defaults.areapathentries":
            case "areas.paths":
                return (Defaults.AreaPathEntries is { Count: > 0 }
                    ? string.Join(";", Defaults.AreaPathEntries.Select(e =>
                        e.IncludeChildren ? e.Path : $"{e.Path}:exact"))
                    : "", true);
            case "seed.staledays":
                return (Seed.StaleDays.ToString(), true);
            case "display.hints":
                return (Display.Hints.ToString().ToLowerInvariant(), true);
            case "display.treedepth":
                return (Display.TreeDepth.ToString(), true);
            case "display.treedepthup":
                return (Display.TreeDepthUp.ToString(), true);
            case "display.treedepthdown":
                return (Display.TreeDepthDown.ToString(), true);
            case "display.treedepthsideways":
                return (Display.TreeDepthSideways.ToString(), true);
            case "display.icons":
                return (Display.Icons, true);
            case "display.cachestaleminutes":
                return (Display.CacheStaleMinutes.ToString(), true);
            case "display.fillratethreshold":
                return (Display.FillRateThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture), true);
            case "display.maxextracolumns":
                return (Display.MaxExtraColumns.ToString(), true);
            case "display.columns.workspace":
                return (Display.Columns?.Workspace is { Count: > 0 }
                    ? string.Join(";", Display.Columns.Workspace)
                    : "", true);
            case "display.columns.sprint":
                return (Display.Columns?.Sprint is { Count: > 0 }
                    ? string.Join(";", Display.Columns.Sprint)
                    : "", true);
            case "user.name":
                return (User.DisplayName ?? "", true);
            case "user.email":
                return (User.Email ?? "", true);
            case "git.branchpattern":
                return (Git.BranchPattern, true);
            case "git.project":
                return (Git.Project ?? "", true);
            case "git.repository":
                return (Git.Repository ?? "", true);
            case "workspace.working_level":
                return (Workspace.WorkingLevel ?? "", true);
            case "workspace.sprints":
                return (Workspace.Sprints is { Count: > 0 }
                    ? string.Join(";", Workspace.Sprints.Select(s => s.Expression))
                    : "", true);
            case "tracking.cleanuppolicy":
                return (Tracking.CleanupPolicy, true);
            case "areas.mode":
                return (Areas.EffectiveMode, true);
            default:
                return (null, false);
        }
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
            case "workspace.sprints":
                var expressions = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (expressions.Length == 0)
                    return false;
                Workspace.Sprints = expressions.Select(e => new SprintEntry { Expression = e }).ToList();
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
            case "defaults.inheritparentarea":
                if (bool.TryParse(value, out var inheritArea))
                {
                    Defaults.InheritParentArea = inheritArea;
                    return true;
                }
                return false;
            case "defaults.inheritparentiteration":
                if (bool.TryParse(value, out var inheritIteration))
                {
                    Defaults.InheritParentIteration = inheritIteration;
                    return true;
                }
                return false;
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

/// <summary>
/// AB#3296: where a configuration key lives once the split is in effect.
/// Used by <see cref="TwigConfiguration.GetConfigScope"/> as a single source
/// of truth for write-routing decisions (which file a key writes to).
/// </summary>
public enum ConfigScope
{
    /// <summary>Key is unknown to twig. Caller should emit a descriptive error.</summary>
    Unknown,
    /// <summary>Key lives in the committed <c>twig.json</c> manifest (repo coords).</summary>
    Repo,
    /// <summary>Key lives in the gitignored <c>.twig/config</c> file (per-user preferences).</summary>
    User,
}

public sealed class AuthConfig
{
    public string Method { get; set; } = "azcli";
}

/// <summary>
/// Repo-scoped configuration: coordinates that every contributor needs to talk
/// to the same Azure DevOps project. As of AB#3296 PR-1 this is an internal
/// partition target; PR-2 will serialize this independently to a committed
/// <c>twig.json</c> at the repo root.
/// </summary>
public sealed class TwigRepoConfig
{
    public string Organization { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Team { get; set; } = string.Empty;
    public string ProcessTemplate { get; set; } = string.Empty;
    public DefaultsConfig Defaults { get; set; } = new();
    public SeedConfig Seed { get; set; } = new();
    public GitConfig Git { get; set; } = new();
    public WorkspaceConfig Workspace { get; set; } = new();
    public AreasConfig Areas { get; set; } = new();
}

/// <summary>
/// Per-user configuration: preferences that vary by machine and should never
/// produce a git diff. As of AB#3296 PR-1 this is an internal partition target;
/// PR-2 will serialize this independently to a gitignored <c>.twig/config</c>.
/// </summary>
public sealed class TwigUserConfig
{
    public AuthConfig Auth { get; set; } = new();
    public DisplayConfig Display { get; set; } = new();
    public UserConfig User { get; set; } = new();
    public TrackingConfig Tracking { get; set; } = new();

    /// <summary>
    /// AB#3296 PR-3: this field is hydrated at bootstrap from the SQLite
    /// <c>process_types</c> cache (see <c>SqliteProcessTypeStore</c>) and is
    /// never serialized to disk. The data is already cached per-context in
    /// the SQLite store; keeping it out of the user-prefs file eliminates the
    /// 60-line JSON array that <c>twig sync</c> used to rewrite on every
    /// invocation. <c>migrate-config</c> drops this field when rewriting a
    /// legacy <c>.twig/config</c>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<TypeAppearanceConfig>? TypeAppearances { get; set; }
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
    /// When <c>true</c> (the default), <c>twig new --parent &lt;id&gt;</c> fetches the parent
    /// work item and uses its <c>System.AreaPath</c> as the default for the new child,
    /// taking priority over <see cref="AreaPath"/>. Set to <c>false</c> to restore the
    /// pre-AB#3242 behavior where <c>--area</c> falls straight through to <see cref="AreaPath"/>.
    /// Has no effect when <c>--parent</c> is not given or <c>--area</c> is explicit.
    /// </summary>
    public bool InheritParentArea { get; set; } = true;

    /// <summary>
    /// When <c>true</c> (the default), <c>twig new --parent &lt;id&gt;</c> fetches the parent
    /// work item and uses its <c>System.IterationPath</c> as the default for the new child,
    /// taking priority over <see cref="IterationPath"/>. Set to <c>false</c> to restore the
    /// pre-AB#3242 behavior. Has no effect when <c>--parent</c> is not given or
    /// <c>--iteration</c> is explicit.
    /// </summary>
    public bool InheritParentIteration { get; set; } = true;

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

    /// <summary>
    /// Human-readable label derived from <see cref="IncludeChildren"/>. Computed only;
    /// never serialized (no setter, so a deserialized round-trip would lose it anyway —
    /// emitting it just creates pointless on-disk churn). See ADO #3237.
    /// </summary>
    [JsonIgnore]
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

    /// <summary>
    /// Configured sprint iteration expressions (e.g., "@current", "@current-1", "Project\Sprint 5").
    /// Each entry represents a subscribed sprint iteration that twig tracks.
    /// </summary>
    public List<SprintEntry>? Sprints { get; set; }
}

/// <summary>
/// A single sprint iteration expression stored in workspace configuration.
/// Expressions can be relative (@current, @current±N) or absolute iteration paths.
/// </summary>
public sealed class SprintEntry
{
    /// <summary>
    /// The iteration expression string (e.g., "@current", "@current-1", "Project\Sprint 5").
    /// </summary>
    public string Expression { get; set; } = string.Empty;
}

/// <summary>
/// Configuration for area-path filtering: paths and default match mode.
/// </summary>
public sealed class AreasConfig
{
    /// <summary>
    /// Default match mode for area entries: "under" (include children) or "exact".
    /// <para>
    /// <c>null</c> represents "no explicit configuration" and is omitted from serialized
    /// output (per the <see cref="JsonIgnoreCondition.WhenWritingNull"/> default on
    /// <see cref="TwigJsonContext"/>). Use <see cref="EffectiveMode"/> when consuming
    /// the value — it returns <c>"under"</c> as the implicit default. See ADO #3237.
    /// </para>
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>
    /// The configured mode, or <c>"under"</c> when none is explicitly set.
    /// </summary>
    [JsonIgnore]
    public string EffectiveMode => Mode ?? "under";
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
