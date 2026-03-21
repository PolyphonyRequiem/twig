using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
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

    /// <summary>
    /// Production constructor — accepts auth + HTTP so it can construct an
    /// <see cref="AdoIterationService"/> with the org/project from command args
    /// (not from the potentially-empty config loaded at DI time).
    /// </summary>
    public InitCommand(IAuthenticationProvider authProvider, HttpClient httpClient, TwigPaths paths,
        OutputFormatterFactory formatterFactory, HintEngine hintEngine)
    {
        _authProvider = authProvider;
        _httpClient = httpClient;
        _paths = paths;
        _formatterFactory = formatterFactory;
        _hintEngine = hintEngine;
    }

    /// <summary>
    /// Test constructor — uses an injected <see cref="IIterationService"/> mock
    /// so unit tests don't need real auth or HTTP.
    /// </summary>
    public InitCommand(IIterationService iterationService, TwigPaths paths,
        OutputFormatterFactory formatterFactory, HintEngine hintEngine)
    {
        _iterationService = iterationService;
        _paths = paths;
        _formatterFactory = formatterFactory;
        _hintEngine = hintEngine;
    }

    public async Task<int> ExecuteAsync(string org, string project, string? team = null, string? gitProject = null, bool force = false, string outputFormat = "human", CancellationToken ct = default)
    {
        var fmt = _formatterFactory.GetFormatter(outputFormat);

        // Derive context-specific paths from the supplied org/project args
        var contextPaths = TwigPaths.ForContext(_paths.TwigDir, org, project);

        if (Directory.Exists(_paths.TwigDir) && !force)
        {
            Console.Error.WriteLine(fmt.FormatError("Twig workspace already initialized. Use --force to reinitialize."));
            return 1;
        }

        // FM-008: --force reinit — delete only the current context's DB, not the entire .twig/ tree
        if (force && Directory.Exists(_paths.TwigDir))
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

        // Create .twig/ root and nested context directory
        Directory.CreateDirectory(_paths.TwigDir);
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
        await config.SaveAsync(_paths.ConfigPath);

        // Build iteration service with the supplied org/project (not from DI config)
        var effectiveTeam = string.IsNullOrWhiteSpace(team) ? $"{project} Team" : team;
        var iterationService = _iterationService
            ?? new AdoIterationService(_httpClient!, _authProvider!, org, project, effectiveTeam);

        Console.WriteLine(fmt.FormatInfo("Detecting process template..."));
        var template = await iterationService.DetectTemplateNameAsync();
        Console.WriteLine(fmt.FormatInfo($"  Process template: {template}"));

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
        try
        {
            var iteration = await iterationService.GetCurrentIterationAsync();
            Console.WriteLine(fmt.FormatInfo($"  Current iteration: {iteration}"));
            config.Defaults.IterationPath = iteration.ToString();
        }
        catch (Exception ex) when (ex is Twig.Infrastructure.Ado.Exceptions.AdoNotFoundException
                                     or Twig.Infrastructure.Ado.Exceptions.AdoException)
        {
            Console.WriteLine(fmt.FormatInfo($"  \u26a0 Could not detect current iteration: {ex.Message}"));
            Console.WriteLine(fmt.FormatHint("You can set it later with: twig config defaults.iterationPath '<path>'"));
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

        await config.SaveAsync(_paths.ConfigPath);

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

        // SEC-001: Append .twig/ to .gitignore
        AppendToGitignore();

        // Blank line before success message (human output only)
        if (!string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine();
        Console.WriteLine(fmt.FormatSuccess($"Initialized Twig workspace in {_paths.TwigDir}"));

        var hints = _hintEngine.GetHints("init", outputFormat: outputFormat);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
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
