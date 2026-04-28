using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Formatters;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;

SQLitePCL.Batteries.Init();

// ITEM-154: Enable UTF-8 output so Unicode type badges render correctly on Windows.
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (Exception)
{
    // InvariantGlobalization or restricted environment — Unicode bytes will still be emitted; rendering depends on terminal capabilities.
}

// EPIC-005: Clean up old binary left behind from a previous Windows self-update.
SelfUpdater.CleanupOldBinary();

// First-run companion check — installs missing companions after upgrade.
// Must run before ConsoleApp.Create() — no SynchronizationContext, blocking is safe.
CompanionStartup.RunFirstRunCheck();

var app = ConsoleApp.Create()
    .ConfigureServices(services =>
    {
        // Core services (config, paths, SQLite persistence, repositories, stores)
        // Walk-up discovery: find nearest .twig/ in current or ancestor directories.
        // Falls back to CWD-relative for commands that run before init (e.g., twig init).
        // startDir is captured separately so InitCommand can create workspaces in CWD
        // even when walk-up discovers an ancestor .twig/ (e.g., ~/.twig).
        var startDir = Directory.GetCurrentDirectory();
        var twigDir = WorkspaceDiscovery.FindTwigDir()
            ?? Path.Combine(startDir, ".twig");
        var configPath = Path.Combine(twigDir, "config");
        var config = TwigConfiguration.Load(configPath);
        services.AddTwigCoreServices(config, twigDir, startDir);

        // ITEM-138: Migrate legacy flat twig.db → nested context path.
        // LegacyDbMigrator is internal to CLI — must be called here, not in shared registration.
        LegacyDbMigrator.MigrateIfNeeded(twigDir, config);

        // Git remote auto-detection (stays in Program.cs — runtime environment probe)
        // Start in background to overlap with other DI registrations (REVIEW-6).
        var gitProject = config.GetGitProject();
        var repository = config.Git.Repository;
        Task<(string? GitProject, string? Repository)>? gitDetectTask = null;
        if (string.IsNullOrWhiteSpace(repository))
        {
            var capturedConfig = config;
            gitDetectTask = Task.Run(() => DetectGitRemote(capturedConfig));
        }

        // Modular DI registration
        // Resolve deferred git detection before network service registration
        if (gitDetectTask is not null)
        {
            (gitProject, repository) = gitDetectTask.GetAwaiter().GetResult();
        }

        services.AddTwigNetworkServices(config, gitProject, repository);

        // Pre-compute state entries for SpectreTheme (avoids sync-over-async in DI factory)
        IReadOnlyList<StateEntry>? stateEntries = null;
        try
        {
            var paths = TwigPaths.BuildPaths(twigDir, config);

            if (Directory.Exists(paths.TwigDir) && File.Exists(paths.DbPath))
            {
                using var cacheStore = new SqliteCacheStore($"Data Source={paths.DbPath}");
                var processTypeStore = new SqliteProcessTypeStore(cacheStore);
                var records = processTypeStore.GetAllAsync().GetAwaiter().GetResult();
                stateEntries = records.SelectMany(r => r.States).ToList();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // SqliteCacheStore uninitialized or query failed — fall through with null
        }

        services.AddTwigRenderingServices(stateEntries);
        services.AddTwigCommandServices();
        services.AddTwigCommands();
    });

app.UseFilter<ExceptionFilter>();

app.Add<TwigCommands>();
app.Add<OhMyPoshCommands>("ohmyposh");

// Handle --version, grouped help (explicit -h/--help), and smart landing (no args)
if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine(VersionHelper.GetVersion());
    return;
}
if (args.Length == 1 && args[0] is "-h" or "--help" or "help")
{
    GroupedHelp.Show();
    return;
}
if (args.Length == 0)
{
    // Smart landing: route to show if workspace is initialized, otherwise show help
    var twigDirCheck = WorkspaceDiscovery.FindTwigDir();
    if (twigDirCheck is not null)
    {
        args = ["show"];
        // Fall through to app.Run(args)
    }
    else
    {
        GroupedHelp.Show();
        return;
    }
}

// Pre-routing interception: show grouped help for unknown commands instead of
// ConsoleAppFramework's default flat error output.
if (args.Length > 0 && !args[0].StartsWith('-') && !GroupedHelp.IsKnownCommand(args))
{
    GroupedHelp.ShowUnknown(args[0]);
    Environment.ExitCode = 1;
    return;
}

app.Run(args);

// Append usage examples when --help was requested for a specific command.
if (args.Length >= 2 && args.Any(a => a is "-h" or "--help"))
    CommandExamples.ShowIfPresent(args);

/// <summary>
/// Detects the git remote URL by spawning <c>git remote get-url origin</c>.
/// Returns resolved project and repository, or the config defaults on failure.
/// </summary>
static (string? GitProject, string? Repository) DetectGitRemote(TwigConfiguration config)
{
    var gitProject = config.GetGitProject();
    string? repository = null;
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "remote get-url origin")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is not null)
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
            var exited = proc.WaitForExit(2000);
            if (!exited)
            {
                try { proc.Kill(); } catch (Exception) { }
            }
            var remoteUrl = stdoutTask.Result.Trim();
            if (exited && proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(remoteUrl))
            {
                var parsed = AdoRemoteParser.Parse(remoteUrl);
                if (parsed is not null)
                {
                    repository = parsed.Repository;
                    if (string.IsNullOrWhiteSpace(config.Git.Project))
                        gitProject = parsed.Project;
                }
            }
        }
    }
    catch (Exception) { /* git operations are best-effort */ }
    return (gitProject, repository);
}

/// <summary>
/// Global exception filter that writes errors to stderr and sets exit codes.
/// </summary>
internal sealed class ExceptionFilter(ConsoleAppFilter next) : ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(ConsoleAppContext context, CancellationToken ct)
    {
        try
        {
            await Next.InvokeAsync(context, ct);
        }
        catch (OperationCanceledException)
        {
            Environment.ExitCode = 130;
        }
        catch (Exception ex)
        {
            ExceptionHandler.Handle(ex);
        }
    }
}

