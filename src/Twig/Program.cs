using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Git;
using Twig.Infrastructure.GitHub;
using Twig.Rendering;

SQLitePCL.Batteries.Init();

// Fast-path: _prompt bypasses the full DI pipeline for <50ms prompt rendering.
// Only needs TwigConfiguration + direct SQLite — no git subprocess, no service registration.
if (args.Length >= 1 && args[0] == "_prompt")
{
    try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
    var twigDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");
    var configPath = Path.Combine(twigDir, "config");
    var config = File.Exists(configPath)
        ? TwigConfiguration.LoadAsync(configPath).GetAwaiter().GetResult()
        : new TwigConfiguration();
    var promptCmd = new PromptCommand(config);
    var format = "plain";
    var maxWidth = 40;
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] is "--format" && i + 1 < args.Length) format = args[++i];
        else if (args[i] is "--max-width" && i + 1 < args.Length && int.TryParse(args[++i], out var w)) maxWidth = w;
    }
    return promptCmd.Execute(format, maxWidth);
}

// ITEM-154: Enable UTF-8 output so Unicode type badges render correctly on Windows.
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch
{
    // InvariantGlobalization or restricted environment — Unicode bytes will still be emitted; rendering depends on terminal capabilities.
}

// EPIC-005: Clean up old binary left behind from a previous Windows self-update.
SelfUpdater.CleanupOldBinary();

