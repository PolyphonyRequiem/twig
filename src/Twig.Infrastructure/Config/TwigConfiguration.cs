using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Config;

/// <summary>Repository coordinates and local preferences, persisted independently.</summary>
public sealed class TwigConfiguration
{
    public TwigRepoConfig RepoCoords { get; set; } = new();
    public TwigUserConfig UserPrefs { get; set; } = new();

    public string Organization { get => RepoCoords.Organization; set => RepoCoords.Organization = value; }
    public string Project { get => RepoCoords.Project; set => RepoCoords.Project = value; }
    public string Team { get => RepoCoords.Team; set => RepoCoords.Team = value; }
    public string? Provider { get => RepoCoords.Provider; set => RepoCoords.Provider = value; }
    public GitHubConfig? GitHub { get => RepoCoords.GitHub; set => RepoCoords.GitHub = value; }
    public string? GitHubAccount { get => UserPrefs.GitHubAccount; set => UserPrefs.GitHubAccount = value; }
    public string? GitHubCredentialSource { get => UserPrefs.GitHubCredentialSource; set => UserPrefs.GitHubCredentialSource = value; }
    public string? GitHubAppClientId { get => UserPrefs.GitHubAppClientId; set => UserPrefs.GitHubAppClientId = value; }
    public string? GitHubOAuthIdentity { get => UserPrefs.GitHubOAuthIdentity; set => UserPrefs.GitHubOAuthIdentity = value; }
    public DefaultsConfig Defaults { get => RepoCoords.Defaults; set => RepoCoords.Defaults = value; }
    public DisplayConfig Display { get => UserPrefs.Display; set => UserPrefs.Display = value; }
    public GitConfig Git { get => RepoCoords.Git; set => RepoCoords.Git = value; }

    public string ResolveWorkItemProviderKind() => WorkItemProviderSetting.Resolve(Provider);

    public string GetGitProject() => !string.IsNullOrWhiteSpace(Git.Project) ? Git.Project : Project;

    public static string ResolveTeam(string project, string? team)
        => string.IsNullOrWhiteSpace(team) ? $"{project} Team" : team;

    public static async Task<TwigConfiguration> LoadSplitAsync(TwigPaths paths, CancellationToken ct = default)
    {
        var repo = File.Exists(paths.RepoConfigPath)
            ? await LoadJsonAsync(paths.RepoConfigPath, TwigJsonContext.Default.TwigRepoConfig, ct).ConfigureAwait(false)
                ?? new TwigRepoConfig()
            : new TwigRepoConfig();
        var user = File.Exists(paths.ConfigPath)
            ? await LoadJsonAsync(paths.ConfigPath, TwigJsonContext.Default.TwigUserConfig, ct).ConfigureAwait(false)
                ?? new TwigUserConfig()
            : new TwigUserConfig();
        return WithGlobalDisplay(new() { RepoCoords = repo, UserPrefs = user }, paths);
    }

    public static TwigConfiguration LoadSplit(TwigPaths paths)
    {
        var repo = File.Exists(paths.RepoConfigPath)
            ? LoadJson(paths.RepoConfigPath, TwigJsonContext.Default.TwigRepoConfig) ?? new TwigRepoConfig()
            : new TwigRepoConfig();
        var user = File.Exists(paths.ConfigPath)
            ? LoadJson(paths.ConfigPath, TwigJsonContext.Default.TwigUserConfig) ?? new TwigUserConfig()
            : new TwigUserConfig();
        return WithGlobalDisplay(new() { RepoCoords = repo, UserPrefs = user }, paths);
    }

    private static TwigConfiguration WithGlobalDisplay(TwigConfiguration config, TwigPaths paths)
    {
        var preference = GlobalDisplayPreferences.Load(paths.GlobalDisplayPath).Icons;
        if (preference is not null)
        {
            var normalized = DisplayConfig.NormalizeIcons(preference)
                ?? throw new TwigConfigurationException(
                    $"Invalid display.icons value in '{paths.GlobalDisplayPath}': '{preference}'.",
                    new InvalidDataException("Expected auto, unicode, or nerd."));
            config.Display.UseGlobalIcons(normalized);
        }
        return config;
    }