/// <summary>
/// Testable error-handling logic extracted from <see cref="ExceptionFilter"/>.
/// Maps exceptions to exit codes and writes messages to stderr.
/// </summary>
internal static class ExceptionHandler
{
    /// <summary>
    /// Handles an exception by writing to stderr and setting the exit code.
    /// Returns the exit code that was set.
    /// </summary>
    public static int Handle(Exception ex, TextWriter? stderr = null)
    {
        stderr ??= Console.Error;

        if (ex is OperationCanceledException)
        {
            Environment.ExitCode = 130;
            return 130;
        }

        // FM-001: Offline — ADO unreachable
        if (ex is Twig.Infrastructure.Ado.Exceptions.AdoOfflineException)
        {
            stderr.WriteLine("\u26a0 ADO unreachable. Operating in offline mode.");
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-002 / FM-003: Authentication errors
        if (ex is Twig.Infrastructure.Ado.Exceptions.AdoAuthenticationException authEx)
        {
            stderr.WriteLine($"error: {authEx.Message}");
            if (authEx.Message.Contains("PAT", StringComparison.OrdinalIgnoreCase))
                stderr.WriteLine("Update PAT in .twig/config or $TWIG_PAT.");
            else
                stderr.WriteLine("Run 'az login' to refresh.");
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-004: 404 — Work item not found
        if (ex is Twig.Infrastructure.Ado.Exceptions.AdoNotFoundException notFoundEx)
        {
            var msg = notFoundEx.WorkItemId.HasValue
                ? $"Work item #{notFoundEx.WorkItemId} not found."
                : "Resource not found.";
            stderr.WriteLine($"error: {msg}");
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-005: 400 — Bad request (state transition etc.)
        if (ex is Twig.Infrastructure.Ado.Exceptions.AdoBadRequestException badReqEx)
        {
            stderr.WriteLine($"error: {badReqEx.Message}");
            if (badReqEx.Message.Contains("transition", StringComparison.OrdinalIgnoreCase)
                || badReqEx.Message.Contains("state", StringComparison.OrdinalIgnoreCase))
            {
                stderr.WriteLine("Transition not allowed. Run 'twig sync' to update process configuration.");
            }
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-006: 412 — Concurrency conflict (revision mismatch)
        if (ex is Twig.Infrastructure.Ado.Exceptions.AdoConflictException)
        {
            stderr.WriteLine("error: Concurrency conflict (revision mismatch).");
            stderr.WriteLine("hint: Another change is being processed. Run 'twig sync' and retry.");
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-009: No editor configured
        if (ex is Twig.Commands.EditorNotFoundException)
        {
            stderr.WriteLine($"error: {ex.Message}");
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-008: Cache corruption — SqliteException directly or wrapped in InvalidOperationException
        if (ex is Microsoft.Data.Sqlite.SqliteException
            || (ex is InvalidOperationException && ex.InnerException is Microsoft.Data.Sqlite.SqliteException))
        {
            stderr.WriteLine("\u26a0 Cache corrupted. Run 'twig init --force' to rebuild.");
            Environment.ExitCode = 1;
            return 1;
        }

        stderr.WriteLine($"error: {ex.Message}");
        Environment.ExitCode = 1;
        return 1;
    }
}

/// <summary>
/// Routes all CLI commands to their respective service implementations.
/// Commands are resolved lazily from <see cref="IServiceProvider"/> so that
/// SQLite-dependent services are only constructed when actually needed —
/// not during startup DI resolution. This allows <c>twig init</c> (which
/// creates <c>.twig/</c>) to run before the database exists.
/// </summary>
public sealed class TwigCommands(IServiceProvider services)
{
    internal static string? JoinTrailingText(string? named, string[]? positional)
        => named ?? (positional is { Length: > 0 } ? string.Join(" ", positional) : null);

    /// <summary>Initialize a new Twig workspace.</summary>
    /// <param name="org">Azure DevOps organization name (e.g., contoso).</param>
    /// <param name="project">Azure DevOps project name.</param>
    /// <param name="team">Team name within the project (defaults to project default team).</param>
    /// <param name="gitProject">ADO project that hosts the git repository, if different from the work-item project.</param>
    /// <param name="force">Overwrite existing workspace configuration.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Init(string org, string project, string? team = null, string? gitProject = null, bool force = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<InitCommand>().ExecuteAsync(org, project, team, gitProject, force, output, ct);

    /// <summary>Set the active work item by ID or title pattern.</summary>
    /// <param name="idOrPattern">Work item ID (e.g., 1234) or title substring to match.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Set([Argument] string idOrPattern, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SetCommand>().ExecuteAsync(idOrPattern, output, ct);

    /// <summary>Display a work item without changing context. Syncs by default; use --no-refresh for cache-only.</summary>
    /// <param name="id">Work item ID to display. Omit to show the active work item.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="noRefresh">Skip the sync and show cached data only.</param>
    public async Task<int> Show([Argument] int? id = null, string output = OutputFormatterFactory.DefaultFormat, bool noRefresh = false, CancellationToken ct = default)
        => await services.GetRequiredService<ShowCommand>().ExecuteAsync(id, output, noRefresh, ct);

    /// <summary>Display multiple work items by ID (cache-only). Missing IDs are silently skipped.</summary>
    /// <param name="batch">Comma-separated work item IDs (e.g., 1234,5678,9012).</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("show-batch")]
    public async Task<int> ShowBatch(string batch, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ShowCommand>().ExecuteBatchAsync(batch, output, ct);

    /// <summary>Search and filter work items via ad-hoc WIQL queries.</summary>
    /// <param name="searchText">Free-text search across work item titles and descriptions.</param>
    /// <param name="title">Filter by title text (CONTAINS match on System.Title).</param>
    /// <param name="description">Filter by description text (CONTAINS match on System.Description).</param>
    /// <param name="type">Filter by work item type (e.g., Task, Bug, User Story).</param>
    /// <param name="state">Filter by work item state (e.g., Active, Closed).</param>
    /// <param name="assignedTo">Filter by assigned user (display name or email).</param>
    /// <param name="areaPath">Filter by area path (e.g., Project\Team).</param>
    /// <param name="iterationPath">Filter by iteration path (e.g., Project\Sprint 1).</param>
    /// <param name="createdSince">Show items created after this date (e.g., 2024-01-01, 7d, 2w).</param>
    /// <param name="changedSince">Show items changed after this date (e.g., 2024-01-01, 7d, 2w).</param>
    /// <param name="top">Maximum number of results to return.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Query([Argument] string? searchText = null, string? title = null, string? description = null, string? type = null, string? state = null, string? assignedTo = null, string? areaPath = null, string? iterationPath = null, string? createdSince = null, string? changedSince = null, int top = 25, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<QueryCommand>().ExecuteAsync(searchText, title, description, type, state, assignedTo, areaPath, iterationPath, createdSince, changedSince, top, output, ct);

    /// <summary>Change the state of the active work item by name.</summary>
    /// <param name="name">Target state name (e.g., Active, Resolved, Closed).</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="id">Work item ID to target; omit to use the active work item.</param>
    public async Task<int> State([Argument] string name, string output = OutputFormatterFactory.DefaultFormat, int? id = null, CancellationToken ct = default)
        => await services.GetRequiredService<StateCommand>().ExecuteAsync(name, id, output, ct);

    /// <summary>List available workflow states for the active work item's type.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> States(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<StatesCommand>().ExecuteAsync(output, ct);

    /// <summary>Create a new work item in ADO.</summary>
    /// <param name="title">Title for the new work item.</param>
    /// <param name="type">Work item type (e.g., Task, Bug, User Story).</param>
    /// <param name="area">Area path for the new work item.</param>
    /// <param name="iteration">Iteration path for the new work item.</param>
    /// <param name="description">Description text for the new work item.</param>
    /// <param name="parent">Parent work item ID to link under.</param>
    /// <param name="set">Set the new work item as the active context after creation.</param>
    /// <param name="editor">Open an external editor to fill in fields.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> New(string? title = null, string? type = null, string? area = null, string? iteration = null, string? description = null, int? parent = null, bool set = false, bool editor = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default, params string[] titleParts)
    {
        var resolvedTitle = JoinTrailingText(title, titleParts);
        return await services.GetRequiredService<NewCommand>().ExecuteAsync(resolvedTitle, type, area, iteration, description, parent, set, editor, output, ct);
    }

    /// <summary>Display the work item tree hierarchy.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="depth">Maximum tree depth to display.</param>
    /// <param name="all">Show all items in the hierarchy, not just the active subtree.</param>
    /// <param name="noLive">Disable live-refresh and render a static snapshot.</param>
    /// <param name="noRefresh">Skip the sync and show cached data only.</param>
    /// <param name="id">Work item ID to target; omit to use the active work item.</param>
    public async Task<int> Tree(string output = OutputFormatterFactory.DefaultFormat, int? depth = null, bool all = false, bool noLive = false, bool noRefresh = false, int? id = null, CancellationToken ct = default)
        => await services.GetRequiredService<TreeCommand>().ExecuteAsync(id, output, depth, all, noLive, noRefresh, ct);

    /// <summary>Navigate to the parent work item.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("nav up")]
    public async Task<int> NavUp(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().UpAsync(output, ct);

    /// <summary>Launch the interactive tree navigator.</summary>
    [Command("nav")]
    public async Task<int> Nav(CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().InteractiveAsync(ct);

    /// <summary>Navigate to a child work item.</summary>
    /// <param name="idOrPattern">Child work item ID or title substring to match.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("nav down")]
    public async Task<int> NavDown([Argument] string? idOrPattern = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().DownAsync(idOrPattern, output, ct);

    /// <summary>Navigate to the next sibling work item.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("nav next")]
    public async Task<int> NavNext(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().NextAsync(output, ct);

    /// <summary>Navigate to the previous sibling work item.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("nav prev")]
    public async Task<int> NavPrev(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().PrevAsync(output, ct);

    /// <summary>Navigate backward in navigation history.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("nav back")]
    public async Task<int> NavBack(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationHistoryCommands>().BackAsync(output, ct);

    /// <summary>Navigate forward in navigation history.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("nav fore")]
    public async Task<int> NavFore(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationHistoryCommands>().ForeAsync(output, ct);

    /// <summary>Display the navigation history.</summary>
    /// <param name="nonInteractive">Skip interactive selection and print the history list.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("nav history")]
    public async Task<int> NavHistory(bool nonInteractive = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationHistoryCommands>().HistoryAsync(nonInteractive, output, ct);

    // ── Backward-compat aliases (bare up/down/next/prev/back/fore/history) ─

    /// <summary>Navigate to the parent work item (alias for nav up).</summary>
    [Hidden]
    public async Task<int> Up(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().UpAsync(output, ct);

    /// <summary>Navigate to a child work item (alias for nav down).</summary>
    [Hidden]
    public async Task<int> Down([Argument] string? idOrPattern = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().DownAsync(idOrPattern, output, ct);

    /// <summary>Navigate to the next sibling work item (alias for nav next).</summary>
    [Hidden]
    public async Task<int> Next(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().NextAsync(output, ct);

    /// <summary>Navigate to the previous sibling work item (alias for nav prev).</summary>
    [Hidden]
    public async Task<int> Prev(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().PrevAsync(output, ct);

    /// <summary>Navigate backward in navigation history (alias for nav back).</summary>
    [Hidden]
    public async Task<int> Back(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationHistoryCommands>().BackAsync(output, ct);

    /// <summary>Navigate forward in navigation history (alias for nav fore).</summary>
    [Hidden]
    public async Task<int> Fore(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationHistoryCommands>().ForeAsync(output, ct);

    /// <summary>Display the navigation history (alias for nav history).</summary>
    [Hidden]
    public async Task<int> History(bool nonInteractive = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationHistoryCommands>().HistoryAsync(nonInteractive, output, ct);

    /// <summary>Open the active work item in the browser.</summary>
    /// <param name="id">Work item ID to open; defaults to the active item.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Web([Argument] int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<WebCommand>().ExecuteAsync(id, output, ct);

    /// <summary>Create a new child work item under the active item (backward compat shortcut).</summary>
    [Hidden]
    public async Task<int> Seed([Argument] string title, string? type = null, bool editor = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedNewCommand>().ExecuteAsync(title, type, editor, output, ct);

    /// <summary>Create a new local seed work item.</summary>
    /// <param name="title">Title for the new seed work item.</param>
    /// <param name="type">Work item type for the seed (e.g., Task, Bug).</param>
    /// <param name="editor">Open an external editor to fill in seed fields.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("seed new")]
    public async Task<int> SeedNew(string? title = null, string? type = null, bool editor = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedNewCommand>().ExecuteAsync(title, type, editor, output, ct);

    /// <summary>Edit seed fields in an external editor.</summary>
    /// <param name="id">Seed ID to edit.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("seed edit")]
    public async Task<int> SeedEdit([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedEditCommand>().ExecuteAsync(id, output, ct);

    /// <summary>Discard (delete) a local seed.</summary>
    /// <param name="id">Seed ID to discard.</param>
    /// <param name="yes">Skip confirmation prompt.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("seed discard")]
    public async Task<int> SeedDiscard([Argument] int id, bool yes = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedDiscardCommand>().ExecuteAsync(id, yes, output, ct);

    /// <summary>Show seed dashboard grouped by parent.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("seed view")]
    public async Task<int> SeedView(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedViewCommand>().ExecuteAsync(output, ct);

    /// <summary>Create a virtual link between two items (at least one must be a seed).</summary>
    /// <param name="sourceId">Source item ID for the link.</param>
    /// <param name="targetId">Target item ID for the link.</param>
    /// <param name="type">Link type (e.g., Related, Dependency).</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("seed link")]
    public async Task<int> SeedLink([Argument] int sourceId, [Argument] int targetId, string? type = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedLinkCommand>().LinkAsync(sourceId, targetId, type, output, ct);

    /// <summary>Remove a virtual link between two items.</summary>
    /// <param name="sourceId">Source item ID of the link to remove.</param>
    /// <param name="targetId">Target item ID of the link to remove.</param>
    /// <param name="type">Link type to remove (e.g., Related, Dependency).</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("seed unlink")]
    public async Task<int> SeedUnlink([Argument] int sourceId, [Argument] int targetId, string? type = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedLinkCommand>().UnlinkAsync(sourceId, targetId, type, output, ct);

    /// <summary>List virtual links, optionally filtered by item ID.</summary>
    /// <param name="id">Item ID to filter links for; omit to show all links.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("seed links")]
    public async Task<int> SeedLinks([Argument] int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedLinkCommand>().ListLinksAsync(id, output, ct);

    /// <summary>Create a chain of linked seeds — interactively or from explicit titles.</summary>
    /// <param name="parent">Parent work item ID to link the chain under.</param>
    /// <param name="type">Work item type for each seed in the chain.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="titles">One or more seed titles to create in order.</param>
    [Command("seed chain")]
    public async Task<int> SeedChain(int? parent = null, string? type = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default, params string[] titles)
        => await services.GetRequiredService<SeedChainCommand>().ExecuteAsync(parent, type, output, ct, titles);

    /// <summary>Validate seeds against publish rules.</summary>
    /// <param name="id">Seed ID to validate; omit to validate all seeds.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("seed validate")]
    public async Task<int> SeedValidate([Argument] int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedValidateCommand>().ExecuteAsync(id, output, ct);

    /// <summary>Publish seeds to Azure DevOps. Use --all for batch, --dry-run to preview, --force to skip validation, --link-branch to link to a git branch.</summary>
    /// <param name="id">Seed ID to publish; omit when using --all.</param>
    /// <param name="all">Publish all seeds in dependency order.</param>
    /// <param name="force">Skip validation before publishing.</param>
    /// <param name="dryRun">Preview what would be published without making changes.</param>
    /// <param name="linkBranch">Link published work items to this branch name (e.g. feature/my-branch). Creates an ADO artifact link to the branch ref.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("seed publish")]
    public async Task<int> SeedPublish([Argument] int? id = null, bool all = false, bool force = false, bool dryRun = false, string? linkBranch = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedPublishCommand>().ExecuteAsync(id, all, force, dryRun, output, linkBranch, ct);

    /// <summary>Reconcile stale seed links and parent references after partial publishes.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("seed reconcile")]
    public async Task<int> SeedReconcile(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedReconcileCommand>().ExecuteAsync(output, ct);

    // ── Link commands (published work items) ────────────────────────

    /// <summary>Set the parent of the active work item.</summary>
    /// <param name="targetId">Work item ID to set as the parent.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("link parent")]
    public async Task<int> LinkParent([Argument] int targetId, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().ParentAsync(targetId, output, ct);

    /// <summary>Remove the parent link from the active work item.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("link unparent")]
    public async Task<int> LinkUnparent(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().UnparentAsync(output, ct);

    /// <summary>Remove the current parent and set a new one.</summary>
    /// <param name="targetId">Work item ID to set as the new parent.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("link reparent")]
    public async Task<int> LinkReparent([Argument] int targetId, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().ReparentAsync(targetId, output, ct);

    /// <summary>Add an artifact link (URL or vstfs:// URI) to a work item.</summary>
    /// <param name="url">Artifact URL (http/https) or vstfs:// URI.</param>
    /// <param name="name">Display name for the link.</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("link artifact")]
    public async Task<int> LinkArtifact([Argument] string url, string? name = null, int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ArtifactLinkCommand>().ExecuteAsync(url, name, id, output, ct);

    /// <summary>Batch state transitions, field updates, and notes in a single call.</summary>
    /// <param name="state">Target state name (e.g. Active, Closed).</param>
    /// <param name="set">Field updates as key=value pairs. Repeatable.</param>
    /// <param name="note">Comment text to add after the update.</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="ids">Comma-separated IDs for multi-item batch (e.g. 1234,5678).</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="format">Convert --set values before sending. Supported: "markdown".</param>
    [Command("batch")]
    public async Task<int> Batch(string? state = null, string[]? set = null, string? note = null, int? id = null, string? ids = null, string output = OutputFormatterFactory.DefaultFormat, string? format = null, CancellationToken ct = default)
        => await services.GetRequiredService<BatchCommand>().ExecuteAsync(state, set, note, id, ids, output, format, ct);

    /// <summary>Add a note to the active work item.</summary>
    /// <param name="text">Note text to add; omit to open an editor.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="id">Work item ID to target; omit to use the active work item.</param>
    public async Task<int> Note(string? text = null, string output = OutputFormatterFactory.DefaultFormat, int? id = null, CancellationToken ct = default, params string[] textParts)
        => await services.GetRequiredService<NoteCommand>().ExecuteAsync(JoinTrailingText(text, textParts), id, output, ct);

    /// <summary>Update a field on the active work item.</summary>
    /// <param name="field">ADO field name or alias to update (e.g., System.Title, title).</param>
    /// <param name="value">New value for the field; omit when using --file or --stdin.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="format">Convert the input value before sending to ADO. Supported: "markdown" (converts Markdown to HTML). Distinct from --output, which controls display format.</param>
    /// <param name="file">Read the field value from a file instead of an inline argument.</param>
    /// <param name="stdin">Read the field value from piped standard input.</param>
    /// <param name="id">Work item ID to update; omit to use the active work item.</param>
    /// <param name="append">Append the value to the existing field content instead of replacing it.</param>
    public async Task<int> Update([Argument] string field, [Argument] string? value = null, string output = OutputFormatterFactory.DefaultFormat, string? format = null, string? file = null, bool stdin = false, int? id = null, bool append = false, CancellationToken ct = default)
        => await services.GetRequiredService<UpdateCommand>().ExecuteAsync(field, value, output, format, file, stdin, id, append, ct);

    /// <summary>Atomically patch multiple fields on a work item via JSON input.</summary>
    /// <param name="json">JSON object with field name → value pairs (e.g., '{"System.Title":"New title"}').</param>
    /// <param name="stdin">Read JSON from standard input instead of --json.</param>
    /// <param name="format">Convert values before sending. Supported: "markdown" (converts Markdown to HTML).</param>
    /// <param name="id">Work item ID to target; omit to use the active work item.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Patch(string? json = null, bool stdin = false, string? format = null, int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<PatchCommand>().ExecuteAsync(json, stdin, id, output, format, ct);

    /// <summary>Edit work item fields in an external editor.</summary>
    /// <param name="field">Specific field to edit; omit to edit all editable fields.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Edit(string? field = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<EditCommand>().ExecuteAsync(field, output, ct);

    /// <summary>Discard pending changes for a single item or all dirty items.</summary>
    /// <param name="id">Work item ID to discard changes for.</param>
    /// <param name="all">Discard all pending changes (excludes seeds).</param>
    /// <param name="yes">Skip confirmation prompt.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Discard([Argument] int? id = null, bool all = false, bool yes = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<DiscardCommand>().ExecuteAsync(id, all, yes, output, ct);

    /// <summary>Permanently delete a work item from Azure DevOps. This is irreversible — consider 'twig state Closed' instead.</summary>
    /// <param name="id">Work item ID to delete (required).</param>
    /// <param name="force">Skip the interactive confirmation prompt.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("delete")]
    public async Task<int> Delete([Argument] int id, bool force = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<DeleteCommand>().ExecuteAsync(id, force, output, ct);

    /// <summary>Push pending changes to Azure DevOps. Deprecated — use 'twig sync' instead.</summary>
    [Hidden]
    public async Task<int> Save([Argument] int? id = null, bool all = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        await Console.Error.WriteLineAsync("hint: 'twig save' is deprecated. Use 'twig sync' instead.");
        return await services.GetRequiredService<SaveCommand>().ExecuteAsync(id, all, output, ct: ct);
    }


    /// <summary>Flush pending changes then refresh the local cache.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="force">Force a full refresh even if the cache is current.</param>
    public async Task<int> Sync(string output = OutputFormatterFactory.DefaultFormat, bool force = false, CancellationToken ct = default)
        => await services.GetRequiredService<SyncCommand>().ExecuteAsync(output, force, ct);

    /// <summary>Refresh the local cache from Azure DevOps. Deprecated — use 'twig sync' instead.</summary>
    [Hidden]
    public async Task<int> Refresh(string output = OutputFormatterFactory.DefaultFormat, bool force = false, CancellationToken ct = default)
    {
        await Console.Error.WriteLineAsync("hint: 'twig refresh' is deprecated. Use 'twig sync' instead.");
        return await services.GetRequiredService<RefreshCommand>().ExecuteAsync(output, force, ct);
    }

    /// <summary>Show the current workspace.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="all">Show all team members' items, not just yours.</param>
    /// <param name="noLive">Disable live-refresh and render a static snapshot.</param>
    /// <param name="noRefresh">Skip the sync and show cached data only.</param>
    /// <param name="flat">Use flat (non-tree) output instead of hierarchical rendering.</param>
    public async Task<int> Workspace(string output = OutputFormatterFactory.DefaultFormat, bool all = false, bool noLive = false, bool noRefresh = false, bool flat = false, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, noLive, noRefresh, ct, flat: flat);

    /// <summary>Show the current workspace (short alias).</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="all">Show all team members' items, not just yours.</param>
    /// <param name="noLive">Disable live-refresh and render a static snapshot.</param>
    /// <param name="noRefresh">Skip the sync and show cached data only.</param>
    /// <param name="flat">Use flat (non-tree) output instead of hierarchical rendering.</param>
    public async Task<int> Ws(string output = OutputFormatterFactory.DefaultFormat, bool all = false, bool noLive = false, bool noRefresh = false, bool flat = false, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, noLive, noRefresh, ct, flat: flat);

    /// <summary>Track a single work item by ID (pinned to workspace).</summary>
    /// <param name="id">Work item ID to track.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("workspace track")]
    public async Task<int> WorkspaceTrack([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<TrackingCommand>().TrackAsync(id, output, ct);

    /// <summary>Track a work item and its subtree.</summary>
    /// <param name="id">Work item ID to track (with descendants).</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("workspace track-tree")]
    public async Task<int> WorkspaceTrackTree([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<TrackingCommand>().TrackTreeAsync(id, output, ct);

    /// <summary>Remove a work item from tracking.</summary>
    /// <param name="id">Work item ID to stop tracking.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("workspace untrack")]
    public async Task<int> WorkspaceUntrack([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<TrackingCommand>().UntrackAsync(id, output, ct);

    /// <summary>Exclude a work item from workspace view.</summary>
    /// <param name="id">Work item ID to exclude.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("workspace exclude")]
    public async Task<int> WorkspaceExclude([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<TrackingCommand>().ExcludeAsync(id, output, ct);

    /// <summary>List all excluded work items.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="clear">Remove all exclusions.</param>
    /// <param name="remove">Remove a specific exclusion by work item ID.</param>
    [Command("workspace exclusions")]
    public async Task<int> WorkspaceExclusions(string output = OutputFormatterFactory.DefaultFormat, bool clear = false, int? remove = null, CancellationToken ct = default)
        => await services.GetRequiredService<TrackingCommand>().ExclusionsAsync(output, clear, remove, ct);

    // ── Workspace Area Path Management ──

    /// <summary>Show the area-filtered workspace view.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("workspace area")]
    public async Task<int> WorkspaceArea(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<AreaCommand>().ViewAsync(output, ct);

    /// <summary>Add an area path to workspace configuration.</summary>
    /// <param name="path">Area path to add (e.g., "Project\Team A").</param>
    /// <param name="exact">Use exact match semantics instead of subtree (under).</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("workspace area add")]
    public async Task<int> WorkspaceAreaAdd([Argument] string path, bool exact = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<AreaCommand>().AddAsync(path, exact, output, ct);

    /// <summary>Remove an area path from workspace configuration.</summary>
    /// <param name="path">Area path to remove.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("workspace area remove")]
    public async Task<int> WorkspaceAreaRemove([Argument] string path, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<AreaCommand>().RemoveAsync(path, output, ct);

    /// <summary>List configured area paths with match semantics.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("workspace area list")]
    public async Task<int> WorkspaceAreaList(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<AreaCommand>().ListAsync(output, ct);

    /// <summary>Fetch team area paths from ADO and replace configuration.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("workspace area sync")]
    public async Task<int> WorkspaceAreaSync(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<AreaCommand>().SyncAsync(output, ct);

    // ── Workspace Sprint Iteration Management ──

    /// <summary>Add a sprint iteration expression to workspace configuration.</summary>
    /// <param name="expression">Sprint expression (e.g., "@current", "@current-1", "Project\Sprint 5").</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("workspace sprint add")]
    public async Task<int> WorkspaceSprintAdd([Argument] string expression, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SprintCommand>().AddAsync(expression, output, ct);

    /// <summary>Remove a sprint iteration expression from workspace configuration.</summary>
    /// <param name="expression">Sprint expression to remove.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("workspace sprint remove")]
    public async Task<int> WorkspaceSprintRemove([Argument] string expression, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SprintCommand>().RemoveAsync(expression, output, ct);

    /// <summary>List configured sprint iteration expressions.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("workspace sprint list")]
    public async Task<int> WorkspaceSprintList(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SprintCommand>().ListAsync(output, ct);

    // ── Area Path Management (deprecated aliases — use 'workspace area' instead) ──

    /// <summary>Show the area-filtered workspace view. Deprecated — use 'twig workspace area' instead.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Hidden]
    [Command("area")]
    public async Task<int> Area(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        await Console.Error.WriteLineAsync("hint: 'twig area' is deprecated. Use 'twig workspace area' instead.");
        return await services.GetRequiredService<AreaCommand>().ViewAsync(output, ct);
    }

    /// <summary>Add an area path. Deprecated — use 'twig workspace area add' instead.</summary>
    /// <param name="path">Area path to add (e.g., "Project\Team A").</param>
    /// <param name="exact">Use exact match semantics instead of subtree (under).</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Hidden]
    [Command("area add")]
    public async Task<int> AreaAdd([Argument] string path, bool exact = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        await Console.Error.WriteLineAsync("hint: 'twig area add' is deprecated. Use 'twig workspace area add' instead.");
        return await services.GetRequiredService<AreaCommand>().AddAsync(path, exact, output, ct);
    }

    /// <summary>Remove an area path. Deprecated — use 'twig workspace area remove' instead.</summary>
    /// <param name="path">Area path to remove.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Hidden]
    [Command("area remove")]
    public async Task<int> AreaRemove([Argument] string path, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        await Console.Error.WriteLineAsync("hint: 'twig area remove' is deprecated. Use 'twig workspace area remove' instead.");
        return await services.GetRequiredService<AreaCommand>().RemoveAsync(path, output, ct);
    }

    /// <summary>List configured area paths. Deprecated — use 'twig workspace area list' instead.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Hidden]
    [Command("area list")]
    public async Task<int> AreaList(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        await Console.Error.WriteLineAsync("hint: 'twig area list' is deprecated. Use 'twig workspace area list' instead.");
        return await services.GetRequiredService<AreaCommand>().ListAsync(output, ct);
    }

    /// <summary>Fetch team area paths from ADO. Deprecated — use 'twig workspace area sync' instead.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Hidden]
    [Command("area sync")]
    public async Task<int> AreaSync(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        await Console.Error.WriteLineAsync("hint: 'twig area sync' is deprecated. Use 'twig workspace area sync' instead.");
        return await services.GetRequiredService<AreaCommand>().SyncAsync(output, ct);
    }

    /// <summary>Show sprint items, grouped by assignee. Defaults to your items; use --all for the full team.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="all">Show all team members' items, not just yours.</param>
    /// <param name="noRefresh">Skip the sync and show cached data only.</param>
    /// <param name="flat">Use flat (non-tree) output instead of hierarchical rendering.</param>
    public async Task<int> Sprint(string output = OutputFormatterFactory.DefaultFormat, bool all = false, bool noRefresh = false, bool flat = false, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, noRefresh: noRefresh, ct: ct, sprintLayout: true, flat: flat);

    /// <summary>Read or set a configuration value.</summary>
    /// <param name="key">Configuration key to read or set (e.g., git.project, ado.pat).</param>
    /// <param name="value">Value to set; omit to read the current value.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("config")]
    public async Task<int> Config([Argument] string key, [Argument] string? value = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConfigCommand>().ExecuteAsync(key, value, output, ct);

    /// <summary>Configure which fields appear in status view.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("config status-fields")]
    public async Task<int> ConfigStatusFields(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConfigStatusFieldsCommand>().ExecuteAsync(output, ct);

    /// <summary>Show the current version.</summary>
    public Task<int> Version()
    {
        Console.WriteLine(VersionHelper.GetVersion());
        return Task.FromResult(0);
    }

    /// <summary>Check for and apply updates from GitHub Releases.</summary>
    public async Task<int> Upgrade(CancellationToken ct = default)
        => await services.GetRequiredService<SelfUpdateCommand>().ExecuteAsync(ct);

    /// <summary>Display recent release notes from GitHub Releases.</summary>
    /// <param name="count">Number of releases to display.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Changelog(int count = 5, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ChangelogCommand>().ExecuteAsync(count, output, ct);

    /// <summary>Launch the full-screen TUI mode (requires twig-tui binary).</summary>
    public Task<int> Tui()
    {
        return Task.FromResult(BinaryLauncher.Launch("twig-tui", "Twig.Tui"));
    }

    /// <summary>Launch the MCP server (requires twig-mcp binary).</summary>
    public Task<int> Mcp()
    {
        return Task.FromResult(BinaryLauncher.Launch("twig-mcp", "Twig.Mcp"));
    }
}

/// <summary>
/// Shared version resolution helper. Reads from AssemblyInformationalVersionAttribute,
/// strips build metadata (e.g. "+sha"), falls back to "0.0.0".
/// </summary>
internal static class VersionHelper
{
    internal static string GetVersion()
    {
        var version = typeof(TwigCommands).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute attr]
            ? attr.InformationalVersion
            : "0.0.0";
        var plusIndex = version.IndexOf('+');
        if (plusIndex >= 0) version = version[..plusIndex];
        return version;
    }
}

/// <summary>
/// Pre-DI first-run companion check. Detects missing companion binaries after an
/// upgrade and installs them from the matching GitHub release archive. Failures are
/// silently swallowed so the check never blocks normal CLI startup.
/// </summary>
internal static class CompanionStartup
{
    /// <summary>
    /// Runs the companion first-run check with manually-wired dependencies.
    /// Safe to call synchronously — no <see cref="System.Threading.SynchronizationContext"/>
    /// exists before <c>ConsoleApp.Create()</c>.
    /// </summary>
    internal static void RunFirstRunCheck()
    {
        try
        {
            RunFirstRunCheckCore(
                Environment.ProcessPath,
                VersionHelper.GetVersion(),
                new DefaultFileSystem());
        }
        catch (Exception)
        {
            // First-run check must never block CLI startup.
        }
    }

    /// <summary>
    /// Testable core: accepts injectable dependencies for file I/O and process path.
    /// </summary>
    internal static void RunFirstRunCheckCore(
        string? processPath,
        string currentVersion,
        IFileSystem fileSystem)
    {
        using var httpClient = NetworkServiceModule.CreateHttpClient();
        var repoSlug = ResolveRepoSlug();
        var releaseService = new GitHubReleaseClient(httpClient, repoSlug);
        var selfUpdater = new SelfUpdater(httpClient);
        var firstRunCheck = new CompanionFirstRunCheck(releaseService, selfUpdater, fileSystem);

        // Pre-ConsoleApp.Create(): no SynchronizationContext exists, blocking is safe.
        firstRunCheck.EnsureCompanionsAsync(processPath, currentVersion)
            .GetAwaiter().GetResult();
    }

    // Duplicates the lookup in CommandRegistrationModule.AddSelfUpdateCommands() intentionally.
    internal static string ResolveRepoSlug() =>
        typeof(TwigCommands).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GitHubRepo")?.Value
        ?? "PolyphonyRequiem/twig";
}

/// <summary>
/// Grouped help output for root <c>twig</c> invocation, replacing the flat
/// alphabetical list generated by ConsoleAppFramework.
/// </summary>
internal static class GroupedHelp
{
    /// <summary>
    /// Manually-maintained set of every CLI command name that twig accepts.
    /// Includes top-level commands, compound sub-commands, group prefixes,
    /// hidden backward-compat aliases, and the <c>help</c> pseudo-command.
    /// Validated by the completeness test (T-1523-4) to prevent drift.
    /// </summary>
    public static HashSet<string> KnownCommands { get; } =
    [
        // Getting Started
        "init",
        "sync",

        // Views
        "tree",
        "sprint",

        // Workspace
        "workspace",
        "ws",
        "workspace track",
        "workspace track-tree",
        "workspace untrack",
        "workspace exclude",
        "workspace exclusions",
        "workspace area",
        "workspace area add",
        "workspace area remove",
        "workspace area list",
        "workspace area sync",
        "workspace sprint add",
        "workspace sprint remove",
        "workspace sprint list",

        // Context
        "set",
        "show",
        "show-batch",
        "query",
        "web",

        // Navigation
        "nav",
        "nav up",
        "nav down",
        "nav next",
        "nav prev",
        "nav back",
        "nav fore",
        "nav history",

        // Work Items
        "state",
        "states",
        "batch",
        "note",
        "update",
        "patch",
        "edit",
        "new",
        "delete",
        "discard",
        "link parent",
        "link unparent",
        "link reparent",
        "link artifact",

        // Seeds
        "seed new",
        "seed edit",
        "seed discard",
        "seed view",
        "seed link",
        "seed unlink",
        "seed links",
        "seed chain",
        "seed validate",
        "seed publish",
        "seed reconcile",

        // System
        "config",
        "config status-fields",
        "version",
        "upgrade",
        "changelog",

        // Experimental
        "tui",
        "mcp",
        "ohmyposh",
        "ohmyposh init",

        // Pseudo-command (early-exit handles single-arg; multi-arg falls through)
        "help",

        // Hidden backward-compat aliases (still accepted by the CLI)
        "up",
        "down",
        "next",
        "prev",
        "back",
        "fore",
        "history",
        "seed",
        "refresh",
        "area",
        "area add",
        "area remove",
        "area list",
        "area sync",

        // Group prefixes for compound commands without standalone handlers
        "link",
    ];

    /// <summary>
    /// Returns <c>true</c> when <paramref name="args"/> begins with a recognized
    /// command name. All compound sub-command prefixes (e.g. <c>nav</c>, <c>seed</c>,
    /// <c>link</c>, <c>workspace</c>, <c>area</c>) are already top-level entries
    /// in <see cref="KnownCommands"/>, so checking <c>args[0]</c> is sufficient.
    /// </summary>
    public static bool IsKnownCommand(string[] args)
        => args.Length > 0 && KnownCommands.Contains(args[0]);

    internal static void ShowUnknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: '{command}'");
        Console.Error.WriteLine();
        Show();
    }

    internal static void Show()
    {
        var v = VersionHelper.GetVersion();
        Console.Write($"""
twig {v}

Usage: twig [command] [-h|--help] [--version]

Getting Started:
  init                 Initialize a new Twig workspace.
  sync                 Flush pending changes then refresh from ADO.

Views:
  tree                 Work item hierarchy (parent → active → children).
  sprint               My sprint items, grouped by assignee.  (--all for team)

Workspace:
  workspace            My sprint items.  (alias: ws)
  workspace track <id>       Pin a work item to the workspace.
  workspace track-tree <id>  Pin a work item and its subtree.
  workspace untrack <id>     Remove a pinned work item.
  workspace exclude <id>     Hide a work item from workspace view.
  workspace exclusions       List all excluded work items.  (--clear / --remove <id>)
  workspace area             Area-filtered view of work items.
  workspace area add <path>  Add an area path to workspace config.  (--exact for exact match)
  workspace area remove <path>  Remove a configured area path.
  workspace area list        List configured area paths with match semantics.
  workspace area sync        Fetch team area paths from ADO and replace config.
  workspace sprint add <expr>  Add a sprint iteration expression.
  workspace sprint remove <expr>  Remove a sprint iteration expression.
  workspace sprint list      List configured sprint expressions.

Context:
  set <id|pattern>     Set the active work item.
  show <id>            Display a work item (syncs by default; --no-refresh for cache-only).
  show-batch --batch   Display multiple work items by ID (cache-only).
  query [text]         Search work items by text, type, state, or assignee.
  web [id]             Open the active work item in the browser.

Navigation:
  nav                  Launch the interactive tree navigator.
  nav up               Navigate to the parent work item.
  nav down [pattern]   Navigate to a child work item.
  nav next             Navigate to the next sibling (by link or order).
  nav prev             Navigate to the previous sibling (by link or order).
  nav back             Navigate backward in navigation history.
  nav fore             Navigate forward in navigation history.
  nav history          Display the navigation history.

Work Items:
  state <name>         Change the state (e.g. Active, Closed).
  states               List available states for the active item's type.
  batch                Batch state, field, and note changes in one call.
  note                 Add a note to the active work item.
  update <field> <v>   Update a field on the active work item.
  patch --json '<json>'  Atomically patch multiple fields via JSON.
  edit                 Edit work item fields in an external editor.
  new                  Create a new work item.
  link parent <id>     Set the parent of the active work item.
  link unparent        Remove the parent link from the active item.
  link reparent <id>   Remove current parent and set a new one.
  link artifact <url>  Add an artifact link (URL or vstfs://) to an item.
  discard <id>         Drop pending changes for a work item.
  discard --all        Drop all pending changes (excludes seeds).
  delete <id>          ⚠ Permanently delete a work item (irreversible).
  delete <id> --force  Delete without confirmation prompt.
  sync                 Flush pending changes then refresh from ADO.

Seeds:
  seed new <title>     Create a new local seed (child work item).
  seed new --editor    Create a seed via editor with field template.
  seed edit <id>       Edit a local seed in an external editor.
  seed discard <id>    Delete a local seed (prompts for confirmation).
  seed view            Show seed dashboard grouped by parent.
  seed link <s> <t>    Create a virtual link between two items.
  seed unlink <s> <t>  Remove a virtual link between two items.
  seed links [id]      List virtual links (all or for a specific item).
  seed chain           Create a chain of linked seeds (interactive or batch).
  seed validate [id]   Validate seeds against publish rules.
  seed publish <id>    Publish a seed to Azure DevOps.
  seed publish --all   Publish all seeds in dependency order.
  seed publish --all --link-branch <name>  Publish all and link to a branch.
  seed reconcile       Repair stale links after partial publishes.

System:
  config <key> [val]   Read or set a configuration value.
  config status-fields Configure which fields appear in status view.
  version              Show the current version.
  upgrade              Check for and apply updates.
  changelog            Display recent release notes.

Experimental:
  tui                  Launch the full-screen TUI mode.
  mcp                  Launch the MCP server (stdio transport).
  ohmyposh init        Generate Oh My Posh shell hook and segment.

Run 'twig <command> --help' for detailed usage of any command.

""");
    }
}

/// <summary>
/// Locates and launches a companion binary (e.g. <c>twig-tui</c>, <c>twig-mcp</c>).
/// Searches the directory containing the running <c>twig</c> binary first, then falls back to PATH.
/// Stdin/stdout are inherited for MCP stdio transport.
/// </summary>
internal static class BinaryLauncher
{
    internal static int Launch(string binaryName, string projectName, TextWriter? stderr = null)
    {
        stderr ??= Console.Error;
        var exeName = OperatingSystem.IsWindows() ? $"{binaryName}.exe" : binaryName;

        var binaryPath = FindAdjacentBinary(exeName) ?? FindInPath(exeName);

        if (binaryPath is null)
        {
            stderr.WriteLine($"error: '{binaryName}' not found. Ensure the {projectName} project is built and on PATH or in the same directory as 'twig'.");
            return 1;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = binaryPath,
                UseShellExecute = false,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                stderr.WriteLine($"error: Failed to start '{binaryName}'.");
                return 1;
            }

            process.WaitForExit();
            Environment.Exit(process.ExitCode);
            return process.ExitCode; // unreachable, but satisfies compiler
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: Failed to launch '{binaryName}': {ex.Message}");
            return 1;
        }
    }

    private static string? FindAdjacentBinary(string exeName)
    {
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            var candidate = Path.Combine(baseDir, exeName);
            if (File.Exists(candidate))
                return candidate;
        }

        var processPath = Environment.ProcessPath;
        if (processPath is not null)
        {
            var dir = Path.GetDirectoryName(processPath);
            if (dir is not null)
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string? FindInPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar is null) return null;

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