var app = ConsoleApp.Create()
    .ConfigureServices(services =>
    {
        // Core services (config, paths, SQLite persistence, repositories, stores)
        services.AddTwigCoreServices();

        // ITEM-138: Migrate legacy flat twig.db → nested context path.
        // LegacyDbMigrator is internal to CLI — must be called here, not in shared registration.
        // Config is loaded independently here; the singleton factory in AddTwigCoreServices()
        // will load it again on first resolution (cheap — just reads a small JSON file).
        var twigDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");
        var configPath = Path.Combine(twigDir, "config");
        var config = File.Exists(configPath)
            ? TwigConfiguration.LoadAsync(configPath).GetAwaiter().GetResult()
            : new TwigConfiguration();
        LegacyDbMigrator.MigrateIfNeeded(twigDir, config);

        // Auth provider (resolve from config)
        services.AddSingleton<IAuthenticationProvider>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            if (string.Equals(cfg.Auth.Method, "pat", StringComparison.OrdinalIgnoreCase))
                return new PatAuthProvider();
            return new AzCliAuthProvider();
        });

        // HTTP client — singleton is acceptable for short-lived CLI process.
        // IHttpClientFactory would be preferable for long-running services (DNS refresh, connection pooling).
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IAdoWorkItemService>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            return new AdoRestClient(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IAuthenticationProvider>(),
                cfg.Organization,
                cfg.Project);
        });
        // IAdoGitService — uses git-specific project (may differ from backlog project).
        // Only registered when both git project and repository can be resolved;
        // FlowDoneCommand/FlowCloseCommand accept IAdoGitService? and handle null gracefully.
        {
            var gitProject = config.GetGitProject();
            var repository = config.Git.Repository;

            // Auto-detect from git remote if not explicitly configured.
            // Detection deferred to service factory (lazy) so it does not block DI startup.
            if (string.IsNullOrWhiteSpace(repository))
            {
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
                        // Drain both streams concurrently to prevent pipe-buffer deadlock,
                        // then wait up to 2 s (local git op should complete well under that).
                        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                        var stderrTask = proc.StandardError.ReadToEndAsync();
                        System.Threading.Tasks.Task.WhenAll(stdoutTask, stderrTask)
                            .GetAwaiter().GetResult();
                        var exited = proc.WaitForExit(2000);
                        if (!exited)
                        {
                            try { proc.Kill(); } catch { }
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
            }

            if (!string.IsNullOrWhiteSpace(gitProject) && !string.IsNullOrWhiteSpace(repository))
            {
                var capturedGitProject = gitProject;
                var capturedRepository = repository;
                services.AddSingleton<IAdoGitService>(sp =>
                    new AdoGitClient(
                        sp.GetRequiredService<HttpClient>(),
                        sp.GetRequiredService<IAuthenticationProvider>(),
                        sp.GetRequiredService<TwigConfiguration>().Organization,
                        capturedGitProject,
                        capturedRepository,
                        sp.GetRequiredService<TwigConfiguration>().Project));
            }
        }
        services.AddSingleton<IIterationService>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            var team = string.IsNullOrWhiteSpace(cfg.Team) ? $"{cfg.Project} Team" : cfg.Team;
            return new AdoIterationService(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IAuthenticationProvider>(),
                cfg.Organization,
                cfg.Project,
                team);
        });

        // Editor launcher (minimal working implementation)
        services.AddSingleton<IEditorLauncher, EditorLauncher>();
        services.AddSingleton<IConsoleInput, ConsoleInput>();

        // Output formatters and factory
        services.AddSingleton<HumanOutputFormatter>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            return new HumanOutputFormatter(cfg.Display, cfg.TypeAppearances);
        });
        services.AddSingleton<JsonOutputFormatter>();
        services.AddSingleton<MinimalOutputFormatter>();
        services.AddSingleton<OutputFormatterFactory>();

        // Hint engine — reads display config at startup
        services.AddSingleton<HintEngine>(sp =>
            new HintEngine(sp.GetRequiredService<TwigConfiguration>().Display));

        // Spectre.Console rendering pipeline
        services.AddSingleton<IAnsiConsole>(AnsiConsole.Console);
        services.AddSingleton<SpectreTheme>(sp =>
        {
            var cfg = sp.GetRequiredService<TwigConfiguration>();
            IReadOnlyList<StateEntry>? stateEntries = null;
            try
            {
                var processTypeStore = sp.GetRequiredService<IProcessTypeStore>();
                var records = Task.Run(() => processTypeStore.GetAllAsync()).GetAwaiter().GetResult();
                stateEntries = records.SelectMany(r => r.States).ToList();
            }
            catch (InvalidOperationException)
            {
                // SqliteCacheStore uninitialized (e.g. twig init) — fall through with null
            }

            return new SpectreTheme(cfg.Display, cfg.TypeAppearances, stateEntries);
        });
        services.AddSingleton<IAsyncRenderer>(sp => new SpectreRenderer(
            sp.GetRequiredService<IAnsiConsole>(),
            sp.GetRequiredService<SpectreTheme>()));
        services.AddSingleton<RenderingPipelineFactory>();

        // Command services — InitCommand uses a factory to inject auth + HTTP
        // (instead of IIterationService) so it can construct an AdoIterationService
        // with the org/project args supplied at invocation time.
        services.AddSingleton<InitCommand>(sp => new InitCommand(
            sp.GetRequiredService<IAuthenticationProvider>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<TwigPaths>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>()));
        services.AddSingleton<SetCommand>();
        services.AddSingleton<StatusCommand>(sp => new StatusCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<RenderingPipelineFactory>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<StateCommand>();
        services.AddSingleton<TreeCommand>(sp => new TreeCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<RenderingPipelineFactory>()));
        services.AddSingleton<NavigationCommands>();
        services.AddSingleton<SeedCommand>();
        services.AddSingleton<NoteCommand>();
        services.AddSingleton<UpdateCommand>();
        services.AddSingleton<EditCommand>();
        services.AddSingleton<SaveCommand>(sp => new SaveCommand(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>()));
        services.AddSingleton<RefreshCommand>(sp => new RefreshCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IIterationService>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<TwigPaths>(),
            sp.GetRequiredService<IProcessTypeStore>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>()));
        services.AddSingleton<WorkspaceCommand>(sp => new WorkspaceCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IIterationService>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<IProcessTypeStore>(),
            sp.GetRequiredService<RenderingPipelineFactory>()));
        services.AddSingleton<ConfigCommand>();
        services.AddSingleton<BranchCommand>(sp => new BranchCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<CommitCommand>(sp => new CommitCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<PrCommand>(sp => new PrCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<StashCommand>(sp => new StashCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>()));
        services.AddSingleton<LogCommand>(sp => new LogCommand(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetService<IGitService>()));

        // Git hooks & context commands
        services.AddSingleton<HookInstaller>();
        services.AddSingleton<HooksCommand>(sp => new HooksCommand(
            sp.GetRequiredService<HookInstaller>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>()));
        services.AddSingleton<GitContextCommand>(sp => new GitContextCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<HookHandlerCommand>(sp => new HookHandlerCommand(
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>()));

        // Flow lifecycle commands (EPIC-004)
        services.AddSingleton<FlowStartCommand>(sp => new FlowStartCommand(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetRequiredService<RenderingPipelineFactory>(),
            sp.GetService<IGitService>(),
            sp.GetService<IIterationService>()));
        services.AddSingleton<FlowDoneCommand>(sp => new FlowDoneCommand(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetRequiredService<SaveCommand>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>()));
        services.AddSingleton<FlowCloseCommand>(sp => new FlowCloseCommand(
            sp.GetRequiredService<IWorkItemRepository>(),
            sp.GetRequiredService<IAdoWorkItemService>(),
            sp.GetRequiredService<IContextStore>(),
            sp.GetRequiredService<IPendingChangeStore>(),
            sp.GetRequiredService<IProcessConfigurationProvider>(),
            sp.GetRequiredService<IConsoleInput>(),
            sp.GetRequiredService<OutputFormatterFactory>(),
            sp.GetRequiredService<HintEngine>(),
            sp.GetRequiredService<TwigConfiguration>(),
            sp.GetService<IGitService>(),
            sp.GetService<IAdoGitService>()));

        // Self-update services (EPIC-005)
        services.AddSingleton<IGitHubReleaseService>(sp =>
        {
            var repoSlug = "PolyphonyRequiem/twig";
            var attrs = typeof(TwigCommands).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false);
            foreach (var attr in attrs)
            {
                if (attr is System.Reflection.AssemblyMetadataAttribute meta && meta.Key == "GitHubRepo" && meta.Value is not null)
                {
                    repoSlug = meta.Value;
                    break;
                }
            }
            return new GitHubReleaseClient(sp.GetRequiredService<HttpClient>(), repoSlug);
        });
        services.AddSingleton<SelfUpdater>(sp => new SelfUpdater(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<SelfUpdateCommand>();
        services.AddSingleton<ChangelogCommand>();

        // Prompt command — reads directly from SQLite, takes only TwigConfiguration
        services.AddSingleton<PromptCommand>();

        // Oh My Posh helper — generates shell hooks and text segment JSON
        services.AddSingleton<OhMyPoshCommands>();
    });

app.UseFilter<ExceptionFilter>();

app.Add<TwigCommands>();
app.Add<OhMyPoshCommands>("ohmyposh");

// Handle --version flag before ConsoleAppFramework parsing (ITEM-118)
if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine(VersionHelper.GetVersion());
    return;
}

app.Run(args);

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
    public static int Handle(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            Environment.ExitCode = 130;
            return 130;
        }

        // FM-001: Offline — ADO unreachable
        if (ex is Twig.Infrastructure.Ado.Exceptions.AdoOfflineException)
        {
            Console.Error.WriteLine("\u26a0 ADO unreachable. Operating in offline mode.");
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-002 / FM-003: Authentication errors
        if (ex is Twig.Infrastructure.Ado.Exceptions.AdoAuthenticationException authEx)
        {
            Console.Error.WriteLine($"error: {authEx.Message}");
            if (authEx.Message.Contains("PAT", StringComparison.OrdinalIgnoreCase))
                Console.Error.WriteLine("Update PAT in .twig/config or $TWIG_PAT.");
            else
                Console.Error.WriteLine("Run 'az login' to refresh.");
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-004: 404 — Work item not found
        if (ex is Twig.Infrastructure.Ado.Exceptions.AdoNotFoundException notFoundEx)
        {
            var msg = notFoundEx.WorkItemId.HasValue
                ? $"Work item #{notFoundEx.WorkItemId} not found."
                : "Resource not found.";
            Console.Error.WriteLine($"error: {msg}");
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-005: 400 — Bad request (state transition etc.)
        if (ex is Twig.Infrastructure.Ado.Exceptions.AdoBadRequestException badReqEx)
        {
            Console.Error.WriteLine($"error: {badReqEx.Message}");
            if (badReqEx.Message.Contains("transition", StringComparison.OrdinalIgnoreCase)
                || badReqEx.Message.Contains("state", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Transition not allowed. Run 'twig refresh' to update process configuration.");
            }
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-009: No editor configured
        if (ex is Twig.Commands.EditorNotFoundException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-008: Cache corruption — SqliteException directly or wrapped in InvalidOperationException
        if (ex is Microsoft.Data.Sqlite.SqliteException
            || (ex is InvalidOperationException && ex.InnerException is Microsoft.Data.Sqlite.SqliteException))
        {
            Console.Error.WriteLine("\u26a0 Cache corrupted. Run 'twig init --force' to rebuild.");
            Environment.ExitCode = 1;
            return 1;
        }

        Console.Error.WriteLine($"error: {ex.Message}");
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
    public async Task<int> Init(string org, string project, string? team = null, string? gitProject = null, bool force = false, string output = "human")
        => await services.GetRequiredService<InitCommand>().ExecuteAsync(org, project, team, gitProject, force, output);

    /// <summary>Set the active work item by ID or title pattern.</summary>
    public async Task<int> Set([Argument] string idOrPattern, string output = "human", CancellationToken ct = default)
        => await services.GetRequiredService<SetCommand>().ExecuteAsync(idOrPattern, output, ct);

    /// <summary>Show status of the active work item.</summary>
    public async Task<int> Status(string output = "human", bool noLive = false)
        => await services.GetRequiredService<StatusCommand>().ExecuteAsync(output, noLive);

    /// <summary>Change the state of the active work item (p/c/s/d/x).</summary>
    public async Task<int> State([Argument] string shorthand, string output = "human")
        => await services.GetRequiredService<StateCommand>().ExecuteAsync(shorthand, output);

    /// <summary>Display the work item tree hierarchy.</summary>
    public async Task<int> Tree(string output = "human", int? depth = null, bool all = false, bool noLive = false)
        => await services.GetRequiredService<TreeCommand>().ExecuteAsync(output, depth, all, noLive);

    /// <summary>Navigate to the parent work item.</summary>
    public async Task<int> Up(string output = "human", CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().UpAsync(output, ct);

    /// <summary>Navigate to a child work item.</summary>
    public async Task<int> Down([Argument] string idOrPattern, string output = "human", CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().DownAsync(idOrPattern, output, ct);

    /// <summary>Create a new child work item under the active item.</summary>
    public async Task<int> Seed([Argument] string title, string? type = null, string output = "human")
        => await services.GetRequiredService<SeedCommand>().ExecuteAsync(title, type, output);

    /// <summary>Add a note to the active work item.</summary>
    public async Task<int> Note(string? text = null, string output = "human")
        => await services.GetRequiredService<NoteCommand>().ExecuteAsync(text, output);

    /// <summary>Update a field on the active work item.</summary>
    public async Task<int> Update([Argument] string field, [Argument] string value, string output = "human")
        => await services.GetRequiredService<UpdateCommand>().ExecuteAsync(field, value, output);

    /// <summary>Edit work item fields in an external editor.</summary>
    public async Task<int> Edit(string? field = null, string output = "human")
        => await services.GetRequiredService<EditCommand>().ExecuteAsync(field, output);

    /// <summary>Push pending changes to Azure DevOps.</summary>
    public async Task<int> Save([Argument] int? id = null, bool all = false, string output = "human")
        => await services.GetRequiredService<SaveCommand>().ExecuteAsync(id, all, output);

    /// <summary>Refresh the local cache from Azure DevOps.</summary>
    public async Task<int> Refresh(string output = "human", bool force = false)
        => await services.GetRequiredService<RefreshCommand>().ExecuteAsync(output, force);

    /// <summary>Show the current workspace.</summary>
    public async Task<int> Workspace(string output = "human", bool all = false, bool noLive = false)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, noLive);

    /// <summary>Show the current workspace (alias).</summary>
    public async Task<int> Show(string output = "human", bool all = false, bool noLive = false)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, noLive);

    /// <summary>Show the current workspace (alias).</summary>
    public async Task<int> Ws(string output = "human", bool all = false, bool noLive = false)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, noLive);

    /// <summary>Show all team items in the current sprint, grouped by assignee.</summary>
    public async Task<int> Sprint(string output = "human")
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all: true);

    /// <summary>Read or set a configuration value.</summary>
    public async Task<int> Config([Argument] string key, [Argument] string? value = null, string output = "human")
        => await services.GetRequiredService<ConfigCommand>().ExecuteAsync(key, value, output);

    /// <summary>Create/checkout a branch for the active work item and optionally link it.</summary>
    public async Task<int> Branch(bool noLink = false, bool noTransition = false, string output = "human")
        => await services.GetRequiredService<BranchCommand>().ExecuteAsync(noLink, noTransition, output);

    /// <summary>Commit with a work-item-enriched message and optionally link the commit.</summary>
    public async Task<int> Commit([Argument] string? message = null, bool noLink = false, string output = "human", params string[] passthrough)
        => await services.GetRequiredService<CommitCommand>().ExecuteAsync(message, noLink, passthrough, output);

    /// <summary>Create an ADO pull request linked to the active work item.</summary>
    public async Task<int> Pr(string? target = null, string? title = null, bool draft = false, string output = "human")
        => await services.GetRequiredService<PrCommand>().ExecuteAsync(target, title, draft, output);

    /// <summary>Stash changes with work item context in the stash message.</summary>
    [Command("stash")]
    public async Task<int> Stash([Argument] string? message = null, string output = "human")
        => await services.GetRequiredService<StashCommand>().ExecuteAsync(message, output);

    /// <summary>Pop the most recent stash and restore Twig context.</summary>
    [Command("stash pop")]
    public async Task<int> StashPop(string output = "human")
        => await services.GetRequiredService<StashCommand>().PopAsync(output);

    /// <summary>Show annotated git log with work item context.</summary>
    public async Task<int> Log(int count = 20, int? workItem = null, string output = "human")
        => await services.GetRequiredService<LogCommand>().ExecuteAsync(count, workItem, output);

    /// <summary>Start working on a work item: set context, transition state, assign, create branch.</summary>
    [Command("flow-start")]
    public async Task<int> FlowStart(
        [Argument] string? idOrPattern = null,
        bool noBranch = false,
        bool noState = false,
        bool noAssign = false,
        bool take = false,
        bool force = false,
        string output = "human")
        => await services.GetRequiredService<FlowStartCommand>()
            .ExecuteAsync(idOrPattern, noBranch, noState, noAssign, take, force, output);

    /// <summary>Mark work as done: save work tree, transition to Resolved, offer PR.</summary>
    [Command("flow-done")]
    public async Task<int> FlowDone(
        [Argument] int? id = null,
        bool noSave = false,
        bool noPr = false,
        string output = "human")
        => await services.GetRequiredService<FlowDoneCommand>()
            .ExecuteAsync(id, noSave, noPr, output);

    /// <summary>Close a work item: guard, transition to Completed, delete branch, clear context.</summary>
    [Command("flow-close")]
    public async Task<int> FlowClose(
        [Argument] int? id = null,
        bool force = false,
        bool noBranchCleanup = false,
        string output = "human")
        => await services.GetRequiredService<FlowCloseCommand>()
            .ExecuteAsync(id, force, noBranchCleanup, output);

    /// <summary>Install Twig-managed git hooks.</summary>
    [Command("hooks install")]
    public async Task<int> HooksInstall(string output = "human")
        => await services.GetRequiredService<HooksCommand>().InstallAsync(output);

    /// <summary>Uninstall Twig-managed git hooks.</summary>
    [Command("hooks uninstall")]
    public async Task<int> HooksUninstall(string output = "human")
        => await services.GetRequiredService<HooksCommand>().UninstallAsync(output);

    /// <summary>Show git context: branch, work item, and PR linkage.</summary>
    public async Task<int> Context(string output = "human")
        => await services.GetRequiredService<GitContextCommand>().ExecuteAsync(output);

    /// <summary>Internal hook handler invoked by git hook scripts.</summary>
    [Hidden]
    [Command("_hook")]
    public async Task<int> Hook([Argument] string hookName, params string[] args)
        => await services.GetRequiredService<HookHandlerCommand>().ExecuteAsync(hookName, args);

    /// <summary>Internal prompt command: outputs compact work item summary for shell integrations.</summary>
    [Hidden]
    [Command("_prompt")]
    public Task<int> Prompt(string format = "plain", int maxWidth = 40)
        => Task.FromResult(services.GetRequiredService<PromptCommand>().Execute(format, maxWidth));

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
    public async Task<int> Changelog(int count = 5, CancellationToken ct = default)
        => await services.GetRequiredService<ChangelogCommand>().ExecuteAsync(count, ct);

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
/// Locates and launches the <c>twig-tui</c> binary. Searches the adjacent directory
/// (same folder as the running <c>twig</c> binary) first, then falls back to PATH.
/// Uses <see cref="System.Diagnostics.Process.Start"/> + <see cref="System.Diagnostics.Process.WaitForExit()"/>
/// + exit code propagation — the correct pattern on Windows (no <c>exec()</c> syscall).
/// </summary>
internal static class TuiLauncher
{
    private const string TuiBinaryName = "twig-tui";

    internal static int Launch()
    {
        var exeName = OperatingSystem.IsWindows() ? $"{TuiBinaryName}.exe" : TuiBinaryName;

        // 1. Look in the same directory as the running twig binary
        var adjacentPath = FindAdjacentBinary(exeName);

        // 2. Fall back to PATH lookup
        var binaryPath = adjacentPath ?? FindInPath(exeName);

        if (binaryPath is null)
        {
            Console.Error.WriteLine($"error: '{TuiBinaryName}' not found. Ensure the Twig.Tui project is built and on PATH or in the same directory as 'twig'.");
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
                Console.Error.WriteLine($"error: Failed to start '{TuiBinaryName}'.");
                return 1;
            }

            process.WaitForExit();
            Environment.Exit(process.ExitCode);
            return process.ExitCode; // unreachable, but satisfies compiler
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: Failed to launch TUI: {ex.Message}");
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
