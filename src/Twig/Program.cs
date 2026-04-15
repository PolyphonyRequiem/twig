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

var app = ConsoleApp.Create()
    .ConfigureServices(services =>
    {
        // Core services (config, paths, SQLite persistence, repositories, stores)
        // Load config once synchronously; pass to AddTwigCoreServices to avoid double-load.
        var twigDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");
        var configPath = Path.Combine(twigDir, "config");
        var config = TwigConfiguration.Load(configPath);
        services.AddTwigCoreServices(config);

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
    // Smart landing: route to status if workspace is initialized, otherwise show help
    var twigDirCheck = Path.Combine(Directory.GetCurrentDirectory(), ".twig");
    if (Directory.Exists(twigDirCheck))
    {
        args = ["status"];
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

    /// <summary>Display a work item from cache without changing context.</summary>
    /// <param name="id">Work item ID to display.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Show([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ShowCommand>().ExecuteAsync(id, output, ct);

    /// <summary>Search and filter work items via ad-hoc WIQL queries.</summary>
    /// <param name="searchText">Free-text search across work item titles and descriptions.</param>
    /// <param name="type">Filter by work item type (e.g., Task, Bug, User Story).</param>
    /// <param name="state">Filter by work item state (e.g., Active, Closed).</param>
    /// <param name="assignedTo">Filter by assigned user (display name or email).</param>
    /// <param name="areaPath">Filter by area path (e.g., Project\Team).</param>
    /// <param name="iterationPath">Filter by iteration path (e.g., Project\Sprint 1).</param>
    /// <param name="createdSince">Show items created after this date (e.g., 2024-01-01, 7d, 2w).</param>
    /// <param name="changedSince">Show items changed after this date (e.g., 2024-01-01, 7d, 2w).</param>
    /// <param name="top">Maximum number of results to return.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Query([Argument] string? searchText = null, string? type = null, string? state = null, string? assignedTo = null, string? areaPath = null, string? iterationPath = null, string? createdSince = null, string? changedSince = null, int top = 25, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<QueryCommand>().ExecuteAsync(searchText, type, state, assignedTo, areaPath, iterationPath, createdSince, changedSince, top, output, ct);

    /// <summary>Show status of the active work item.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="noLive">Disable live-refresh and render a static snapshot.</param>
    public async Task<int> Status(string output = OutputFormatterFactory.DefaultFormat, bool noLive = false, CancellationToken ct = default)
        => await services.GetRequiredService<StatusCommand>().ExecuteAsync(output, noLive, ct);

    /// <summary>Change the state of the active work item by name.</summary>
    /// <param name="name">Target state name (e.g., Active, Resolved, Closed).</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> State([Argument] string name, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<StateCommand>().ExecuteAsync(name, output, ct);

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
    public async Task<int> New(string title, string type, string? area = null, string? iteration = null, string? description = null, int? parent = null, bool set = false, bool editor = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NewCommand>().ExecuteAsync(title, type, area, iteration, description, parent, set, editor, output, ct);

    /// <summary>Display the work item tree hierarchy.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="depth">Maximum tree depth to display.</param>
    /// <param name="all">Show all items in the hierarchy, not just the active subtree.</param>
    /// <param name="noLive">Disable live-refresh and render a static snapshot.</param>
    public async Task<int> Tree(string output = OutputFormatterFactory.DefaultFormat, int? depth = null, bool all = false, bool noLive = false, CancellationToken ct = default)
        => await services.GetRequiredService<TreeCommand>().ExecuteAsync(output, depth, all, noLive, ct);

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

    /// <summary>Publish seeds to Azure DevOps. Use --all for batch, --dry-run to preview, --force to skip validation.</summary>
    /// <param name="id">Seed ID to publish; omit when using --all.</param>
    /// <param name="all">Publish all seeds in dependency order.</param>
    /// <param name="force">Skip validation before publishing.</param>
    /// <param name="dryRun">Preview what would be published without making changes.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("seed publish")]
    public async Task<int> SeedPublish([Argument] int? id = null, bool all = false, bool force = false, bool dryRun = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedPublishCommand>().ExecuteAsync(id, all, force, dryRun, output, ct);

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

    /// <summary>Add a note to the active work item.</summary>
    /// <param name="text">Note text to add; omit to open an editor.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Note(string? text = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NoteCommand>().ExecuteAsync(text, output, ct);

    /// <summary>Update a field on the active work item.</summary>
    /// <param name="field">ADO field name or alias to update (e.g., System.Title, title).</param>
    /// <param name="value">New value for the field; omit when using --file or --stdin.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="format">Convert the input value before sending to ADO. Supported: "markdown" (converts Markdown to HTML). Distinct from --output, which controls display format.</param>
    /// <param name="file">Read the field value from a file instead of an inline argument.</param>
    /// <param name="stdin">Read the field value from piped standard input.</param>
    public async Task<int> Update([Argument] string field, [Argument] string? value = null, string output = OutputFormatterFactory.DefaultFormat, string? format = null, string? file = null, bool stdin = false, CancellationToken ct = default)
        => await services.GetRequiredService<UpdateCommand>().ExecuteAsync(field, value, output, format, file, stdin, ct);

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
    public async Task<int> Workspace(string output = OutputFormatterFactory.DefaultFormat, bool all = false, bool noLive = false, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, noLive, ct);

    /// <summary>Show the current workspace (short alias).</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="all">Show all team members' items, not just yours.</param>
    /// <param name="noLive">Disable live-refresh and render a static snapshot.</param>
    public async Task<int> Ws(string output = OutputFormatterFactory.DefaultFormat, bool all = false, bool noLive = false, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, noLive, ct);

    /// <summary>Show sprint items, grouped by assignee. Defaults to your items; use --all for the full team.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="all">Show all team members' items, not just yours.</param>
    public async Task<int> Sprint(string output = OutputFormatterFactory.DefaultFormat, bool all = false, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, ct: ct, sprintLayout: true);

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

    /// <summary>Create/checkout a branch for the active work item and optionally link it.</summary>
    /// <param name="noLink">Skip linking the branch to the work item in ADO.</param>
    /// <param name="noTransition">Skip transitioning the work item state.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Branch(bool noLink = false, bool noTransition = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<BranchCommand>().ExecuteAsync(noLink, noTransition, output, ct);

    /// <summary>Commit with a work-item-enriched message and optionally link the commit.</summary>
    /// <param name="message">Commit message; auto-generated from the work item if omitted.</param>
    /// <param name="noLink">Skip linking the commit to the work item.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    /// <param name="passthrough">Additional arguments forwarded to git commit.</param>
    public async Task<int> Commit([Argument] string? message = null, bool noLink = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default, params string[] passthrough)
        => await services.GetRequiredService<CommitCommand>().ExecuteAsync(message, noLink, passthrough, output, ct);

    /// <summary>Create an ADO pull request linked to the active work item.</summary>
    /// <param name="target">Target branch for the pull request (defaults to main).</param>
    /// <param name="title">PR title; auto-generated from the work item if omitted.</param>
    /// <param name="draft">Create the pull request as a draft.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Pr(string? target = null, string? title = null, bool draft = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<PrCommand>().ExecuteAsync(target, title, draft, output, ct);

    /// <summary>Stash changes with work item context in the stash message.</summary>
    /// <param name="message">Stash message; auto-generated with work item context if omitted.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("stash")]
    public async Task<int> Stash([Argument] string? message = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<StashCommand>().ExecuteAsync(message, output, ct);

    /// <summary>Pop the most recent stash and restore Twig context.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("stash pop")]
    public async Task<int> StashPop(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<StashCommand>().PopAsync(output, ct);

    /// <summary>Show annotated git log with work item context.</summary>
    /// <param name="count">Number of log entries to display.</param>
    /// <param name="workItem">Filter log entries by work item ID.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Log(int count = 20, int? workItem = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LogCommand>().ExecuteAsync(count, workItem, output, ct);

    /// <summary>Start working on a work item: set context, transition state, assign, create branch.</summary>
    /// <param name="idOrPattern">Work item ID or title substring to start working on.</param>
    /// <param name="noBranch">Skip creating or checking out a branch.</param>
    /// <param name="noState">Skip transitioning the work item state.</param>
    /// <param name="noAssign">Skip assigning the work item to yourself.</param>
    /// <param name="take">Assign the work item even if already assigned to someone else.</param>
    /// <param name="force">Force start even if pre-conditions are not met.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("flow-start")]
    public async Task<int> FlowStart(
        [Argument] string? idOrPattern = null,
        bool noBranch = false,
        bool noState = false,
        bool noAssign = false,
        bool take = false,
        bool force = false,
        string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<FlowStartCommand>()
            .ExecuteAsync(idOrPattern, noBranch, noState, noAssign, take, force, output, ct);

    /// <summary>Mark work as done: save work tree, transition to Resolved, offer PR.</summary>
    /// <param name="id">Work item ID; defaults to the active item.</param>
    /// <param name="noSave">Skip saving pending changes before transitioning.</param>
    /// <param name="noPr">Skip the pull request creation prompt.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("flow-done")]
    public async Task<int> FlowDone(
        [Argument] int? id = null,
        bool noSave = false,
        bool noPr = false,
        string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<FlowDoneCommand>()
            .ExecuteAsync(id, noSave, noPr, output, ct);

    /// <summary>Close a work item: guard, transition to Completed, delete branch, clear context.</summary>
    /// <param name="id">Work item ID; defaults to the active item.</param>
    /// <param name="force">Force close even if guard conditions are not met.</param>
    /// <param name="noBranchCleanup">Skip deleting the feature branch.</param>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("flow-close")]
    public async Task<int> FlowClose(
        [Argument] int? id = null,
        bool force = false,
        bool noBranchCleanup = false,
        string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<FlowCloseCommand>()
            .ExecuteAsync(id, force, noBranchCleanup, output, ct);

    /// <summary>Install Twig-managed git hooks.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("hooks install")]
    public async Task<int> HooksInstall(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<HooksCommand>().InstallAsync(output, ct);

    /// <summary>Uninstall Twig-managed git hooks.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    [Command("hooks uninstall")]
    public async Task<int> HooksUninstall(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<HooksCommand>().UninstallAsync(output, ct);

    /// <summary>Show git context: branch, work item, and PR linkage.</summary>
    /// <param name="output">-o, Output format: human, json, jsonc, minimal.</param>
    public async Task<int> Context(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<GitContextCommand>().ExecuteAsync(output, ct);

    /// <summary>Internal hook handler invoked by git hook scripts.</summary>
    [Hidden]
    [Command("_hook")]
    public async Task<int> Hook([Argument] string hookName, CancellationToken ct = default, params string[] args)
        => await services.GetRequiredService<HookHandlerCommand>().ExecuteAsync(hookName, args, ct);

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
        "status",
        "tree",
        "workspace",
        "ws",
        "sprint",

        // Context
        "set",
        "show",
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
        "note",
        "update",
        "edit",
        "new",
        "discard",
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
        "link parent",
        "link unparent",
        "link reparent",

        // Git
        "branch",
        "commit",
        "pr",
        "stash",
        "stash pop",
        "log",
        "context",
        "hooks install",
        "hooks uninstall",

        // Workflow
        "flow-start",
        "flow-done",
        "flow-close",

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
        "save",
        "refresh",
        "_hook",

        // Group prefixes for compound commands without standalone handlers
        "link",
        "hooks",
    ];

    /// <summary>
    /// Returns <c>true</c> when <paramref name="args"/> begins with a recognized
    /// command name. All compound sub-command prefixes (e.g. <c>nav</c>, <c>seed</c>,
    /// <c>link</c>, <c>hooks</c>) are already top-level entries in <see cref="KnownCommands"/>,
    /// so checking <c>args[0]</c> is sufficient.
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
  status               Active item detail and pending changes.
  tree                 Work item hierarchy (parent → active → children).
  workspace            My sprint items.  (alias: ws)
  sprint               My sprint items, grouped by assignee.  (--all for team)

Context:
  set <id|pattern>     Set the active work item.
  show <id>            Display a work item from cache (read-only).
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
  note                 Add a note to the active work item.
  update <field> <v>   Update a field on the active work item.
  edit                 Edit work item fields in an external editor.
  new                  Create a new work item.
  link parent <id>     Set the parent of the active work item.
  link unparent        Remove the parent link from the active item.
  link reparent <id>   Remove current parent and set a new one.
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
  seed reconcile       Repair stale links after partial publishes.
  discard <id>         Drop pending changes for a work item.
  discard --all        Drop all pending changes (excludes seeds).
  sync                 Flush pending changes then refresh from ADO.

Git:
  branch               Create/checkout a branch and link it.
  commit [message]     Commit with a work-item-enriched message.
  pr                   Create an ADO pull request.
  stash [message]      Stash changes with work item context.
  stash pop            Pop the most recent stash and restore context.
  log                  Show annotated git log.
  context              Show branch, work item, and PR linkage.
  hooks install        Install Twig-managed git hooks.
  hooks uninstall      Uninstall Twig-managed git hooks.

Workflow:
  flow-start [id]      Start: set context, transition, assign, branch.
  flow-done [id]       Done: save, transition to Resolved, offer PR.
  flow-close [id]      Close: transition to Completed, clean up.

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
