using System.ComponentModel;
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
    /// <see cref="AdoIterationService"/> with the effective init coordinates
    /// instead of the potentially-empty config loaded at DI time.
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

    public async Task<int> ExecuteAsync(string org, string project, string? team = null, string? gitProject = null, bool force = false, string outputFormat = OutputFormatterFactory.DefaultFormat, string? sprint = null, string? area = null, CancellationToken ct = default)
    {
        using var scope = new CommandActivityScope("init", "human");
        int exitCode;
        try
        {
            int fieldCount;
            bool hadGlobalProfile;
            (exitCode, hadGlobalProfile, fieldCount) = await ExecuteCoreAsync(org, project, team, gitProject, force, outputFormat, sprint, area, ct);
            scope.Complete(exitCode);
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
                ["duration_ms"] = Stopwatch.GetElapsedTime(scope.StartTimestamp).TotalMilliseconds,
                ["field_count"] = fieldCount
            });
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
    }

    private async Task<(int ExitCode, bool HadGlobalProfile, int FieldCount)> ExecuteCoreAsync(string org, string project, string? team, string? gitProject, bool force, string outputFormat, string? sprint, string? area, CancellationToken ct)
    {
        var fmt = _formatterFactory.GetFormatter(outputFormat);
        var telemetryHadGlobalProfile = false;
        var telemetryFieldCount = 0;

        // init always targets CWD, not the walked-up .twig/ ancestor.
        // This prevents ~/projects/repo from reusing ~/.twig when repos live under ~.
        var twigDir = Path.Combine(_paths.StartDir, ".twig");

        // Derive context-specific paths from the supplied org/project args
        var requestedContextPaths = TwigPaths.ForContext(twigDir, org, project, _paths.StartDir);
        var preserveRepoManifest = false;
        if (File.Exists(requestedContextPaths.RepoConfigPath))
        {
            try
            {
                preserveRepoManifest = await IsRepoManifestTrackedAsync(_paths.StartDir, ct);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(fmt.FormatError(ex.Message));
                return (1, false, 0);
            }
        }

        var config = preserveRepoManifest
            ? await TwigConfiguration.LoadSplitAsync(requestedContextPaths, ct)
            : new TwigConfiguration
            {
                Organization = org,
                Project = project,
                Team = team ?? string.Empty,
            };

        var contextPaths = preserveRepoManifest
            ? TwigPaths.ForContext(twigDir, config.Organization, config.Project, _paths.StartDir)
            : requestedContextPaths;

        if (File.Exists(contextPaths.DbPath) && !force)
        {
            Console.Error.WriteLine(fmt.FormatError("Twig workspace already initialized. Use --force to reinitialize."));
            return (1, false, 0);
        }

        if (preserveRepoManifest
            && GetManifestCoordinateConflict(config, org, project, team) is { } coordinateConflict)
        {
            Console.Error.WriteLine(fmt.FormatError(coordinateConflict));
            return (1, false, 0);
        }

        if (preserveRepoManifest
            && GetManifestOverrideConflict(gitProject, sprint, area) is { } overrideConflict)
        {
            Console.Error.WriteLine(fmt.FormatError(overrideConflict));
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
                    Console.WriteLine("\u26a0 Pending changes exist and will be lost.");

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

        // Set git.project if explicitly provided
        if (!string.IsNullOrWhiteSpace(gitProject))
        {
            config.Git.Project = gitProject;
            Console.WriteLine($"  Git project: {gitProject}");
        }

        // AB#3296: write the new split shape — twig.json (committed manifest) at
        // repo root and .twig/config (gitignored user prefs). Fresh init always
        // produces the new shape; migration handles legacy upgrades.
        if (preserveRepoManifest)
            await config.SaveUserAsync(contextPaths.ConfigPath, ct);
        else
            await config.SaveSplitAsync(contextPaths, ct);

        var effectiveOrg = config.Organization;
        var effectiveProject = config.Project;
        var effectiveTeam = string.IsNullOrWhiteSpace(config.Team) ? $"{effectiveProject} Team" : config.Team;
        var iterationService = _iterationService
            ?? new AdoIterationService(_httpClient!, _authProvider!, effectiveOrg, effectiveProject, effectiveTeam);

        var isInteractive = _consoleInput is not null && !_consoleInput.IsOutputRedirected;

        var template = await iterationService.DetectTemplateNameAsync();
        if (!preserveRepoManifest)
            config.ProcessTemplate = template ?? string.Empty;

        // AB#3296 PR-3: type appearances are sourced from the SQLite cache
        // (process_types table, populated below by ProcessTypeSyncService).
        // The previous explicit GetWorkItemTypeAppearancesAsync fetch was
        // redundant — same data, written to two places. The 60-line JSON
        // array no longer ships to .twig/config.

        // DD-8/FR-17: Only auto-detect area paths in interactive mode.
        // Non-interactive init starts empty; use --area flag for explicit config.
        if (isInteractive && !preserveRepoManifest)
        {
            Console.WriteLine("Fetching team area paths...");
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
                        Console.WriteLine($"  Area path: {ap.Path}{(ap.IncludeChildren ? " (include children)" : "")}");
                }
            }
            catch (Exception ex) when (ex is Twig.Infrastructure.Ado.Exceptions.AdoNotFoundException
                                         or Twig.Infrastructure.Ado.Exceptions.AdoException)
            {
                Console.WriteLine($"  \u26a0 Could not detect team area paths: {ex.Message}");
                Console.WriteLine("You can set it later with: twig config defaults.areapaths 'Path1;Path2'");
            }
        }

        Console.WriteLine("Getting current iteration...");
        Domain.ValueObjects.IterationPath? currentIteration = null;
        try
        {
            currentIteration = await iterationService.GetCurrentIterationAsync();
            Console.WriteLine($"  Current iteration: {currentIteration}");
        }
        catch (Exception ex) when (ex is Twig.Infrastructure.Ado.Exceptions.AdoNotFoundException
                                     or Twig.Infrastructure.Ado.Exceptions.AdoException)
        {
            Console.WriteLine($"  \u26a0 Could not detect current iteration: {ex.Message}");
        }

        // Detect authenticated user identity
        Console.WriteLine("Detecting user identity...");
        try
        {
            var displayName = await iterationService.GetAuthenticatedUserDisplayNameAsync();
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                config.User.DisplayName = displayName;
                Console.WriteLine($"  User: {displayName}");
            }
            else
            {
                Console.WriteLine("  \u26a0 Could not detect user identity.");
                Console.WriteLine("You can set it later with: twig config user.name '<Your Name>'");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine("  \u26a0 Could not detect user identity.");
            Console.WriteLine("You can set it later with: twig config user.name '<Your Name>'");
        }

        // Prompt for workspace mode (TTY only; default to sprint in non-TTY)
        if (isInteractive && !preserveRepoManifest)
        {
            Console.Write("Default workspace mode? [sprint/workspace] (sprint): ");
            var modeResponse = _consoleInput!.ReadLine()?.Trim().ToLowerInvariant();
            if (modeResponse is "workspace")
                config.Defaults.Mode = "workspace";
        }

        // Prompt for workspace sources (TTY only; skip if --sprint or --area flags provided)
        if (isInteractive
            && !preserveRepoManifest
            && string.IsNullOrWhiteSpace(sprint) && string.IsNullOrWhiteSpace(area))
        {
            Console.WriteLine("Workspace sources \u2014 what should be included in your workspace?");
            Console.WriteLine("  1. Sprint only (@Current)");
            Console.WriteLine("  2. Area paths only (sync from team)");
            Console.WriteLine("  3. Both sprint and area paths");
            Console.WriteLine("  4. Neither (start empty, configure later)");
            Console.Write("Choose [1-4] (4): ");
            var prefResponse = _consoleInput!.ReadLine()?.Trim();

            switch (prefResponse)
            {
                case "1": // Sprint only
                    config.Workspace.Sprints = [new SprintEntry { Expression = "@current" }];
                    config.Defaults.AreaPathEntries = [];
                    config.Defaults.AreaPaths = [];
                    Console.WriteLine("  Sprint: @current");
                    break;
                case "2": // Area paths only — keep auto-detected areas
                    Console.WriteLine("  Keeping team area paths");
                    break;
                case "3": // Both
                    config.Workspace.Sprints = [new SprintEntry { Expression = "@current" }];
                    Console.WriteLine("  Sprint: @current");
                    Console.WriteLine("  Keeping team area paths");
                    break;
                default: // "4" or any other input → Neither (start empty)
                    config.Defaults.AreaPathEntries = [];
                    config.Defaults.AreaPaths = [];
                    Console.WriteLine("  Starting empty \u2014 configure later with workspace commands");
                    break;
            }
        }

        // --sprint flag: add sprint expressions to workspace.sprints[]
        if (!string.IsNullOrWhiteSpace(sprint))
        {
            var expressions = sprint.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var sprintEntries = new List<SprintEntry>();
            foreach (var expr in expressions)
            {
                var parsed = IterationExpression.Parse(expr);
                if (!parsed.IsSuccess)
                {
                    Console.Error.WriteLine(fmt.FormatError($"Invalid sprint expression '{expr}': {parsed.Error}"));
                    return (1, telemetryHadGlobalProfile, 0);
                }
                sprintEntries.Add(new SprintEntry { Expression = expr });
            }
            config.Workspace.Sprints = sprintEntries;
            foreach (var entry in sprintEntries)
                Console.WriteLine($"  Sprint: {entry.Expression}");
        }

        // --area flag: add area path entries to defaults.areapathentries[]
        if (!string.IsNullOrWhiteSpace(area))
        {
            var areaParts = area.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var areaEntries = new List<AreaPathEntry>();
            foreach (var raw in areaParts)
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
                {
                    Console.Error.WriteLine(fmt.FormatError($"Invalid area path '{pathPart}': {parsed.Error}"));
                    return (1, telemetryHadGlobalProfile, 0);
                }
                areaEntries.Add(new AreaPathEntry { Path = pathPart, IncludeChildren = includeChildren });
            }
            config.Defaults.AreaPathEntries = areaEntries;
            config.Defaults.AreaPaths = areaEntries.Select(e => e.Path).ToList();
            foreach (var entry in areaEntries)
                Console.WriteLine($"  Area: {entry.Path}{(entry.IncludeChildren ? "" : " (exact)")}");
        }

        if (preserveRepoManifest)
            await config.SaveUserAsync(contextPaths.ConfigPath, ct);
        else
            await config.SaveSplitAsync(contextPaths, ct);

        // Initialize SQLite cache in context-specific path and persist process type data
        using var cacheStore = new Infrastructure.Persistence.SqliteCacheStore($"Data Source={contextPaths.DbPath}");

        // Fetch state sequences and process configuration for all types
        Console.WriteLine("Fetching type state sequences...");
        Console.WriteLine("Fetching process configuration...");
        var processTypeStore = new Infrastructure.Persistence.SqliteProcessTypeStore(cacheStore);
        try
        {
            var count = await ProcessTypeSyncService.SyncAsync(iterationService, processTypeStore);
            Console.WriteLine($"  Loaded state sequences for {count} type(s)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  ⚠ Could not fetch type data: {ex.Message}");
        }

        // DD-08: Fetch field definitions during init for immediate availability
        var fieldDefStore = new Infrastructure.Persistence.SqliteFieldDefinitionStore(cacheStore);
        Console.WriteLine("Fetching field definitions...");
        try
        {
            var fieldDefCount = await FieldDefinitionSyncService.SyncAsync(iterationService, fieldDefStore, ct);
            telemetryFieldCount = fieldDefCount;
            Console.WriteLine($"  Loaded {fieldDefCount} field definition(s)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  ⚠ Could not fetch field definitions: {ex.Message}");
        }

        // Global profile resolution — apply or merge status-fields from global profile (FR-09: wrapped in try-catch)
        try
        {
            if (_globalProfileStore is not null && template is not null)
            {
                var metadata = await _globalProfileStore.LoadMetadataAsync(effectiveOrg, template, ct);
                if (metadata is not null)
                {
                    telemetryHadGlobalProfile = true;
                    var fieldDefs = await fieldDefStore.GetAllAsync(ct);
                    if (fieldDefs.Count > 0)
                    {
                        var currentHash = FieldDefinitionHasher.ComputeFieldHash(fieldDefs);
                        var profileContent = await _globalProfileStore.LoadStatusFieldsAsync(effectiveOrg, template, ct);
                        if (profileContent is not null)
                        {
                            if (metadata.FieldDefinitionHash == currentHash)
                            {
                                // Hash match → copy profile status-fields verbatim (DD-05: workspace layer)
                                await File.WriteAllTextAsync(contextPaths.StatusFieldsPath, profileContent, ct);
                                Console.WriteLine($"✓ Applied existing field configuration for {effectiveOrg}/{template}");
                            }
                            else
                            {
                                // Hash mismatch → merge with existing preferences
                                var mergedContent = StatusFieldsConfig.Generate(fieldDefs, profileContent);
                                await File.WriteAllTextAsync(contextPaths.StatusFieldsPath, mergedContent, ct);
                                await _globalProfileStore.SaveStatusFieldsAsync(effectiveOrg, template, mergedContent, ct);
                                var updatedMetadata = metadata with
                                {
                                    FieldDefinitionHash = currentHash,
                                    LastSyncedAt = DateTimeOffset.UtcNow,
                                    FieldCount = fieldDefs.Count
                                };
                                await _globalProfileStore.SaveMetadataAsync(effectiveOrg, template, updatedMetadata, ct);
                                Console.WriteLine("⚠ Process fields changed — merged with existing preferences");
                                Console.WriteLine("Run 'twig config status-fields' to review");
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
            Console.WriteLine($"  ⚠ Could not apply global profile: {ex.Message}");
            Console.WriteLine("Run 'twig config status-fields' to configure manually");
        }

        // SEC-001: Append .twig/ to .gitignore
        AppendToGitignore();

        // DD-8/FR-17: Only inline-refresh when workspace has configured sources.
        // Non-interactive init with no flags starts empty; interactive "Neither" also skips.
        var hasConfiguredSources = (config.Workspace.Sprints is { Count: > 0 }) ||
                                   config.Defaults.ResolveAreaPaths() is not null;

        // Inline refresh: populate the cache with sprint items when workspace sources exist
        if (hasConfiguredSources && _httpClient is not null && _authProvider is not null)
        {
            try
            {
                Console.WriteLine("Refreshing sprint items...");
                var adoClient = new AdoRestClient(
                    _httpClient,
                    _authProvider,
                    effectiveOrg,
                    effectiveProject,
                    new WorkItemMapper());
                var workItemRepo = new Infrastructure.Persistence.SqliteWorkItemRepository(cacheStore, new WorkItemMapper());
                var contextStore = new Infrastructure.Persistence.SqliteContextStore(cacheStore);

                // Resolve configured sprint expressions to concrete iteration paths
                var sprintEntries = config.Workspace.Sprints;
                IReadOnlyList<IterationPath> resolvedIterations = [];
                if (sprintEntries is { Count: > 0 })
                {
                    var sprintResolver = new SprintIterationResolver(iterationService, workItemRepo);
                    var expressions = new List<IterationExpression>(sprintEntries.Count);
                    foreach (var entry in sprintEntries)
                    {
                        var parseResult = IterationExpression.Parse(entry.Expression);
                        if (parseResult.IsSuccess)
                            expressions.Add(parseResult.Value);
                    }
                    if (expressions.Count > 0)
                        resolvedIterations = await sprintResolver.ResolveAllAsync(expressions, ct);
                }

                // Build WIQL with multi-sprint OR-joined iteration clauses
                var wiql = "SELECT [System.Id] FROM WorkItems";
                var whereClauses = new List<string>();

                if (resolvedIterations.Count > 0)
                {
                    var iterationClauses = resolvedIterations
                        .Select(ip => $"[System.IterationPath] = '{ip.Value.Replace("'", "''")}'");
                    var joined = string.Join(" OR ", iterationClauses);
                    whereClauses.Add(resolvedIterations.Count == 1 ? joined : $"({joined})");
                }

                // Build area path filter: prefer AreaPathEntries (with IncludeChildren), fall back to AreaPaths
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
                    whereClauses.Add(areaPathEntries.Count == 1
                        ? clauses.First()
                        : $"({string.Join(" OR ", clauses)})");
                }
                else
                {
                    var areaPaths = config.Defaults?.AreaPaths;
                    if (areaPaths is { Count: > 0 })
                    {
                        var clauses = areaPaths
                            .Select(ap => $"[System.AreaPath] UNDER '{ap.Replace("'", "''")}'");
                        whereClauses.Add(areaPaths.Count == 1
                            ? clauses.First()
                            : $"({string.Join(" OR ", clauses)})");
                    }
                }

                if (whereClauses.Count > 0)
                {
                    wiql += " WHERE " + string.Join(" AND ", whereClauses);
                }
                wiql += " ORDER BY [System.Id]";

                // Skip query when no WHERE clauses were generated (all expressions failed to resolve)
                if (whereClauses.Count == 0)
                {
                    Console.WriteLine("  No iterations or area paths resolved — skipping refresh.");
                }
                else
                {
                    var ids = await adoClient.QueryByWiqlAsync(wiql);
                    var realIds = ids.Where(id => id > 0).ToList();
                    if (realIds.Count > 0)
                    {
                        var sprintItems = await adoClient.FetchBatchAsync(realIds, ct);
                        await workItemRepo.SaveBatchAsync(sprintItems);
                        Console.WriteLine($"  Cached {sprintItems.Count} sprint item(s).");
                    }
                    else
                    {
                        Console.WriteLine("  No items found in configured iterations.");
                    }

                    // Set cache freshness timestamp
                    await contextStore.SetValueAsync("last_refreshed_at", DateTimeOffset.UtcNow.ToString("O"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"  \u26a0 Could not refresh sprint items: {ex.Message}");
                Console.WriteLine("Run 'twig sync' to populate your workspace.");
            }
        }

        // Blank line before success message (human output only)
        if (!string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine();
        Console.WriteLine($"Initialized Twig workspace in {twigDir}");

        var hints = _hintEngine.GetHints("init", outputFormat: outputFormat);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return (0, telemetryHadGlobalProfile, telemetryFieldCount);
    }

    private static string? GetManifestCoordinateConflict(
        TwigConfiguration config,
        string org,
        string project,
        string? team)
    {
        if (!string.Equals(config.Organization, org, StringComparison.OrdinalIgnoreCase))
            return $"--org '{org}' conflicts with existing twig.json value '{config.Organization}'. The manifest is authoritative.";

        if (!string.Equals(config.Project, project, StringComparison.OrdinalIgnoreCase))
            return $"--project '{project}' conflicts with existing twig.json value '{config.Project}'. The manifest is authoritative.";

        if (!string.IsNullOrWhiteSpace(team)
            && !string.Equals(config.Team, team, StringComparison.OrdinalIgnoreCase))
        {
            return $"--team '{team}' conflicts with existing twig.json value '{config.Team}'. The manifest is authoritative.";
        }

        return null;
    }

    private static string? GetManifestOverrideConflict(string? gitProject, string? sprint, string? area)
    {
        if (!string.IsNullOrWhiteSpace(gitProject))
            return "--git-project cannot override existing tracked twig.json. The manifest is authoritative.";
        if (!string.IsNullOrWhiteSpace(sprint))
            return "--sprint cannot override existing tracked twig.json. The manifest is authoritative.";
        if (!string.IsNullOrWhiteSpace(area))
            return "--area cannot override existing tracked twig.json. The manifest is authoritative.";

        return null;
    }

    private static async Task<bool> IsRepoManifestTrackedAsync(string repoRoot, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("--error-unmatch");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(WorkspaceDiscovery.RepoManifestFileName);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException("Cannot determine whether twig.json is tracked because Git did not start.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);
            if (process.ExitCode == 0)
                return true;
            if (process.ExitCode == 1)
                return false;

            var stderr = await stderrTask;
            if (process.ExitCode == 128
                && stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw new InvalidOperationException(
                $"Cannot determine whether twig.json is tracked because 'git ls-files' exited with code {process.ExitCode}: {stderr.Trim()}");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Cannot determine whether twig.json is tracked because Git is unavailable. Refusing to overwrite the existing manifest.",
                ex);
        }
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