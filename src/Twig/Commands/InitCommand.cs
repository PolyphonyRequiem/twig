using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Field;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig init</c>: creates .twig/ directory, config file, detects process template,
/// initializes SQLite cache, and appends .twig/ to .gitignore (SEC-001).
/// </summary>
public sealed class InitCommand
{
    private readonly IIterationService? _iterationService;
    private readonly IAuthenticationProvider? _authProvider;
    private readonly HttpClient? _httpClient;
    private readonly TwigPaths _paths;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly IGlobalProfileStore? _globalProfileStore;
    private readonly ITelemetryClient? _telemetryClient;
    private readonly IConsoleInput? _consoleInput;

    /// <summary>
    /// Production constructor — accepts auth + HTTP so it can construct an
    /// <see cref="AdoIterationService"/> with the org/project from command args
    /// (not from the potentially-empty config loaded at DI time).
    /// </summary>
    public InitCommand(IAuthenticationProvider authProvider, HttpClient httpClient, TwigPaths paths,
        OutputFormatterFactory formatterFactory, HintEngine hintEngine, IGlobalProfileStore globalProfileStore,
        IConsoleInput consoleInput,
        ITelemetryClient? telemetryClient = null)
    {
        _authProvider = authProvider;
        _httpClient = httpClient;
        _paths = paths;
        _formatterFactory = formatterFactory;
        _hintEngine = hintEngine;
        _globalProfileStore = globalProfileStore;
        _consoleInput = consoleInput;
        _telemetryClient = telemetryClient;
    }

    /// <summary>
    /// Test constructor — uses an injected <see cref="IIterationService"/> mock
    /// so unit tests don't need real auth or HTTP.
    /// </summary>
    public InitCommand(IIterationService iterationService, TwigPaths paths,
        OutputFormatterFactory formatterFactory, HintEngine hintEngine,
        IGlobalProfileStore? globalProfileStore = null,
        IConsoleInput? consoleInput = null,
        ITelemetryClient? telemetryClient = null)
    {
        _iterationService = iterationService;
        _paths = paths;
        _formatterFactory = formatterFactory;
        _hintEngine = hintEngine;
        _globalProfileStore = globalProfileStore;
        _consoleInput = consoleInput;
        _telemetryClient = telemetryClient;
    }

    public async Task<int> ExecuteAsync(string org, string project, string? team = null, string? gitProject = null, bool force = false, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var (exitCode, hadGlobalProfile, fieldCount) = await ExecuteCoreAsync(org, project, team, gitProject, force, outputFormat, ct);
        _telemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
        {
            ["command"] = "init",
            ["exit_code"] = exitCode.ToString(),
            ["output_format"] = outputFormat,
            ["twig_version"] = VersionHelper.GetVersion(),
            ["os_platform"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["had_global_profile"] = hadGlobalProfile.ToString()
        }, new Dictionary<string, double>
        {
            ["duration_ms"] = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            ["field_count"] = fieldCount
        });
        return exitCode;
    }