    private static async Task<T?> LoadJsonAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new TwigConfigurationException($"Cannot read config file '{path}': {ex.Message}", ex);
        }
    }

    private static T? LoadJson<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new TwigConfigurationException($"Cannot read config file '{path}': {ex.Message}", ex);
        }
    }

    public async Task SaveRepoAsync(string repoPath, CancellationToken ct = default)
        => await WriteIfChangedAsync(repoPath, await GetRepoBytesAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    internal async Task<byte[]> GetRepoBytesAsync(CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(buffer, RepoCoords, TwigRepoJsonContext.Default.TwigRepoConfig, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public async Task SaveUserAsync(string userPath, CancellationToken ct = default)
    {
        var bytes = await GetUserBytesAsync(ct).ConfigureAwait(false);
        if (bytes is null)
        {
            if (File.Exists(userPath)) File.Delete(userPath);
            return;
        }
        await WriteIfChangedAsync(userPath, bytes, ct).ConfigureAwait(false);
    }

    internal async Task<byte[]?> GetUserBytesAsync(CancellationToken ct = default)
    {
        if (!UserPrefs.HasOverrides) return null;
        using var buffer = new MemoryStream();
        await JsonSerializer.SerializeAsync(buffer, UserPrefs, TwigJsonContext.Default.TwigUserConfig, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task WriteIfChangedAsync(string path, byte[] bytes, CancellationToken ct)
    {
        if (File.Exists(path) && (await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
            return;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }

    public async Task SaveSplitAsync(TwigPaths paths, CancellationToken ct = default)
    {
        await SaveRepoAsync(paths.RepoConfigPath, ct).ConfigureAwait(false);
        await SaveUserAsync(paths.ConfigPath, ct).ConfigureAwait(false);
    }
}

/// <summary>Portable coordinates and query defaults in the committed twig.json.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TwigRepoConfig
{
    public string Organization { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Team { get; set; } = string.Empty;
    public string? Provider { get; set; }
    [JsonPropertyName("github")]
    public GitHubConfig? GitHub { get; set; }
    public DefaultsConfig Defaults { get; set; } = new();
    public GitConfig Git { get; set; } = new();
}

/// <summary>Explicit local preferences, never a repository-selected credential identity.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TwigUserConfig
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GitHubCredentialSource { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GitHubAppClientId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GitHubOAuthIdentity { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GitHubAccount { get; set; }

    private DisplayConfig _display = new();
    [JsonIgnore]
    public DisplayConfig Display { get => _display; set => _display = value ?? new(); }
    [JsonInclude]
    [JsonPropertyName("display")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal DisplayConfig? StoredDisplay { get => _display.HasOverrides ? _display : null; set => _display = value ?? new(); }

    internal bool HasOverrides => StoredDisplay is not null || GitHubAccount is not null
        || GitHubCredentialSource is not null || GitHubAppClientId is not null || GitHubOAuthIdentity is not null;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DefaultsConfig
{
    public List<AreaPathEntry>? AreaPathEntries { get; set; }
    public string? IterationPath { get; set; }

    /// <summary>No entries means no configured area scope, not an implicit provider root.</summary>
    public IReadOnlyList<(string Path, bool IncludeChildren)>? ResolveAreaPaths()
        => AreaPathEntries is { Count: > 0 }
            ? AreaPathEntries.Select(e => (e.Path, e.IncludeChildren)).ToList()
            : null;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AreaPathEntry
{
    public string Path { get; set; } = string.Empty;
    public bool IncludeChildren { get; set; } = true;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DisplayConfig
{
    [JsonIgnore]
    public int TreeDepthUp { get => TreeDepthUpOverride ?? 2; set => TreeDepthUpOverride = value; }
    [JsonInclude, JsonPropertyName("treeDepthUp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal int? TreeDepthUpOverride { get; set; }
    [JsonIgnore]
    public int TreeDepthDown { get => TreeDepthDownOverride ?? 10; set => TreeDepthDownOverride = value; }
    [JsonInclude, JsonPropertyName("treeDepthDown"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal int? TreeDepthDownOverride { get; set; }
    [JsonIgnore]
    public int TreeDepthSideways { get => TreeDepthSidewaysOverride ?? 1; set => TreeDepthSidewaysOverride = value; }
    [JsonInclude, JsonPropertyName("treeDepthSideways"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal int? TreeDepthSidewaysOverride { get; set; }

    private string? _globalIcons;
    [JsonIgnore]
    public string Icons { get => IconsOverride ?? _globalIcons ?? "auto"; set => IconsOverride = value; }
    [JsonPropertyName("icons"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconsOverride { get; set; }
    public void ClearIconsOverride() => IconsOverride = null;
    public void UseGlobalIcons(string? icons) => _globalIcons = icons;
    public static string? NormalizeIcons(string value)
    {
        var mode = value.ToLowerInvariant();
        return mode is "auto" or "unicode" or "nerd" ? mode : null;
    }

    internal bool HasOverrides => TreeDepthUpOverride.HasValue || TreeDepthDownOverride.HasValue
        || TreeDepthSidewaysOverride.HasValue || IconsOverride is not null;

    public string ResolveIconMode()
    {
        if (!string.Equals(Icons, "auto", StringComparison.OrdinalIgnoreCase))
            return ResolveIconMode(false, null, null);
        return ResolveIconMode(Console.IsOutputRedirected, Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("TERM_PROGRAM"));
    }

    internal string ResolveIconMode(bool outputRedirected, string? term, string? terminalProgram)
    {
        if (string.Equals(Icons, "nerd", StringComparison.OrdinalIgnoreCase)) return "nerd";
        if (!string.Equals(Icons, "auto", StringComparison.OrdinalIgnoreCase)
            || outputRedirected || string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
            return "unicode";
        var supportsNerdFonts =
            string.Equals(term, "xterm-kitty", StringComparison.OrdinalIgnoreCase)
            || string.Equals(term, "xterm-ghostty", StringComparison.OrdinalIgnoreCase)
            || string.Equals(term, "wezterm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(terminalProgram, "kitty", StringComparison.OrdinalIgnoreCase)
            || string.Equals(terminalProgram, "WezTerm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(terminalProgram, "ghostty", StringComparison.OrdinalIgnoreCase);
        return supportsNerdFonts ? "nerd" : "unicode";
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GitConfig
{
    public string? Project { get; set; }
    public string? Repository { get; set; }
}
