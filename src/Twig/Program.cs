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

app.Run(args);

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
                stderr.WriteLine("Transition not allowed. Run 'twig refresh' to update process configuration.");
            }
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-006: 412 — Concurrency conflict (revision mismatch)
        if (ex is Twig.Infrastructure.Ado.Exceptions.AdoConflictException)
        {
            stderr.WriteLine("error: Concurrency conflict (revision mismatch).");
            stderr.WriteLine("hint: Another change is being processed. Run 'twig refresh' and retry.");
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
    public async Task<int> Init(string org, string project, string? team = null, string? gitProject = null, bool force = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<InitCommand>().ExecuteAsync(org, project, team, gitProject, force, output, ct);

    /// <summary>Set the active work item by ID or title pattern.</summary>
    public async Task<int> Set([Argument] string idOrPattern, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SetCommand>().ExecuteAsync(idOrPattern, output, ct);

    /// <summary>Show status of the active work item.</summary>
    public async Task<int> Status(string output = OutputFormatterFactory.DefaultFormat, bool noLive = false, CancellationToken ct = default)
        => await services.GetRequiredService<StatusCommand>().ExecuteAsync(output, noLive, ct);

    /// <summary>Change the state of the active work item by name.</summary>
    public async Task<int> State([Argument] string name, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<StateCommand>().ExecuteAsync(name, output, ct);

    /// <summary>Create a new top-level work item in ADO (no parent required).</summary>
    public async Task<int> New(string title, string type, string? area = null, string? iteration = null, string? description = null, bool set = false, bool editor = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NewCommand>().ExecuteAsync(title, type, area, iteration, description, set, editor, output, ct);

    /// <summary>Display the work item tree hierarchy.</summary>
    public async Task<int> Tree(string output = OutputFormatterFactory.DefaultFormat, int? depth = null, bool all = false, bool noLive = false, CancellationToken ct = default)
        => await services.GetRequiredService<TreeCommand>().ExecuteAsync(output, depth, all, noLive, ct);

    /// <summary>Navigate to the parent work item.</summary>
    [Command("nav up")]
    public async Task<int> NavUp(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().UpAsync(output, ct);

    /// <summary>Launch the interactive tree navigator.</summary>
    [Command("nav")]
    public async Task<int> Nav(CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().InteractiveAsync(ct);

    /// <summary>Navigate to a child work item.</summary>
    [Command("nav down")]
    public async Task<int> NavDown([Argument] string? idOrPattern = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().DownAsync(idOrPattern, output, ct);

    /// <summary>Navigate to the next sibling work item.</summary>
    [Command("nav next")]
    public async Task<int> NavNext(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().NextAsync(output, ct);

    /// <summary>Navigate to the previous sibling work item.</summary>
    [Command("nav prev")]
    public async Task<int> NavPrev(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().PrevAsync(output, ct);

    /// <summary>Navigate backward in navigation history.</summary>
    [Command("nav back")]
    public async Task<int> NavBack(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationHistoryCommands>().BackAsync(output, ct);

    /// <summary>Navigate forward in navigation history.</summary>
    [Command("nav fore")]
    public async Task<int> NavFore(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationHistoryCommands>().ForeAsync(output, ct);

    /// <summary>Display the navigation history.</summary>
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
    public async Task<int> Web([Argument] int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<WebCommand>().ExecuteAsync(id, output, ct);

    /// <summary>Create a new child work item under the active item (backward compat shortcut).</summary>
    public async Task<int> Seed([Argument] string title, string? type = null, bool editor = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedNewCommand>().ExecuteAsync(title, type, editor, output, ct);

    /// <summary>Create a new local seed work item.</summary>
    [Command("seed new")]
    public async Task<int> SeedNew(string? title = null, string? type = null, bool editor = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedNewCommand>().ExecuteAsync(title, type, editor, output, ct);

    /// <summary>Edit seed fields in an external editor.</summary>
    [Command("seed edit")]
    public async Task<int> SeedEdit([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedEditCommand>().ExecuteAsync(id, output, ct);

    /// <summary>Discard (delete) a local seed.</summary>
    [Command("seed discard")]
    public async Task<int> SeedDiscard([Argument] int id, bool yes = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedDiscardCommand>().ExecuteAsync(id, yes, output, ct);

    /// <summary>Show seed dashboard grouped by parent.</summary>
    [Command("seed view")]
    public async Task<int> SeedView(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedViewCommand>().ExecuteAsync(output, ct);

    /// <summary>Create a virtual link between two items (at least one must be a seed).</summary>
    [Command("seed link")]
    public async Task<int> SeedLink([Argument] int sourceId, [Argument] int targetId, string? type = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedLinkCommand>().LinkAsync(sourceId, targetId, type, output, ct);

    /// <summary>Remove a virtual link between two items.</summary>
    [Command("seed unlink")]
    public async Task<int> SeedUnlink([Argument] int sourceId, [Argument] int targetId, string? type = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedLinkCommand>().UnlinkAsync(sourceId, targetId, type, output, ct);

    /// <summary>List virtual links, optionally filtered by item ID.</summary>
    [Command("seed links")]
    public async Task<int> SeedLinks([Argument] int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedLinkCommand>().ListLinksAsync(id, output, ct);

    /// <summary>Interactively create a chain of linked seeds.</summary>
    [Command("seed chain")]
    public async Task<int> SeedChain(int? parent = null, string? type = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedChainCommand>().ExecuteAsync(parent, type, output, ct);

    /// <summary>Validate seeds against publish rules.</summary>
    [Command("seed validate")]
    public async Task<int> SeedValidate([Argument] int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedValidateCommand>().ExecuteAsync(id, output, ct);

    /// <summary>Publish seeds to Azure DevOps. Use --all for batch, --dry-run to preview, --force to skip validation.</summary>
    [Command("seed publish")]
    public async Task<int> SeedPublish([Argument] int? id = null, bool all = false, bool force = false, bool dryRun = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedPublishCommand>().ExecuteAsync(id, all, force, dryRun, output, ct);

    /// <summary>Reconcile stale seed links and parent references after partial publishes.</summary>
    [Command("seed reconcile")]
    public async Task<int> SeedReconcile(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedReconcileCommand>().ExecuteAsync(output, ct);

    /// <summary>Add a note to the active work item.</summary>
    public async Task<int> Note(string? text = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NoteCommand>().ExecuteAsync(text, output, ct);

    /// <summary>Update a field on the active work item.</summary>
    /// <param name="format">Convert the input value before sending to ADO. Supported: "markdown" (converts Markdown to HTML). Distinct from --output, which controls display format.</param>
    public async Task<int> Update([Argument] string field, [Argument] string value, string output = OutputFormatterFactory.DefaultFormat, string? format = null, CancellationToken ct = default)
        => await services.GetRequiredService<UpdateCommand>().ExecuteAsync(field, value, output, format, ct);

    /// <summary>Edit work item fields in an external editor.</summary>
    public async Task<int> Edit(string? field = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<EditCommand>().ExecuteAsync(field, output, ct);

    /// <summary>Push pending changes to Azure DevOps.</summary>
    public async Task<int> Save([Argument] int? id = null, bool all = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SaveCommand>().ExecuteAsync(id, all, output, ct: ct);

    /// <summary>Flush pending changes then refresh the local cache.</summary>
    public async Task<int> Sync(string output = OutputFormatterFactory.DefaultFormat, bool force = false, CancellationToken ct = default)
        => await services.GetRequiredService<SyncCommand>().ExecuteAsync(output, force, ct);

    /// <summary>Refresh the local cache from Azure DevOps.</summary>
    public async Task<int> Refresh(string output = OutputFormatterFactory.DefaultFormat, bool force = false, CancellationToken ct = default)
        => await services.GetRequiredService<RefreshCommand>().ExecuteAsync(output, force, ct);

    /// <summary>Show the current workspace.</summary>
    public async Task<int> Workspace(string output = OutputFormatterFactory.DefaultFormat, bool all = false, bool noLive = false, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, noLive, ct);

    /// <summary>Show the current workspace (short alias).</summary>
    public async Task<int> Ws(string output = OutputFormatterFactory.DefaultFormat, bool all = false, bool noLive = false, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, noLive, ct);

    /// <summary>Show sprint items, grouped by assignee. Defaults to your items; use --all for the full team.</summary>
    public async Task<int> Sprint(string output = OutputFormatterFactory.DefaultFormat, bool all = false, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, ct: ct, sprintLayout: true);

    /// <summary>Read or set a configuration value.</summary>
    [Command("config")]
    public async Task<int> Config([Argument] string key, [Argument] string? value = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConfigCommand>().ExecuteAsync(key, value, output, ct);

    /// <summary>Configure which fields appear in status view.</summary>
    [Command("config status-fields")]
    public async Task<int> ConfigStatusFields(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConfigStatusFieldsCommand>().ExecuteAsync(output, ct);

    /// <summary>Create/checkout a branch for the active work item and optionally link it.</summary>
    public async Task<int> Branch(bool noLink = false, bool noTransition = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<BranchCommand>().ExecuteAsync(noLink, noTransition, output, ct);

    /// <summary>Commit with a work-item-enriched message and optionally link the commit.</summary>
    public async Task<int> Commit([Argument] string? message = null, bool noLink = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default, params string[] passthrough)
        => await services.GetRequiredService<CommitCommand>().ExecuteAsync(message, noLink, passthrough, output, ct);

    /// <summary>Create an ADO pull request linked to the active work item.</summary>
    public async Task<int> Pr(string? target = null, string? title = null, bool draft = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<PrCommand>().ExecuteAsync(target, title, draft, output, ct);

    /// <summary>Stash changes with work item context in the stash message.</summary>
    [Command("stash")]
    public async Task<int> Stash([Argument] string? message = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<StashCommand>().ExecuteAsync(message, output, ct);

    /// <summary>Pop the most recent stash and restore Twig context.</summary>
    [Command("stash pop")]
    public async Task<int> StashPop(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<StashCommand>().PopAsync(output, ct);

    /// <summary>Show annotated git log with work item context.</summary>
    public async Task<int> Log(int count = 20, int? workItem = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LogCommand>().ExecuteAsync(count, workItem, output, ct);

    /// <summary>Start working on a work item: set context, transition state, assign, create branch.</summary>
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
    [Command("hooks install")]
    public async Task<int> HooksInstall(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<HooksCommand>().InstallAsync(output, ct);

    /// <summary>Uninstall Twig-managed git hooks.</summary>
    [Command("hooks uninstall")]
    public async Task<int> HooksUninstall(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<HooksCommand>().UninstallAsync(output, ct);

    /// <summary>Show git context: branch, work item, and PR linkage.</summary>
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
    public async Task<int> Changelog(int count = 5, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ChangelogCommand>().ExecuteAsync(count, output, ct);

    /// <summary>Launch the full-screen TUI mode (requires twig-tui binary).</summary>
    public Task<int> Tui()
    {
        return Task.FromResult(TuiLauncher.Launch());
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
    internal static void Show()
    {
        var v = VersionHelper.GetVersion();
        Console.Write($"""
twig {v}

Usage: twig [command] [-h|--help] [--version]

Getting Started:
  init                 Initialize a new Twig workspace.
  refresh              Refresh the local cache from Azure DevOps.

Views:
  status               Active item detail and pending changes.
  tree                 Work item hierarchy (parent → active → children).
  workspace            My sprint items.  (alias: ws)
  sprint               My sprint items, grouped by assignee.  (--all for team)

Context:
  set <id|pattern>     Set the active work item.
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
  seed new <title>     Create a new local seed (child work item).
  seed new --editor    Create a seed via editor with field template.
  seed edit <id>       Edit a local seed in an external editor.
  seed discard <id>    Delete a local seed (prompts for confirmation).
  seed view            Show seed dashboard grouped by parent.
  seed link <s> <t>    Create a virtual link between two items.
  seed unlink <s> <t>  Remove a virtual link between two items.
  seed links [id]      List virtual links (all or for a specific item).
  seed chain           Interactively create a chain of linked seeds.
  seed validate [id]   Validate seeds against publish rules.
  seed publish <id>    Publish a seed to Azure DevOps.
  seed publish --all   Publish all seeds in dependency order.
  seed reconcile       Repair stale links after partial publishes.
  save                 Push pending changes to Azure DevOps.
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
  ohmyposh init        Generate Oh My Posh shell hook and segment.

Run 'twig <command> --help' for detailed usage of any command.

""");
    }
}

/// <summary>
/// Locates and launches the <c>twig-tui</c> binary. Searches the adjacent directory
/// (same folder as the running <c>twig</c> binary) first, then falls back to PATH.
/// Uses <see cref="System.Diagnostics.Process.Start"/> + <see cref="System.Diagnostics.Process.WaitForExit()"/>
/// + exit code propagation — the correct pattern on Windows (no <c>exec()</c> syscall).
/// </summary>
internal static class TuiLauncher
{
    private const string TuiBinaryName = "twig-tui";

    internal static int Launch(TextWriter? stderr = null)
    {
        stderr ??= Console.Error;
        var exeName = OperatingSystem.IsWindows() ? $"{TuiBinaryName}.exe" : TuiBinaryName;

        // 1. Look in the same directory as the running twig binary
        var adjacentPath = FindAdjacentBinary(exeName);

        // 2. Fall back to PATH lookup
        var binaryPath = adjacentPath ?? FindInPath(exeName);

        if (binaryPath is null)
        {
            stderr.WriteLine($"error: '{TuiBinaryName}' not found. Ensure the Twig.Tui project is built and on PATH or in the same directory as 'twig'.");
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
                stderr.WriteLine($"error: Failed to start '{TuiBinaryName}'.");
                return 1;
            }

            process.WaitForExit();
            Environment.Exit(process.ExitCode);
            return process.ExitCode; // unreachable, but satisfies compiler
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: Failed to launch TUI: {ex.Message}");
            return 1;
        }
    }

    private static string? FindAdjacentBinary(string exeName)
    {
        // Use AppContext.BaseDirectory for AOT/single-file compatibility
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            var candidate = Path.Combine(baseDir, exeName);
            if (File.Exists(candidate))
                return candidate;
        }

        // Also check the directory of the running process
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