    private async Task<(int ExitCode, bool HadGlobalProfile, int FieldCount)> ExecuteCoreAsync(string org, string project, string? team, string? gitProject, bool force, string outputFormat, CancellationToken ct)
    {
        var fmt = _formatterFactory.GetFormatter(outputFormat);
        var telemetryHadGlobalProfile = false;
        var telemetryFieldCount = 0;

        // init always targets CWD, not the walked-up .twig/ ancestor.
        // This prevents ~/projects/repo from reusing ~/.twig when repos live under ~.
        var twigDir = Path.Combine(_paths.StartDir, ".twig");
        var configPath = Path.Combine(twigDir, "config");

        // Derive context-specific paths from the supplied org/project args
        var contextPaths = TwigPaths.ForContext(twigDir, org, project, _paths.StartDir);

        if (Directory.Exists(twigDir) && !force)
        {
            Console.Error.WriteLine(fmt.FormatError("Twig workspace already initialized. Use --force to reinitialize."));
            return (1, false, 0);
        }

        // FM-008: --force reinit — delete only the current context's DB, not the entire .twig/ tree
        if (force && Directory.Exists(twigDir))
        {
            if (File.Exists(contextPaths.DbPath))
            {
                // Warn if pending changes exist before deleting
                var hasPending = false;
                try
                {
                    using var probe = new Infrastructure.Persistence.SqliteCacheStore($"Data Source={contextPaths.DbPath}");
                    var conn = probe.GetConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM pending_changes;";
                    var count = Convert.ToInt32(cmd.ExecuteScalar());
                    hasPending = count > 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // DB may be corrupt or inaccessible — that's fine, we'll delete and reinitialize it
                }

                if (hasPending)
                    Console.WriteLine(fmt.FormatInfo("\u26a0 Pending changes exist and will be lost."));

                // Release pooled connections before deleting the file
                SqliteConnection.ClearAllPools();

                File.Delete(contextPaths.DbPath);
                // Also delete WAL/SHM journal files
                var walPath = contextPaths.DbPath + "-wal";
                var shmPath = contextPaths.DbPath + "-shm";
                if (File.Exists(walPath)) File.Delete(walPath);
                if (File.Exists(shmPath)) File.Delete(shmPath);
            }
        }

        // Check for .git directory/file alongside the target — warn if missing
        var gitPath = Path.Combine(_paths.StartDir, ".git");
        if (!Directory.Exists(gitPath) && !File.Exists(gitPath))
        {
            if (_consoleInput is not null && !_consoleInput.IsOutputRedirected)
            {
                Console.Error.Write($"\u26a0 No .git directory found at {_paths.StartDir}. This may not be a repository root. Continue? [y/N]: ");
                var response = _consoleInput.ReadLine();
                if (!string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine(fmt.FormatError("Aborted."));
                    return (1, false, 0);
                }
            }
        }

        // Create .twig/ root and nested context directory
        Directory.CreateDirectory(twigDir);
        var contextDir = Path.GetDirectoryName(contextPaths.DbPath)!;
        Directory.CreateDirectory(contextDir);

        // Detect process template and current iteration
        var config = new TwigConfiguration
        {
            Organization = org,
            Project = project,
            Team = team ?? string.Empty,
        };

        // Set git.project if explicitly provided
        if (!string.IsNullOrWhiteSpace(gitProject))
        {
            config.Git.Project = gitProject;
            Console.WriteLine(fmt.FormatInfo($"  Git project: {gitProject}"));
        }

        // Write config early so iteration service can use it
        await config.SaveAsync(configPath);

        // Build iteration service with the supplied org/project (not from DI config)
        var effectiveTeam = string.IsNullOrWhiteSpace(team) ? $"{project} Team" : team;
        var iterationService = _iterationService
            ?? new AdoIterationService(_httpClient!, _authProvider!, org, project, effectiveTeam);

        var template = await iterationService.DetectTemplateNameAsync();
        config.ProcessTemplate = template ?? string.Empty;

        Console.WriteLine(fmt.FormatInfo("Fetching type appearances..."));
        var appearances = await iterationService.GetWorkItemTypeAppearancesAsync();
        var typeAppearances = new List<TypeAppearanceConfig>(appearances.Count);
        foreach (var appearance in appearances)
        {
            typeAppearances.Add(new TypeAppearanceConfig
            {
                Name = appearance.Name,
                Color = appearance.Color ?? string.Empty,
                IconId = appearance.IconId
            });
        }
        config.TypeAppearances = typeAppearances;
        Console.WriteLine(fmt.FormatInfo($"  Loaded {appearances.Count} type appearance(s)."));

        Console.WriteLine(fmt.FormatInfo("Fetching team area paths..."));
        try
        {
            var areaPaths = await iterationService.GetTeamAreaPathsAsync();
            if (areaPaths.Count > 0)
            {
                config.Defaults.AreaPathEntries = areaPaths
                    .Select(ap => new AreaPathEntry { Path = ap.Path, IncludeChildren = ap.IncludeChildren })
                    .ToList();
                // Also populate AreaPaths for backward compatibility
                config.Defaults.AreaPaths = areaPaths.Select(ap => ap.Path).ToList();
                foreach (var ap in areaPaths)
                    Console.WriteLine(fmt.FormatInfo($"  Area path: {ap.Path}{(ap.IncludeChildren ? " (include children)" : "")}"));
            }
        }
        catch (Exception ex) when (ex is Twig.Infrastructure.Ado.Exceptions.AdoNotFoundException
                                     or Twig.Infrastructure.Ado.Exceptions.AdoException)
        {
            Console.WriteLine(fmt.FormatInfo($"  \u26a0 Could not detect team area paths: {ex.Message}"));
            Console.WriteLine(fmt.FormatHint("You can set it later with: twig config defaults.areapaths 'Path1;Path2'"));
        }

        Console.WriteLine(fmt.FormatInfo("Getting current iteration..."));
        Domain.ValueObjects.IterationPath? currentIteration = null;
        try
        {
            currentIteration = await iterationService.GetCurrentIterationAsync();
            Console.WriteLine(fmt.FormatInfo($"  Current iteration: {currentIteration}"));
        }
        catch (Exception ex) when (ex is Twig.Infrastructure.Ado.Exceptions.AdoNotFoundException
                                     or Twig.Infrastructure.Ado.Exceptions.AdoException)
        {
            Console.WriteLine(fmt.FormatInfo($"  \u26a0 Could not detect current iteration: {ex.Message}"));
        }

        // Detect authenticated user identity
        Console.WriteLine(fmt.FormatInfo("Detecting user identity..."));
        try
        {
            var displayName = await iterationService.GetAuthenticatedUserDisplayNameAsync();
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                config.User.DisplayName = displayName;
                Console.WriteLine(fmt.FormatInfo($"  User: {displayName}"));
            }
            else
            {
                Console.WriteLine(fmt.FormatInfo("  \u26a0 Could not detect user identity."));
                Console.WriteLine(fmt.FormatHint("You can set it later with: twig config user.name '<Your Name>'"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(fmt.FormatInfo("  \u26a0 Could not detect user identity."));
            Console.WriteLine(fmt.FormatHint("You can set it later with: twig config user.name '<Your Name>'"));
        }

        // Prompt for workspace mode (TTY only; default to sprint in non-TTY)
        if (_consoleInput is not null && !_consoleInput.IsOutputRedirected)
        {
            Console.Write("Default workspace mode? [sprint/workspace] (sprint): ");
            var modeResponse = _consoleInput.ReadLine()?.Trim().ToLowerInvariant();
            if (modeResponse is "workspace")
                config.Defaults.Mode = "workspace";
        }

        await config.SaveAsync(configPath);

        // Initialize SQLite cache in context-specific path and persist process type data
        using var cacheStore = new Infrastructure.Persistence.SqliteCacheStore($"Data Source={contextPaths.DbPath}");

        // Fetch state sequences and process configuration for all types
        Console.WriteLine(fmt.FormatInfo("Fetching type state sequences..."));
        Console.WriteLine(fmt.FormatInfo("Fetching process configuration..."));
        var processTypeStore = new Infrastructure.Persistence.SqliteProcessTypeStore(cacheStore);
        try
        {
            var count = await ProcessTypeSyncService.SyncAsync(iterationService, processTypeStore);
            Console.WriteLine(fmt.FormatInfo($"  Loaded state sequences for {count} type(s)"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(fmt.FormatInfo($"  ⚠ Could not fetch type data: {ex.Message}"));
        }

        // DD-08: Fetch field definitions during init for immediate availability
        var fieldDefStore = new Infrastructure.Persistence.SqliteFieldDefinitionStore(cacheStore);
        Console.WriteLine(fmt.FormatInfo("Fetching field definitions..."));
        try
        {
            var fieldDefCount = await FieldDefinitionSyncService.SyncAsync(iterationService, fieldDefStore, ct);
            telemetryFieldCount = fieldDefCount;
            Console.WriteLine(fmt.FormatInfo($"  Loaded {fieldDefCount} field definition(s)"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(fmt.FormatInfo($"  ⚠ Could not fetch field definitions: {ex.Message}"));
        }

        // Global profile resolution — apply or merge status-fields from global profile (FR-09: wrapped in try-catch)
        try
        {
            if (_globalProfileStore is not null && template is not null)
            {
                var metadata = await _globalProfileStore.LoadMetadataAsync(org, template, ct);
                if (metadata is not null)
                {
                    telemetryHadGlobalProfile = true;
                    var fieldDefs = await fieldDefStore.GetAllAsync(ct);
                    if (fieldDefs.Count > 0)
                    {
                        var currentHash = FieldDefinitionHasher.ComputeFieldHash(fieldDefs);
                        var profileContent = await _globalProfileStore.LoadStatusFieldsAsync(org, template, ct);
                        if (profileContent is not null)
                        {
                            if (metadata.FieldDefinitionHash == currentHash)
                            {
                                // Hash match → copy profile status-fields verbatim (DD-05: workspace layer)
                                await File.WriteAllTextAsync(contextPaths.StatusFieldsPath, profileContent, ct);
                                Console.WriteLine(fmt.FormatInfo($"✓ Applied existing field configuration for {org}/{template}"));
                            }
                            else
                            {
                                // Hash mismatch → merge with existing preferences
                                var mergedContent = StatusFieldsConfig.Generate(fieldDefs, profileContent);
                                await File.WriteAllTextAsync(contextPaths.StatusFieldsPath, mergedContent, ct);
                                await _globalProfileStore.SaveStatusFieldsAsync(org, template, mergedContent, ct);
                                var updatedMetadata = metadata with
                                {
                                    FieldDefinitionHash = currentHash,
                                    LastSyncedAt = DateTimeOffset.UtcNow,
                                    FieldCount = fieldDefs.Count
                                };
                                await _globalProfileStore.SaveMetadataAsync(org, template, updatedMetadata, ct);
                                Console.WriteLine(fmt.FormatInfo("⚠ Process fields changed — merged with existing preferences"));
                                Console.WriteLine(fmt.FormatHint("Run 'twig config status-fields' to review"));
                            }
                        }
                    }
                }
                // If no profile exists → skip silently (first workspace for this org/process)
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // FR-09: init failures in profile logic must never block init completion
            Console.WriteLine(fmt.FormatInfo($"  ⚠ Could not apply global profile: {ex.Message}"));
            Console.WriteLine(fmt.FormatHint("Run 'twig config status-fields' to configure manually"));
        }

        // SEC-001: Append .twig/ to .gitignore
        AppendToGitignore();

        // Inline refresh: populate the cache with sprint items so the workspace isn't empty after init
        if (currentIteration is not null && _httpClient is not null && _authProvider is not null)
        {
            try
            {
                Console.WriteLine(fmt.FormatInfo("Refreshing sprint items..."));
                var adoClient = new AdoRestClient(_httpClient, _authProvider, org, project, new WorkItemMapper());
                var workItemRepo = new Infrastructure.Persistence.SqliteWorkItemRepository(cacheStore, new WorkItemMapper());
                var contextStore = new Infrastructure.Persistence.SqliteContextStore(cacheStore);

                // Build WIQL query scoped to current iteration + area paths
                var sanitizedPath = currentIteration.Value.Value.Replace("'", "''");
                var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.IterationPath] = '{sanitizedPath}'";
                var areaPathEntries = config.Defaults?.AreaPathEntries;
                if (areaPathEntries is { Count: > 0 })
                {
                    var clauses = areaPathEntries
                        .Select(entry =>
                        {
                            var escaped = entry.Path.Replace("'", "''");
                            var op = entry.IncludeChildren ? "UNDER" : "=";
                            return $"[System.AreaPath] {op} '{escaped}'";
                        });
                    wiql += $" AND ({string.Join(" OR ", clauses)})";
                }
                else
                {
                    var areaPaths = config.Defaults?.AreaPaths;
                    if (areaPaths is { Count: > 0 })
                    {
                        var clauses = areaPaths
                            .Select(ap => $"[System.AreaPath] UNDER '{ap.Replace("'", "''")}'");
                        wiql += $" AND ({string.Join(" OR ", clauses)})";
                    }
                }
                wiql += " ORDER BY [System.Id]";

                var ids = await adoClient.QueryByWiqlAsync(wiql);
                var realIds = ids.Where(id => id > 0).ToList();
                if (realIds.Count > 0)
                {
                    var sprintItems = await adoClient.FetchBatchAsync(realIds, ct);
                    await workItemRepo.SaveBatchAsync(sprintItems);
                    Console.WriteLine(fmt.FormatInfo($"  Cached {sprintItems.Count} sprint item(s)."));
                }
                else
                {
                    Console.WriteLine(fmt.FormatInfo("  No items found in current iteration."));
                }

                // Set cache freshness timestamp
                await contextStore.SetValueAsync("last_refreshed_at", DateTimeOffset.UtcNow.ToString("O"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(fmt.FormatInfo($"  \u26a0 Could not refresh sprint items: {ex.Message}"));
                Console.WriteLine(fmt.FormatHint("Run 'twig sync' to populate your workspace."));
            }
        }

        // Blank line before success message (human output only)
        if (!string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine();
        Console.WriteLine(fmt.FormatSuccess($"Initialized Twig workspace in {twigDir}"));

        var hints = _hintEngine.GetHints("init", outputFormat: outputFormat);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return (0, telemetryHadGlobalProfile, telemetryFieldCount);
    }

    private void AppendToGitignore()
    {
        var gitignorePath = Path.Combine(Directory.GetCurrentDirectory(), ".gitignore");
        const string twigEntry = ".twig/";

        if (File.Exists(gitignorePath))
        {
            var content = File.ReadAllText(gitignorePath);
            if (content.Contains(twigEntry, StringComparison.Ordinal))
                return;

            // Ensure we're on a new line
            if (!content.EndsWith('\n') && content.Length > 0)
                File.AppendAllText(gitignorePath, Environment.NewLine);

            File.AppendAllText(gitignorePath, twigEntry + Environment.NewLine);
        }
        else
        {
            File.WriteAllText(gitignorePath, twigEntry + Environment.NewLine);
        }
    }

}