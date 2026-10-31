using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Formatters;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.GitHub;
using Twig.ProcessOverrides;
using Twig.Rendering;

if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
{
    Console.Error.WriteLine($"error: {PlatformHelper.UnsupportedPlatformMessage}");
    Environment.ExitCode = 1;
    return;
}

// AB#350: the generated argument binder deserializes a JSON-array option value
// through JsonSerializer. Twig sets JsonSerializerIsReflectionEnabledByDefault=false
// for AOT, so it must be handed a source-generated resolver or every array-form
// option fails to parse. RepeatableOptionNormalizer emits exactly that form.
ConsoleApp.JsonSerializerOptions = new System.Text.Json.JsonSerializerOptions
{
    TypeInfoResolver = ArgumentBinderJsonContext.Default,
};

// Windows/Linux launch aliases are copies of this same native executable, not
// companion processes. Normalize before help and argument guards so every entry
// has the canonical tui options and lifecycle.
var nativeTuiPlatform = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
var executableName = Path.GetFileName(Environment.ProcessPath);
if (nativeTuiPlatform && (OperatingSystem.IsWindows()
    ? string.Equals(executableName, "twig-tui.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(executableName, "twig-bench-tui.exe", StringComparison.OrdinalIgnoreCase)
    : executableName is "twig-tui" or "twig-bench-tui"))
    args = ["tui", .. args];

var isBrowserCommand = (args.Length >= 2 && args[0] == "bench" && args[1] == "browser")
    || (nativeTuiPlatform && args.Length > 0 && args[0] == "tui");

// ITEM-154: Enable UTF-8 output so Unicode type badges render correctly on Windows.
// The native interactive route owns encoding in its terminal scope, so it must
// capture the inherited code page before any CLI bootstrap changes it. Help stays
// on the ordinary offline CLI path, including its existing UTF-8 output contract.
if (!isBrowserCommand || ProgressiveHelp.IsHelpRequest(args))
{
    try
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
    }
    catch (Exception)
    {
        // InvariantGlobalization or restricted environment — Unicode bytes will still be emitted; rendering depends on terminal capabilities.
    }
}

// TICKET-0018: startup side effects (binary cleanup, companion first-run check)
// deliberately do NOT run here. They sit below the fast-exit block so that
// --version / --help / no-args / unknown-command never pay for them.

var runApp = Twig.TwigConsoleApp.CreateRunner(services =>
    {
        // CAF resolves services for leaf help too; keep all offline routes independent
        // of workspace configuration, even when the discovered workspace is invalid.
        if (ProgressiveHelp.IsHelpRequest(args)
            || (args.Length > 0 && args[0] is "skills" or "--help-all")) return;

        if (isBrowserCommand)
        {
            // Manifest actions run from the plugin root. Only the host adapter
            // may capture topology; its runtime admits that validated source.
            services.AddSingleton(new TwigConfiguration());
            services.AddTwigRenderingServices();
            services.AddSingleton<Twig.Domain.Interfaces.ITelemetryClient, Twig.Infrastructure.Telemetry.TelemetryClient>();
            services.AddSingleton<Twig.Browser.BrowserRuntimeFactory>();
            services.AddSingleton<BrowserHostCommand>();
            return;
        }
        services.AddTwigWorkspaceServices(Directory.GetCurrentDirectory(), args);
    });

// Offline discovery and smart landing (no arguments).
if (args.Length == 1 && args[0] == "--version")
{
    Console.WriteLine(VersionHelper.GetVersion());
    return;
}
if (args.Length >= 1 && args[0] == "--skill")
{
    try
    {
        var package = Twig.Skills.SkillPackage.Load();
        Console.WriteLine(args.Length switch
        {
            1 => package.ShowEntry(),
            2 => package.ShowReference(args[1]),
            _ => throw new Twig.Skills.SkillLifecycleException("Usage: twig --skill [operations|changes|presentation]")
        });
    }
    catch (Twig.Skills.SkillLifecycleException e)
    {
        Console.Error.WriteLine(e.Message);
        Environment.ExitCode = 2;
    }
    return;
}
if (args.Length == 1 && args[0] is "-h" or "--help" or "help")
{
    ProgressiveHelp.ShowRoot();
    return;
}
if (args.Length == 0)
{
    // Smart landing: route to show if workspace is initialized, otherwise show help
    var twigDirCheck = WorkspaceDiscovery.FindTwigDir();
    if (twigDirCheck is not null)
    {
        args = ["show"];
        // Fall through to runApp(args)
    }
    else
    {
        ProgressiveHelp.ShowRoot();
        return;
    }
}

// Unknown commands provide a compact discovery hint without dumping the catalog.
if (args.Length > 0 && !args[0].StartsWith('-') && !GroupedHelp.IsKnownCommand(args))
{
    ProgressiveHelp.ShowUnknown(args[0]);
    Environment.ExitCode = 1;
    return;
}

// Help must return before argument validation, SQLite, cleanup, companion downloads,
// or command-service resolution. CAF renders declaration-derived help before DI.
if (ProgressiveHelp.TryShow(args, runApp))
    return;

// Target-only export rejects incompatible/unknown options before DI or repository reads.
var showBatchExportError = ShowBatchExportArguments.Validate(args);
if (showBatchExportError is not null)
{
    Console.Error.WriteLine(showBatchExportError);
    Environment.ExitCode = 2;
    return;
}

// AB#79: the check above only inspects args[0], so a known GROUP prefix let any compound
// verb through — `twig link bogus 5` printed top-level usage and exited 0, and `twig seed
// bogus` did not even print usage, it created a seed titled "bogus". Every rc == 0 check,
// `set -e`, and `&&` chain read those as success; #77 lost 13 link edges that way. Sits
// beside the unknown-command guard rather than inside IsKnownCommand because that predicate
// is also the source of truth for help rendering, and a subcommand typo is a usage error
// rather than an unknown command.
var subcommandError = SubcommandGuard.Validate(args);
if (subcommandError is not null)
{
    Console.Error.WriteLine(subcommandError);
    Environment.ExitCode = SubcommandGuard.UsageExitCode;
    return;
}

// AB#398: a stray extra positional reaches the generated parser as "Argument 'world' is not
// recognized." — true, and useless, because the remedy is quoting and the message never says
// so. Sits after SubcommandGuard because an unknown SUBCOMMAND is the better diagnosis when
// both apply: `twig seed bogus extra` is a typo'd verb, not a quoting mistake.
var strayPositionalError = StrayPositionalGuard.Validate(args);
if (strayPositionalError is not null)
{
    Console.Error.WriteLine(strayPositionalError);
    Environment.ExitCode = StrayPositionalGuard.UsageExitCode;
    return;
}

// AB#350: collapse repeated --field / --set into the single JSON-array form the
// argument binder actually supports. ConsoleAppFramework's generated parser
// ASSIGNS rather than appends on each occurrence, so without this only the last
// one survives and `twig new` exits 0 having written one field of N. Runs before
// -o validation so the rewrite cannot perturb it (neither option is -o).
args = RepeatableOptionNormalizer.Normalize(args);

// Wayfinder 0019: -o is validated ONCE, here, against the single accept-list in
// OutputFormats. Previously both format factories ended in a catch-all that
// silently meant "human", so `-o jsno` emitted ANSI prose with exit 0 — the
// worst failure mode for a tool whose stdout is piped into jq.
//
// This sits ABOVE the 0018 startup side effects deliberately: a usage error must
// exit without paying for a self-update sweep or a blocking companion download.
var outputFormatError = OutputFormatArgumentValidator.Validate(args);
if (outputFormatError is not null)
{
    Console.Error.WriteLine(outputFormatError);
    Environment.ExitCode = OutputFormatArgumentValidator.UsageExitCode;
    return;
}

// Structured calls must not infer a target from the interactive active-item pointer.
// Refuse before workspace resolution so missing scope cannot read active context.
var structuredMutationError = StructuredMutationArgumentValidator.Validate(args);
if (structuredMutationError is not null)
{
    CommandError.Write(new RendererFactory(), Console.Error,
        StructuredMutationArgumentValidator.GetOutputFormat(args) ?? OutputFormats.Default, structuredMutationError);
    Environment.ExitCode = StructuredMutationArgumentValidator.UsageExitCode;
    return;
}

if (!isBrowserCommand)
{
    var root = WorkspaceDiscovery.FindWorkspaceRoot(Directory.GetCurrentDirectory()) ?? Directory.GetCurrentDirectory();
    var coreAdmissionError = Twig.CoreMutationAdmissionGuard.Validate(args, Path.Combine(root, ".twig"));
    if (coreAdmissionError is not null)
    {
        CommandError.Write(new RendererFactory(), Console.Error,
            StructuredMutationArgumentValidator.GetOutputFormat(args) ?? OutputFormats.Default, coreAdmissionError);
        Environment.ExitCode = 1;
        return;
    }
}

// Skills are an explicit offline utility: do not resolve workspace/network services
// or run binary cleanup/companion installation for this command family.
if (args.Length > 0 && args[0] == "skills")
{
    runApp(args);
    return;
}

// A validated target-only export is an offline read. Keep update cleanup and
// companion installation unreachable, including on its first apphost invocation.
if (args.Length > 0 && args[0] == "show-batch" && args.Contains("--include-fields"))
{
    SQLitePCL.Batteries.Init();
    runApp(args);
    return;
}

// TICKET-0018: startup side effects run ONLY once every fast-exit path has been
// ruled out. Above this point no network or filesystem side effect is permitted —
// --version / --help / no-args / unknown-command must stay allocation-cheap and
// perform zero network calls.

// Native persistence is needed only for real command execution, never help.
SQLitePCL.Batteries.Init();

if (!isBrowserCommand)
{
    SelfUpdater.CleanupOldBinary();
    CompanionStartup.RunFirstRunCheck();
}

runApp(args);


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
            stderr.WriteLine("Inspect 'twig auth status'; renew the selected identity with 'twig auth login' (AAD) or 'twig auth pat' (PAT).");
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
            if (AdoErrorClassifier.IsTransitionError(badReqEx.Message))
            {
                stderr.WriteLine("Transition not allowed. Run 'twig sync' to update process configuration.");
            }
            else
            {
                stderr.WriteLine(
                    "hint: This change may require updating dependent fields in the same atomic 'twig patch' request.");
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

        // AB#2590/AB#2591: No workspace found — distinct from cache corruption.
        if (ex is Twig.Infrastructure.Config.WorkspaceNotFoundException)
        {
            stderr.WriteLine($"error: {ex.Message}");
            Environment.ExitCode = 1;
            return 1;
        }

        // FM-008: SQLite failures — SqliteException directly or wrapped in InvalidOperationException.
        // Branch on SqliteErrorCode rather than exception type (#271): most SQLite errors are NOT
        // corruption, and `twig init --force` destroys a healthy cache along with every staged note.
        // Demonstrated live in #268, where a FOREIGN KEY violation (a twig logic bug) told the user
        // their cache was corrupt and recommended the one action that would actually lose their data.
        var sqliteEx = ex as Microsoft.Data.Sqlite.SqliteException
            ?? (ex as InvalidOperationException)?.InnerException as Microsoft.Data.Sqlite.SqliteException;
        if (sqliteEx is not null)
        {
            WriteSqliteDiagnostic(sqliteEx, stderr);
            Environment.ExitCode = 1;
            return 1;
        }

        stderr.WriteLine($"error: {ex.Message}");
        Environment.ExitCode = 1;
        return 1;
    }

    // SQLite primary result codes. Named rather than inlined so the mapping below reads as
    // intent; see https://www.sqlite.org/rescode.html.
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteReadOnly = 8;
    private const int SqliteCorrupt = 11;
    private const int SqliteFull = 13;
    private const int SqliteCantOpen = 14;
    private const int SqliteConstraint = 19;
    private const int SqliteNotADb = 26;

    /// <summary>
    /// Maps a <see cref="Microsoft.Data.Sqlite.SqliteException"/> to an actionable message.
    /// The destructive `twig init --force` hint is deliberately scoped to the two codes that
    /// mean the database is genuinely unreadable (#271).
    /// </summary>
    private static void WriteSqliteDiagnostic(
        Microsoft.Data.Sqlite.SqliteException ex,
        TextWriter stderr)
    {
        // Microsoft.Data.Sqlite reports extended codes (e.g. 787 SQLITE_CONSTRAINT_FOREIGNKEY);
        // the low 8 bits carry the primary code.
        var primary = ex.SqliteErrorCode & 0xFF;

        switch (primary)
        {
            case SqliteCorrupt:
            case SqliteNotADb:
                stderr.WriteLine("\u26a0 Cache corrupted. Run 'twig init --force' to rebuild.");
                break;

            case SqliteConstraint:
                // A constraint violation is a bug in twig, not damaged data. Rebuilding would
                // destroy a healthy cache without fixing the defect.
                stderr.WriteLine($"error: Internal database constraint violation: {ex.Message}");
                stderr.WriteLine("hint: This is a bug in twig, not a damaged cache — your data is intact.");
                stderr.WriteLine("hint: Please report it at https://github.com/PolyphonyRequiem/twig/issues");
                break;

            case SqliteBusy:
            case SqliteLocked:
                stderr.WriteLine($"error: Cache is locked by another process: {ex.Message}");
                stderr.WriteLine("hint: Another twig or twig-mcp process is using the cache. Close it and retry.");
                break;

            case SqliteReadOnly:
            case SqliteCantOpen:
                stderr.WriteLine($"error: Cannot open the cache for writing: {ex.Message}");
                stderr.WriteLine("hint: Check file permissions on the .twig directory.");
                break;

            case SqliteFull:
                stderr.WriteLine($"error: Not enough disk space to write the cache: {ex.Message}");
                stderr.WriteLine("hint: Free up disk space and retry.");
                break;

            default:
                // Unknown code: report it honestly rather than guessing corruption.
                stderr.WriteLine($"error: Database error (SQLite code {ex.SqliteErrorCode}): {ex.Message}");
                break;
        }
    }
}

/// <summary>
/// Routes all CLI commands to their respective service implementations.
/// Commands are resolved lazily from <see cref="IServiceProvider"/> so that
/// SQLite-dependent services are only constructed when actually needed —
/// not during startup DI resolution. This allows <c>twig init</c> (which
/// creates <c>.twig/</c>) to run before the database exists.
/// </summary>
public sealed class TwigCommands(IServiceProvider services) : TwigCommandsCompatibility
{
    protected override Task<int> PreviewLegacyAsync(string? file, string output, CancellationToken ct)
        => PlanPreview(file, output, full: false, interactive: false, ct: ct);

    /// <summary>
    /// Splits <c>seed chain</c>'s comma-separated titles into the array the command takes.
    ///
    /// <para>
    /// AB#398 replaced this command's <c>params string[] titles</c> with a single
    /// <c>[Argument]</c> slot. The trailing-array shape emitted NO positional slot at all
    /// (ConsoleAppFramework 5.7.13 ignores a <c>params</c> array declared after the
    /// <see cref="CancellationToken"/>), so every bare word was rejected and the chain could
    /// only ever be built interactively. A single quoted, comma-separated value is the one
    /// multi-value spelling this argument reader supports.
    /// </para>
    /// </summary>
    internal static string[] SplitSeedTitles(string? titles)
        => string.IsNullOrWhiteSpace(titles)
            ? []
            : titles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Initialize a new Twig workspace.</summary>
    /// <param name="orgArg">Organization, positionally: twig init &lt;org&gt; &lt;project&gt;. The shipped examples document this spelling.</param>
    /// <param name="projectArg">Project, positionally, as the second argument.</param>
    /// <param name="org">Azure DevOps organization name (e.g., contoso).</param>
    /// <param name="project">Azure DevOps project name.</param>
    /// <param name="team">Team name within the project (defaults to project default team).</param>
    /// <param name="gitProject">ADO project that hosts the git repository, if different from the work-item project.</param>
    /// <param name="force">Overwrite existing workspace configuration.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="sprint">Sprint expression(s) to subscribe to (e.g., @current, @current-1). Semicolon-separated for multiple.</param>
    /// <param name="area">Area path(s) to filter by (e.g., Project\Team). Append :exact for exact match. Semicolon-separated for multiple.</param>
    /// <param name="reinitialize">Archive an existing <c>.twig/</c> tree to <c>.twig-legacy-&lt;timestamp&gt;/</c> and start clean (design §7 supported legacy recovery — no in-place migration).</param>
    /// <param name="profile">Opaque released profile identity to explicitly select (default: none). Does not discover live-process compatibility.</param>
    /// <param name="provider">Explicit work-item provider: ado (default) or github. Never inferred from git remotes.</param>
    /// <param name="host">Explicit lowercase GitHub host, used with --provider github.</param>
    /// <param name="repository">Explicit GitHub owner/name, used with --provider github. Init stays offline; discover and bind separately.</param>
    /// <param name="binding">Registered ADO binding ID to pin locally in a fresh checkout (default: endpoint default). Cannot combine with --force/--reinitialize or replace existing local state.</param>
    /// <param name="coreStore">New absolute Core database file in a fresh isolated ADO worktree. Existing workspace data is never adopted or reset.</param>
    public async Task<int> Init([Argument] string? orgArg = null, [Argument] string? projectArg = null, string? org = null, string? project = null, string? team = null, string? gitProject = null, bool force = false, string output = OutputFormatterFactory.DefaultFormat, string? sprint = null, string? area = null, bool reinitialize = false, CancellationToken ct = default, string? profile = null, string? provider = null, string? host = null, string? repository = null, string? binding = null, string? coreStore = null)
    {
        // AB#398 made both coordinates optional so the POSITIONAL spelling the shipped examples
        // document (`twig init <org> <project>`) parses alongside the named one. The generated
        // parser's own required-argument check no longer applies, so the refusal is restated
        // here rather than letting empty strings reach the command as if they were supplied.
        var resolvedOrg = org ?? orgArg;
        var resolvedProject = project ?? projectArg;
        var github = WorkItemProviderSetting.Normalize(provider) == "github";
        if (github && (resolvedOrg is not null || resolvedProject is not null))
        {
            Console.Error.WriteLine("error: GitHub init uses --host and --repository, not ADO org/project arguments.");
            return 1;
        }
        if (github)
        {
            resolvedOrg = host;
            resolvedProject = repository;
        }
        if (!github && provider is not null && WorkItemProviderSetting.Normalize(provider) is null)
        {
            Console.Error.WriteLine("error: provider-unknown: use --provider ado or --provider github.");
            return 1;
        }
        if (!github && (string.IsNullOrWhiteSpace(resolvedOrg) || string.IsNullOrWhiteSpace(resolvedProject)))
        {
            Console.Error.WriteLine("error: Usage: twig init <org> <project>, or twig init --org <org> --project <project>");
            return 1;
        }

        if (coreStore is not null && (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), ".twig"))
            || !Path.IsPathFullyQualified(coreStore) || File.Exists(coreStore) || force || reinitialize))
        {
            CommandError.Write(new RendererFactory(), Console.Error, output,
                "Core initialization refuses existing workspaces/files and reset flags. Use a fresh isolated worktree and new absolute --core-store file.");
            return 1;
        }
        return await services.GetRequiredService<InitCommand>().ExecuteAsync(resolvedOrg ?? "", resolvedProject ?? "", team, gitProject, force, output, sprint, area, reinitialize, ct, profile: profile, provider: provider, host: host, repository: repository, binding: binding, coreStore: coreStore);
    }

    /// <summary>Set the active work item by ID or title pattern.</summary>
    /// <param name="idOrPattern">Work item ID (e.g., 1234) or title substring to match.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    public async Task<int> Set([Argument] string idOrPattern, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SetCommand>().ExecuteAsync(idOrPattern, output, ct);

    /// <summary>Display a work item without changing context. Integer IDs are cache-only by default; --local-id reads a GitHub provider snapshot when configured.</summary>
    /// <param name="idArg">Work item ID, positionally. Omit only in human output to show the active work item.</param>
    /// <param name="id">Explicit work item ID; may also be supplied positionally. Conflicting IDs are refused.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal.</param>
    /// <param name="tree">Display as hierarchy tree instead of detail card.</param>
    /// <param name="refresh">Sync from ADO before displaying, instead of reading cache only.</param>
    /// <param name="fields">Comma-separated field reference names for opt-in compact JSON.</param>
    /// <param name="sections">Comma-separated links, children, parent sections for compact JSON.</param>
    /// <param name="localId">Canonical lowercase GUIDv7 identity for a GitHub provider snapshot read; currently unavailable for other providers, requires JSON output, and excludes the other show options.</param>
    public async Task<int> Show([Argument] int? idArg = null, string output = OutputFormatterFactory.DefaultFormat, bool tree = false, bool refresh = false, CancellationToken ct = default, string? fields = null, string? sections = null, string? localId = null, int? id = null)
    {
        id ??= idArg;
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.ShowAsync(id, output, tree, refresh, fields, sections, localId, ct);
        if (localId is not null)
        {
            // Resolve only the provider snapshot route; the cache-first command has process,
            // synchronization, tree, and git dependencies that this route does not need.
            return await services.GetRequiredService<ShowLocalIdCommand>().ExecuteAsync(
                id, output, tree, refresh, ct, fields, sections, localId);
        }

        return await services.GetRequiredService<ShowCommand>().ExecuteAsync(
            id, output, tree, refresh, ct, fields: fields, sections: sections);
    }

    /// <summary>Display multiple work items by ID (cache-only). Missing IDs are disclosed and exit nonzero.</summary>
    /// <param name="batchArg">Comma-separated work item IDs, positionally: twig show-batch 1234,5678,9012.</param>
    /// <param name="batch">Comma-separated work item IDs (e.g., 1234,5678,9012).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="fields">Comma-separated field reference names for opt-in compact JSON.</param>
    /// <param name="sections">Comma-separated links, children, parent sections for compact JSON.</param>
    /// <param name="includeFields">Export every stored target field, core and link metadata; requires -o json, no fields/sections/refresh.</param>
    [Command("show-batch")]
    public async Task<int> ShowBatch([Argument] string? batchArg = null, string? batch = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default, string? fields = null, string? sections = null, bool includeFields = false)
    {
        var resolved = ResolveBatch(batch, batchArg);

        // Making `batch` optional retires the generated parser's own [Required] check, which is
        // what printed help and exited 0 for a bare `twig show-batch`. That behaviour is
        // restated here rather than letting a null reach the command as if a value were
        // supplied — the same reason InitCommand restates its refusal above.
        if (resolved is null)
        {
            Console.Error.WriteLine("error: Usage: twig show-batch <ids>, or twig show-batch --batch <ids>");
            return 1;
        }

        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.ShowBatchAsync(resolved, output, fields, sections, includeFields, ct);
        return await services.GetRequiredService<ShowCommand>().ExecuteBatchAsync(resolved, output, ct, fields: fields, sections: sections, includeFields: includeFields);
    }

    /// <summary>
    /// AB#501 — resolves <c>show-batch</c>'s ids from the named option and its positional twin,
    /// named-wins, matching every other twin on this surface (<c>text ?? textArg</c>,
    /// <c>org ?? orgArg</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 Extracted as a pure function DELIBERATELY, and the mutation sweep is why. With the
    /// expression inline, mutants that dropped the positional's value or inverted the
    /// precedence both SURVIVED: every production-CLI arm runs without a populated cache, so
    /// the workspace refusal fires before the value is ever read and two different resolutions
    /// produce byte-identical output. The untestability and the untestedness were one defect.
    /// </para>
    /// <para>
    /// The positional is a scalar <c>string?</c>, never <c>string[]</c>: an ARRAY slot consumes
    /// one token and comma-splits it, moving the error from the first word to the second
    /// without fixing anything (AB#398 recorded that reverted attempt). The ids arrive as a
    /// single comma-separated token, so scalar is both correct and the shape that works.
    /// </para>
    /// <para>
    /// The named spelling is ADDED TO, never substituted — converting one regressed
    /// <c>edit --field</c> and <c>init --org/--project</c> mid-implementation on AB#398.
    /// </para>
    /// </remarks>
    internal static string? ResolveBatch(string? batch, string? batchArg) => batch ?? batchArg;

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
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    public async Task<int> Query([Argument] string? searchText = null, string? title = null, string? description = null, string? type = null, string? state = null, string? assignedTo = null, string? areaPath = null, string? iterationPath = null, string? createdSince = null, string? changedSince = null, int top = 25, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<QueryCommand>().ExecuteAsync(searchText, title, description, type, state, assignedTo, areaPath, iterationPath, createdSince, changedSince, top, output, ct);

    /// <summary>Change the state of the active work item by name.</summary>
    /// <param name="name">Target state name (e.g., Active, Resolved, Closed).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="id">Work item ID to target; omit to use the active work item.</param>
    public async Task<int> State([Argument] string name, string output = OutputFormatterFactory.DefaultFormat, int? id = null, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.StateAsync(name, id, output, ct);
        return await services.GetRequiredService<StateCommand>().ExecuteAsync(name, id, output, ct);
    }

    /// <summary>Show process configuration: list types (no args) or type details (with type name).</summary>
    /// <param name="type">Work item type name to show details for (omit to list all types).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="org">Azure DevOps organization to describe instead of this workspace's. Requires --project. Reads live from ADO (announced on stderr); writes nothing.</param>
    /// <param name="project">Azure DevOps project to describe instead of this workspace's. Requires --org.</param>
    /// <param name="includeHidden">Include types ADO reserves for its own tooling (Code Review, Feedback, Test Case and friends). Excluded by default: they cannot be created by hand.</param>
    /// <param name="refresh">AB#879: force a targeted metadata-only sync (process types + field definitions) before rendering. This is the actionable "rerun me" surface behind a "Metadata not ready" message; it never pulls work items or flushes pending writes.</param>
    public async Task<int> Process([Argument] string? type = null, string output = OutputFormatterFactory.DefaultFormat, string? org = null, string? project = null, bool includeHidden = false, bool refresh = false, CancellationToken ct = default)
        => await ProcessOverrideHost.RunAsync(
            services, org, project,
            sp => sp.GetRequiredService<ProcessCommand>().ExecuteAsync(type, output, includeHidden, refresh, ct),
            output, ct);

    /// <summary>Show the server-defined form layout (tabs, boxes, ordered fields) for a work item type.</summary>
    /// <param name="type">Work item type name (e.g. Bug, Task, User Story).</param>
    /// <param name="out">Write the rendered layout to this file instead of stdout.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="org">Azure DevOps organization to read the layout from instead of this workspace's. Requires --project.</param>
    /// <param name="project">Azure DevOps project to read the layout from instead of this workspace's. Requires --org.</param>
    [Command("process layout")]
    public async Task<int> ProcessLayout([Argument] string type, string? @out = null, string output = OutputFormatterFactory.DefaultFormat, string? org = null, string? project = null, CancellationToken ct = default)
        => await ProcessOverrideHost.RunAsync(
            services, org, project,
            sp => sp.GetRequiredService<ProcessLayoutCommand>().ExecuteAsync(type, @out, output, ct),
            output, ct);

    /// <summary>Write a byte-stable structural description of this project's process, for diffing against another.</summary>
    /// <param name="type">Work item type REFERENCE name (e.g. Niflheim.Grilling); omit to describe every type.</param>
    /// <param name="out">Write the rendered description to this file instead of stdout.</param>
    /// <param name="output">-o, Output format. Only json (and its aliases) is complete; others are abridged summaries.</param>
    /// <param name="sections">Comma-separated fields, requirements sections for opt-in compact JSON; requires a type.</param>
    /// <param name="fields">Comma-separated field reference names to select in compact JSON.</param>
    [Command("process description")]
    public async Task<int> ProcessDescription([Argument] string? type = null, string? @out = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default, string? sections = null, string? fields = null)
        => await services.GetRequiredService<ProcessDescriptionCommand>().ExecuteAsync(type, @out, output, ct, sections: sections, fields: fields);

    /// <summary>Human-only alias for the active work item's states; structured callers must use process &lt;type&gt;.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Hidden]
    public async Task<int> States(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ProcessCommand>().ExecuteStatesAsync(output, ct);

    /// <summary>Create a new work item in ADO.</summary>
    /// <param name="typeArg">Work item type, positionally: twig new task "Write tests". The shipped examples document this spelling.</param>
    /// <param name="titleArg">Title as the second positional argument. Quote multi-word titles.</param>
    /// <param name="title">Title for the new work item.</param>
    /// <param name="type">Work item type (e.g., Task, Bug, User Story).</param>
    /// <param name="area">Area path for the new work item.</param>
    /// <param name="iteration">Iteration path for the new work item.</param>
    /// <param name="description">Description text for the new work item.</param>
    /// <param name="parent">Parent work item ID to link under.</param>
    /// <param name="set">Set the new work item as the active context after creation.</param>
    /// <param name="editor">Open an external editor to fill in fields.</param>
    /// <param name="format">Convert --description before sending. Supported: "markdown" (force convert) or "raw" (never convert). Default: auto — convert when System.Description is HTML-typed.</param>
    /// <param name="field">Set a field at creation time: fieldReferenceName=value. Repeatable. Required for types with required custom fields. Values convert Markdown only for HTML-typed fields (not affected by --format); an explicit --field System.Description overrides --description.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="descriptionFile">Read the description body from a file instead of --description.</param>
    /// <param name="descriptionStdin">Read the description body from piped standard input.</param>
    public async Task<int> New([Argument] string? typeArg = null, [Argument] string? titleArg = null, string? title = null, string? type = null, string? area = null, string? iteration = null, string? description = null, int? parent = null, bool set = false, bool editor = false, string? format = null, string[]? field = null, string output = OutputFormatterFactory.DefaultFormat, string? descriptionFile = null, bool descriptionStdin = false, CancellationToken ct = default)
        => await services.GetRequiredService<NewCommand>().ExecuteAsync(title ?? titleArg, type ?? typeArg, area, iteration, description, parent, set, editor, format, field, output, descriptionFile, descriptionStdin, ct);

    /// <summary>Display the work item tree hierarchy (hidden alias: routes to show --tree, or workspace --tree when --all).</summary>
    /// <param name="idArg">Positional work item ID; required for structured output unless --id or --all is supplied.</param>
    /// <param name="id">Explicit work item ID; conflicting positional and named IDs are refused.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="depth">Maximum tree depth to display.</param>
    /// <param name="all">When set, routes to workspace --tree showing all team items as hierarchy.</param>
    /// <param name="noLive">Disable live-refresh and render a static snapshot.</param>
    /// <param name="refresh">Sync from ADO before displaying, instead of reading cache only.</param>
    [Hidden]
    public async Task<int> Tree([Argument] int? idArg = null, string output = OutputFormatterFactory.DefaultFormat, int? depth = null, bool all = false, bool noLive = false, bool refresh = false, CancellationToken ct = default, int? id = null)
        => all
            ? await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all: true, noLive, refresh, ct, tree: true)
            : await services.GetRequiredService<ShowCommand>().ExecuteAsync(id ?? idArg, output, tree: true, refresh, ct, depth, noLive);

    /// <summary>Render an arbitrary working set of work items as a forest of annotated trees.</summary>
    /// <param name="items">Comma-separated work item IDs, or @file (one per line), or @- for stdin.</param>
    /// <param name="annotate">JSON map of id to {note, style, icon}, or @file, or @- for stdin.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="depth">Levels of children to expand below each set member. 0 (default) renders the induced subtree only.</param>
    /// <param name="rootsOnly">Render only the items given, without connecting ancestors.</param>
    /// <param name="icons">Override the configured glyph mode for this invocation: auto, unicode, or nerd.</param>
    [Command("tree-set")]
    public async Task<int> TreeSet(
        string? items = null,
        string? annotate = null,
        string output = OutputFormatterFactory.DefaultFormat,
        int depth = 0,
        bool rootsOnly = false,
        string? icons = null,
        CancellationToken ct = default)
        => await services.GetRequiredService<Twig.Commands.SetTree.WorkingSetTreeCommand>()
            .ExecuteAsync(items, annotate, output, depth, rootsOnly, icons, ct);

    /// <summary>Navigate to the parent work item.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("nav up")]
    public async Task<int> NavUp(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().UpAsync(output, ct);

    /// <summary>Launch the interactive tree navigator.</summary>
    [Command("nav")]
    public async Task<int> Nav(CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().InteractiveAsync(ct);

    /// <summary>Navigate to a child work item.</summary>
    /// <param name="idOrPattern">Child work item ID or title substring to match.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("nav down")]
    public async Task<int> NavDown([Argument] string? idOrPattern = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().DownAsync(idOrPattern, output, ct);

    /// <summary>Navigate to the next sibling work item.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("nav next")]
    public async Task<int> NavNext(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().NextAsync(output, ct);

    /// <summary>Navigate to the previous sibling work item.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("nav prev")]
    public async Task<int> NavPrev(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationCommands>().PrevAsync(output, ct);

    /// <summary>Navigate backward in navigation history.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("nav back")]
    public async Task<int> NavBack(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationHistoryCommands>().BackAsync(output, ct);

    /// <summary>Navigate forward in navigation history.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("nav fore")]
    public async Task<int> NavFore(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<NavigationHistoryCommands>().ForeAsync(output, ct);

    /// <summary>Display the navigation history.</summary>
    /// <param name="nonInteractive">Skip interactive selection and print the history list.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
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

    /// <summary>Show the revision history for a work item.</summary>
    /// <param name="id">Work item ID whose history to display (required).</param>
    /// <param name="detail">Comma-delimited update IDs, or 'all', to show full field deltas for.</param>
    /// <param name="field">Comma-delimited ADO field reference names to restrict deltas to.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    public async Task<int> History([Argument] int? id = null, string? detail = null, string? field = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        // The bare `history` verb was a hidden backward-compat alias for `nav history`.
        // `twig history <id>` takes over the name; `nav history` is unchanged. Disambiguation
        // is free — the new command takes a required positional ID, the old alias took none.
        if (id is null)
        {
            Console.Error.WriteLine("error: 'twig history' requires a work item ID. For navigation history, use 'twig nav history'.");
            return 1;
        }

        return await services.GetRequiredService<HistoryCommand>().ExecuteAsync(id.Value, detail, field, output, ct);
    }

    /// <summary>Open the active work item in the browser.</summary>
    /// <param name="id">Work item ID to open; defaults to the active item.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    public async Task<int> Web([Argument] int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<WebCommand>().ExecuteAsync(id, output, ct);

    /// <summary>Create a new child work item under the active item (backward compat shortcut).</summary>
    [Hidden]
    public async Task<int> Seed([Argument] string title, string? type = null, bool editor = false, int? parent = null, bool noParent = false, string? description = null, string[]? field = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SeedNewAsync(title, type, editor, parent, noParent, description, field, output, ct);
        return await services.GetRequiredService<SeedNewCommand>().ExecuteAsync(title, type, editor, parent, noParent, description, field, output, ct);
    }

    /// <summary>Create a new local seed work item.</summary>
    /// <param name="title">Title for the new seed work item.</param>
    /// <param name="type">Work item type for the seed (e.g., Task, Bug).</param>
    /// <param name="editor">Open an external editor to fill in seed fields.</param>
    /// <param name="parent">Parent work item ID. Defaults to the active work item.</param>
    /// <param name="noParent">Create the seed with no parent. Requires --type.</param>
    /// <param name="description">Description text for the new seed work item.</param>
    /// <param name="field">Set a field on the seed: fieldReferenceName=value. Repeatable. Values convert Markdown only for HTML-typed fields; an explicit --field System.Description overrides --description.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed new")]
    public async Task<int> SeedNew(string? title = null, string? type = null, bool editor = false, int? parent = null, bool noParent = false, string? description = null, string[]? field = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SeedNewAsync(title, type, editor, parent, noParent, description, field, output, ct);
        return await services.GetRequiredService<SeedNewCommand>().ExecuteAsync(title, type, editor, parent, noParent, description, field, output, ct);
    }

    /// <summary>Edit seed fields in an external editor.</summary>
    /// <param name="id">Seed ID to edit.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed edit")]
    public async Task<int> SeedEdit([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedEditCommand>().ExecuteAsync(id, output, ct);

    /// <summary>Discard (delete) a local seed.</summary>
    /// <param name="id">Seed ID to discard.</param>
    /// <param name="yes">Skip confirmation prompt.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed discard")]
    public async Task<int> SeedDiscard([Argument] int id, bool yes = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedDiscardCommand>().ExecuteAsync(id, yes, output, ct);

    /// <summary>Show seed dashboard grouped by parent.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed view")]
    public async Task<int> SeedView(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core) return await core.SeedViewAsync(output, ct);
        return await services.GetRequiredService<SeedViewCommand>().ExecuteAsync(output, ct);
    }

    /// <summary>Create a virtual link between two items (at least one must be a seed).</summary>
    /// <param name="sourceId">Source item ID for the link.</param>
    /// <param name="targetId">Target item ID for the link.</param>
    /// <param name="type">Link type: parent-child, blocks, blocked-by, depends-on, depended-on-by, related (default), successor, or predecessor. For parent-child, source is the child and target is the parent.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed link")]
    public async Task<int> SeedLink([Argument] int sourceId, [Argument] int targetId, string? type = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedLinkCommand>().LinkAsync(sourceId, targetId, type, output, ct);

    /// <summary>Remove a virtual link between two items.</summary>
    /// <param name="sourceId">Source item ID of the link to remove.</param>
    /// <param name="targetId">Target item ID of the link to remove.</param>
    /// <param name="type">Link type to remove: parent-child, blocks, blocked-by, depends-on, depended-on-by, related (default), successor, or predecessor. For parent-child, source is the child and target is the parent.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed unlink")]
    public async Task<int> SeedUnlink([Argument] int sourceId, [Argument] int targetId, string? type = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedLinkCommand>().UnlinkAsync(sourceId, targetId, type, output, ct);

    /// <summary>List virtual links, optionally filtered by item ID.</summary>
    /// <param name="id">Item ID to filter links for; omit to show all links.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed links")]
    public async Task<int> SeedLinks([Argument] int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedLinkCommand>().ListLinksAsync(id, output, ct);

    /// <summary>Create a chain of linked seeds — interactively or from explicit titles.</summary>
    /// <param name="parent">Parent work item ID to link the chain under.</param>
    /// <param name="type">Work item type for each seed in the chain.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="titles">Seed titles, comma-separated. Quote the value: twig seed chain "design api,build api".</param>
    [Command("seed chain")]
    public async Task<int> SeedChain([Argument] string? titles = null, int? parent = null, string? type = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedChainCommand>().ExecuteAsync(parent, type, output, ct, SplitSeedTitles(titles));

    /// <summary>Validate seeds against publish rules.</summary>
    /// <param name="id">Seed ID to validate; omit to validate all seeds.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed validate")]
    public async Task<int> SeedValidate([Argument] int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedValidateCommand>().ExecuteAsync(id, output, ct);

    /// <summary>Publish seeds to Azure DevOps. Use --all for batch, --dry-run to preview, --force to skip validation, --link-branch to link to a git branch.</summary>
    /// <param name="id">Seed ID to publish; omit when using --all.</param>
    /// <param name="all">Publish all seeds in dependency order.</param>
    /// <param name="force">Skip validation before publishing.</param>
    /// <param name="dryRun">Preview what would be published without making changes.</param>
    /// <param name="linkBranch">Link published work items to this branch name (e.g. feature/my-branch). Creates an ADO artifact link to the branch ref.</param>
    /// <param name="repo">Repository name for branch linking. When omitted, uses the default repository for the project.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed publish")]
    public async Task<int> SeedPublish([Argument] int? id = null, bool all = false, bool force = false, bool dryRun = false, string? linkBranch = null, string? repo = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SeedPublishAsync(id, all, force, dryRun, linkBranch, repo, output, ct);
        return await services.GetRequiredService<SeedPublishCommand>().ExecuteAsync(id, all, force, dryRun, output, linkBranch, repo, ct);
    }

    /// <summary>Reconcile stale seed links and parent references after partial publishes.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed reconcile")]
    public async Task<int> SeedReconcile(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SeedLinkRepairCommand>().ExecuteAsync(output, ct);

    // ── Link commands (published work items) ────────────────────────

    /// <summary>Set the parent of the active work item.</summary>
    /// <param name="targetId">Work item ID to set as the parent.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link parent")]
    public async Task<int> LinkParent([Argument] int targetId, [Argument] int? child = null, int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().ParentAsync(targetId, child ?? id, output, ct);

    /// <summary>Remove the parent link from the active work item.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link unparent")]
    public async Task<int> LinkUnparent([Argument] int? child = null, int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().UnparentAsync(child ?? id, output, ct);

    /// <summary>Remove the current parent and set a new one.</summary>
    /// <param name="targetId">Work item ID to set as the new parent.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link reparent")]
    public async Task<int> LinkReparent([Argument] int targetId, [Argument] int? child = null, int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().ReparentAsync(targetId, child ?? id, output, ct);

    /// <summary>Mark the active work item as blocked by another item (Predecessor link).</summary>
    /// <param name="targetId">Work item ID that must complete first (the blocker).</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link predecessor")]
    public async Task<int> LinkPredecessor([Argument] int targetId, int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().DependencyAsync(LinkTypes.Predecessor, targetId, id, output, ct);

    /// <summary>Mark the active work item as blocking another item (Successor link).</summary>
    /// <param name="targetId">Work item ID that this item blocks.</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link successor")]
    public async Task<int> LinkSuccessor([Argument] int targetId, int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().DependencyAsync(LinkTypes.Successor, targetId, id, output, ct);

    /// <summary>Relate the active work item to another item (symmetric Related link).</summary>
    /// <param name="targetId">Work item ID to relate this item to.</param>
    /// <param name="comment">-c, Why the two items are related. Recorded on the link itself.</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link related")]
    public async Task<int> LinkRelated([Argument] int targetId, string? comment = null, int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().RelatedAsync(targetId, id, comment, output, ct);

    /// <summary>Remove a Related link between the active work item and another item.</summary>
    /// <param name="targetId">Work item ID at the other end of the link.</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link unrelate")]
    public async Task<int> LinkUnrelate([Argument] int targetId, int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().UnrelateAsync(targetId, id, output, ct);

    /// <summary>Create an explicit Twig-local Related link between two live local work items.</summary>
    /// <param name="sourceLocalId">Canonical lowercase GUIDv7 identity of the source local item.</param>
    /// <param name="targetLocalId">Canonical lowercase GUIDv7 identity of the target local item.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link local related")]
    public async Task<int> LinkLocalRelated(
        [Argument] string sourceLocalId,
        [Argument] string targetLocalId,
        string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<LocalRelationshipCommand>()
            .AddRelatedAsync(sourceLocalId, targetLocalId, output, ct);

    /// <summary>Remove an existing Twig-local Related link without contacting the provider.</summary>
    /// <param name="sourceLocalId">Canonical lowercase GUIDv7 identity of one local endpoint.</param>
    /// <param name="targetLocalId">Canonical lowercase GUIDv7 identity of the other local endpoint.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link local unrelate")]
    public async Task<int> LinkLocalUnrelate(
        [Argument] string sourceLocalId,
        [Argument] string targetLocalId,
        string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<LocalRelationshipCommand>()
            .UnrelateAsync(sourceLocalId, targetLocalId, output, ct);

    /// <summary>List Twig-local Related links touching a local work item, without contacting the provider.</summary>
    /// <param name="localId">Canonical lowercase GUIDv7 identity of the local work item.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link local list")]
    public async Task<int> LinkLocalList(
        [Argument] string localId,
        string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<LocalRelationshipCommand>()
            .ListAsync(localId, output, ct);

    /// <summary>Remove a non-hierarchy link (predecessor, successor, or related) from a work item.</summary>
    /// <param name="linkType">Link type to remove: predecessor, successor, or related.</param>
    /// <param name="targetId">Work item ID at the other end of the link.</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link unlink")]
    public async Task<int> LinkUnlink([Argument] string linkType, [Argument] int targetId, int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().UnlinkDependencyAsync(linkType, targetId, id, output, ct);

    /// <summary>Add an artifact link (URL or vstfs:// URI) to a work item.</summary>
    /// <param name="url">Artifact URL (http/https) or vstfs:// URI.</param>
    /// <param name="name">Display name for the link.</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link artifact")]
    public async Task<int> LinkArtifact([Argument] string url, string? name = null, int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.LinkResourceAsync(url, name, id, output, ct);
        return await services.GetRequiredService<ArtifactLinkCommand>().ExecuteAsync(url, name, id, output, ct);
    }

    /// <summary>Batch state transitions, field updates, and notes in a single call.</summary>
    /// <param name="state">Target state name (e.g. Active, Closed).</param>
    /// <param name="set">Field updates as key=value pairs. Repeatable.</param>
    /// <param name="note">Comment text to add after the update.</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="ids">Comma-separated IDs for multi-item batch (e.g. 1234,5678).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="format">Convert --set values before sending. Supported: "markdown".</param>
    [Command("batch")]
    public async Task<int> Batch(string? state = null, string[]? set = null, string? note = null, int? id = null, string? ids = null, string output = OutputFormatterFactory.DefaultFormat, string? format = null, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.BatchAsync(state, set, note, id, ids, output, format, ct);
        return await services.GetRequiredService<BatchCommand>().ExecuteAsync(state, set, note, id, ids, output, format, ct);
    }

    /// <summary>Add a note to the active work item.</summary>
    /// <param name="textArg">Note text, positionally. Quote multi-word text: twig note "hello world".</param>
    /// <param name="text">Note text to add; omit to open an editor.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="id">Work item ID to target; omit to use the active work item.</param>
    /// <param name="format">Convert the note text before sending. Supported: "raw" to send pre-rendered HTML or plain text unchanged; "markdown" (default) converts Markdown to HTML.</param>
    /// <param name="file">Read the note body from a file instead of an inline argument.</param>
    /// <param name="stdin">Read the note body from piped standard input.</param>
    public async Task<int> Note([Argument] string? textArg = null, string? text = null, string output = OutputFormatterFactory.DefaultFormat, int? id = null, string? format = null, string? file = null, bool stdin = false, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.NoteAsync(text ?? textArg, file, stdin, id, output, format, ct);
        return await services.GetRequiredService<NoteCommand>().ExecuteAsync(text ?? textArg, id, output, format, file, stdin, ct);
    }

    /// <summary>Update a field on the active work item.</summary>
    /// <param name="field">ADO field name or alias to update (e.g., System.Title, title).</param>
    /// <param name="value">New value for the field; omit when using --file or --stdin.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="format">Convert the input value before sending to ADO. Supported: "markdown" (force-convert), "raw" (pass through unchanged). Default: auto — converts only when the field is HTML-typed in ADO.</param>
    /// <param name="file">Read the field value from a file instead of an inline argument.</param>
    /// <param name="stdin">Read the field value from piped standard input.</param>
    /// <param name="id">Work item ID to update; omit to use the active work item.</param>
    /// <param name="append">Append the value to the existing field content instead of replacing it.</param>
    public async Task<int> Update([Argument] string field, [Argument] string? value = null, string output = OutputFormatterFactory.DefaultFormat, string? format = null, string? file = null, bool stdin = false, int? id = null, bool append = false, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.UpdateAsync(field, value, output, format, file, stdin, id, append, ct);
        return await services.GetRequiredService<UpdateCommand>().ExecuteAsync(field, value, output, format, file, stdin, id, append, ct);
    }

    /// <summary>Atomically patch multiple fields on a work item via JSON input.</summary>
    /// <param name="json">JSON object with field name → value pairs (e.g., '{"System.Title":"New title"}').</param>
    /// <param name="stdin">Read JSON from standard input instead of --json.</param>
    /// <param name="format">Convert values before sending. Supported: "markdown" (force-convert all fields), "raw" (pass through unchanged). Default: auto — converts each field individually when its ADO type is HTML.</param>
    /// <param name="workItemId">Work item ID as a positional argument (e.g. 'twig patch 1234 --json ...'), matching 'twig show'/'set'/'state'. Omit to use --id or the active work item.</param>
    /// <param name="id">Work item ID to target; equivalent to the positional form. Omit to use the active work item.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    public async Task<int> Patch([Argument] int? workItemId = null, string? json = null, bool stdin = false, string? format = null, int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.PatchAsync(id ?? workItemId, json, stdin, format, output, ct);
        return await services.GetRequiredService<PatchCommand>().ExecuteAsync(json, stdin, id ?? workItemId, output, format, ct);
    }

    /// <summary>Edit work item fields in an external editor.</summary>
    /// <param name="fieldArg">Field to edit, positionally: twig edit System.Title. The shipped examples document this spelling.</param>
    /// <param name="field">Specific field to edit; omit to edit all editable fields.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    public async Task<int> Edit([Argument] string? fieldArg = null, string? field = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<EditCommand>().ExecuteAsync(field ?? fieldArg, output, ct);

    /// <summary>Discard pending changes for a single item or all dirty items.</summary>
    /// <param name="id">Work item ID to discard changes for.</param>
    /// <param name="all">Discard all pending changes (excludes seeds).</param>
    /// <param name="yes">Skip confirmation prompt.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    public async Task<int> Discard([Argument] int? id = null, bool all = false, bool yes = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.DiscardAsync(id, all, yes, output, ct);
        return await services.GetRequiredService<DiscardCommand>().ExecuteAsync(id, all, yes, output, ct);
    }

    /// <summary>Permanently delete a work item from Azure DevOps. This is irreversible — consider 'twig state Closed' instead.</summary>
    /// <param name="id">Work item ID to delete (required).</param>
    /// <param name="force">Skip the interactive confirmation prompt.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("delete")]
    public async Task<int> Delete([Argument] int id, bool force = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core) return core.Delete(output);
        return await services.GetRequiredService<DeleteCommand>().ExecuteAsync(id, force, output, ct);
    }

    /// <summary>Push pending changes to Azure DevOps. Deprecated — use 'twig sync' instead.</summary>
    [Hidden]
    public async Task<int> Save([Argument] int? id = null, bool all = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SaveAsync(id, all, output, ct);
        await Console.Error.WriteLineAsync("hint: 'twig save' is deprecated. Use 'twig sync' instead.");
        return await services.GetRequiredService<SaveCommand>().ExecuteAsync(id, all, output, ct: ct);
    }


    /// <summary>Flush pending changes then refresh the local cache.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="pullOnly">Skip the flush phase and only pull from the active provider. Required for GitHub.</param>
    public async Task<int> Sync(string output = OutputFormatterFactory.DefaultFormat, bool pullOnly = false, CancellationToken ct = default)
        => services.GetRequiredService<TwigConfiguration>().ResolveWorkItemProviderKind() == "github"
            ? await services.GetRequiredService<ProviderSyncCommand>().ExecuteAsync(output, pullOnly, ct)
            : await services.GetRequiredService<SyncCommand>().ExecuteAsync(output, pullOnly, ct);

    /// <summary>Refresh the local cache from the active provider. Routes through sync --pull-only.</summary>
    [Hidden]
    public async Task<int> Refresh(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await Sync(output, pullOnly: true, ct);

    /// <summary>Show the current workspace.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="all">Show all team members' items, not just yours.</param>
    /// <param name="noLive">Disable live-refresh and render a static snapshot.</param>
    /// <param name="refresh">Sync from ADO before displaying, instead of reading cache only.</param>
    /// <param name="flat">Use flat (non-tree) output instead of hierarchical rendering.</param>
    /// <param name="tree">Render full backlog hierarchy tree instead of the current-Bench view.</param>
    /// <param name="view">Current-Bench presentation: table or tree; unset defaults to tree. Cannot be combined with --all, --flat, or --tree.</param>
    /// <param name="includeBrowser">Add the versioned semantic browser envelope; requires --view tree -o json.</param>
    /// <param name="expectBinding">Optional expected native binding ID; refuses a different connection selection.</param>
    /// <param name="expectIdentity">Optional expected native identity ID; refuses a different authenticated principal.</param>
    /// <param name="bench">Explicit saved Bench name or stable ID; requires --include-browser --browser-version 2 --view tree -o json. No refresh or alternate layouts.</param>
    /// <param name="browserVersion">Semantic contract version: unset preserves v1; 2 requires --include-browser and --bench. Other values are refused.</param>
    /// <param name="expectBench">Optional expected explicit Bench ID; only valid with --bench and --browser-version 2. Never selects the current Bench.</param>
    public async Task<int> Workspace(string output = OutputFormatterFactory.DefaultFormat, bool all = false, bool noLive = false, bool refresh = false, bool flat = false, bool tree = false, string? view = null, bool includeBrowser = false, string? expectBinding = null, string? expectIdentity = null, string? bench = null, int? browserVersion = null, string? expectBench = null, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, noLive, refresh, ct, flat: flat, tree: tree, view: view, includeBrowser: includeBrowser, expectBinding: expectBinding, expectIdentity: expectIdentity, bench: bench, browserVersion: browserVersion, expectBench: expectBench);

    /// <summary>Show the current workspace (short alias).</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="all">Show all team members' items, not just yours.</param>
    /// <param name="noLive">Disable live-refresh and render a static snapshot.</param>
    /// <param name="refresh">Sync from ADO before displaying, instead of reading cache only.</param>
    /// <param name="flat">Use flat (non-tree) output instead of hierarchical rendering.</param>
    /// <param name="tree">Render full backlog hierarchy tree instead of the current-Bench view.</param>
    /// <param name="view">Current-Bench presentation: table or tree; unset defaults to tree. Cannot be combined with --all, --flat, or --tree.</param>
    /// <param name="includeBrowser">Add the versioned semantic browser envelope; requires --view tree -o json.</param>
    /// <param name="expectBinding">Optional expected native binding ID; refuses a different connection selection.</param>
    /// <param name="expectIdentity">Optional expected native identity ID; refuses a different authenticated principal.</param>
    /// <param name="bench">Explicit saved Bench name or stable ID; requires --include-browser --browser-version 2 --view tree -o json. No refresh or alternate layouts.</param>
    /// <param name="browserVersion">Semantic contract version: unset preserves v1; 2 requires --include-browser and --bench. Other values are refused.</param>
    /// <param name="expectBench">Optional expected explicit Bench ID; only valid with --bench and --browser-version 2. Never selects the current Bench.</param>
    public async Task<int> Ws(string output = OutputFormatterFactory.DefaultFormat, bool all = false, bool noLive = false, bool refresh = false, bool flat = false, bool tree = false, string? view = null, bool includeBrowser = false, string? expectBinding = null, string? expectIdentity = null, string? bench = null, int? browserVersion = null, string? expectBench = null, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, noLive, refresh, ct, flat: flat, tree: tree, view: view, includeBrowser: includeBrowser, expectBinding: expectBinding, expectIdentity: expectIdentity, bench: bench, browserVersion: browserVersion, expectBench: expectBench);

    /// <summary>Track a single work item by ID (pinned to workspace).</summary>
    /// <param name="id">Work item ID to track.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench storage ID; refuses a changed current Bench before writing.</param>
    /// <param name="expectBinding">Optional captured native binding ID; refuses a changed connection.</param>
    /// <param name="expectIdentity">Optional captured native identity ID; refuses a changed principal.</param>
    /// <param name="expectSettings">Optional captured raw automatic settings digest; refuses stale pin forms.</param>
    [Command("workspace track")]
    public async Task<int> WorkspaceTrack([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, string? expectSettings = null, CancellationToken ct = default)
        => await services.GetRequiredService<TrackingCommand>().TrackAsync(id, output, ct, expectBench, expectBinding, expectIdentity, expectSettings);

    /// <summary>Track a work item and its subtree.</summary>
    /// <param name="id">Work item ID to track (with descendants).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench storage ID; refuses a changed current Bench before writing.</param>
    /// <param name="expectBinding">Optional captured native binding ID; refuses a changed connection.</param>
    /// <param name="expectIdentity">Optional captured native identity ID; refuses a changed principal.</param>
    /// <param name="expectSettings">Optional captured raw automatic settings digest; refuses stale pin forms.</param>
    [Command("workspace track-tree")]
    public async Task<int> WorkspaceTrackTree([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, string? expectSettings = null, CancellationToken ct = default)
        => await services.GetRequiredService<TrackingCommand>().TrackTreeAsync(id, output, ct, expectBench, expectBinding, expectIdentity, expectSettings);

    /// <summary>Remove explicit pins, optionally only single or tree; inherited membership remains.</summary>
    /// <param name="id">Work item ID to stop tracking.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench storage ID; refuses a changed current Bench before writing.</param>
    /// <param name="expectBinding">Optional captured native binding ID; refuses a changed connection.</param>
    /// <param name="expectIdentity">Optional captured native identity ID; refuses a changed principal.</param>
    /// <param name="expectSettings">Optional captured raw automatic settings digest; refuses stale pin forms.</param>
    /// <param name="mode">Optional pin kind: single or tree (case-insensitive). Unset removes both explicit kinds.</param>
    [Command("workspace untrack")]
    public async Task<int> WorkspaceUntrack([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, string? expectSettings = null, string? mode = null, CancellationToken ct = default)
        => await services.GetRequiredService<TrackingCommand>().UntrackAsync(id, output, ct, expectBench, expectBinding, expectIdentity, expectSettings, mode);

    /// <summary>Pull only the current Bench's query, pin, and bounded relationship scope; never flush pending writes.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench storage ID; refuses a changed current Bench before syncing.</param>
    /// <param name="expectBinding">Optional captured native binding ID; refuses a changed connection.</param>
    /// <param name="expectIdentity">Optional captured native identity ID; refuses a changed principal.</param>
    [Command("workspace sync")]
    public async Task<int> WorkspaceSync(string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, CancellationToken ct = default)
        => await services.GetRequiredService<BenchSyncCommand>().ExecuteAsync(output, expectBench, expectBinding, expectIdentity, ct);

    /// <summary>Exclude a work item from workspace view.</summary>
    /// <param name="id">Work item ID to exclude.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace exclude")]
    public async Task<int> WorkspaceExclude([Argument] int id, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<TrackingCommand>().ExcludeAsync(id, output, ct);

    /// <summary>List all excluded work items.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="clear">Remove all exclusions.</param>
    /// <param name="remove">Remove a specific exclusion by work item ID.</param>
    [Command("workspace exclusions")]
    public async Task<int> WorkspaceExclusions(string output = OutputFormatterFactory.DefaultFormat, bool clear = false, int? remove = null, CancellationToken ct = default)
        => await services.GetRequiredService<TrackingCommand>().ExclusionsAsync(output, clear, remove, ct);

    // ── Bench (ADO #148) ──

    /// <summary>Create a Bench with a name you will recognise later.</summary>
    /// <param name="name">Name for the new Bench, e.g. "release blockers".</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBinding">Optional captured native binding ID; refuses a changed connection before creating.</param>
    /// <param name="expectIdentity">Optional captured native identity ID; refuses a changed principal before creating.</param>
    [Command("bench create")]
    public async Task<int> BenchCreate([Argument] string name, string output = OutputFormatterFactory.DefaultFormat, string? expectBinding = null, string? expectIdentity = null, CancellationToken ct = default)
        => await services.GetRequiredService<BenchCommand>().CreateAsync(name, output, ct, expectBinding, expectIdentity);

    /// <summary>List the Benches that exist, marking the current one.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="includeManagement">Emit versioned management JSON with stable IDs, full contents digests, pins and query descriptions; requires -o json.</param>
    /// <param name="expectBinding">Optional captured native binding ID; refuses a changed connection before listing.</param>
    /// <param name="expectIdentity">Optional captured native identity ID; refuses a changed principal before listing.</param>
    [Command("bench list")]
    public async Task<int> BenchList(string output = OutputFormatterFactory.DefaultFormat, bool includeManagement = false, string? expectBinding = null, string? expectIdentity = null, CancellationToken ct = default)
        => await services.GetRequiredService<BenchCommand>().ListAsync(output, ct, includeManagement, expectBinding, expectIdentity);

    /// <summary>Stand on another Bench. Naming one that does not exist fails; nothing is created.</summary>
    /// <param name="name">Name of an existing Bench.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured target Bench storage ID; refuses deletion/recreation under the same name.</param>
    /// <param name="expectBinding">Optional captured native binding ID; refuses a changed connection before selecting.</param>
    /// <param name="expectIdentity">Optional captured native identity ID; refuses a changed principal before selecting.</param>
    [Command("bench switch")]
    public async Task<int> BenchSwitch([Argument] string name, string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, CancellationToken ct = default)
        => await services.GetRequiredService<BenchCommand>().SwitchAsync(name, output, ct, expectBench, expectBinding, expectIdentity);

    /// <summary>Delete a Bench. One holding pins reports what it holds and deletes nothing.</summary>
    /// <param name="name">Name of the Bench to delete.</param>
    /// <param name="confirm">Re-type the Bench's name to delete one that holds pins. There is deliberately no --force.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured target Bench storage ID; refuses deletion/recreation under the same name.</param>
    /// <param name="expectContents">Optional complete stored selector digest from management listing; changed contents require fresh confirmation.</param>
    /// <param name="expectBinding">Optional captured native binding ID; refuses a changed connection before deleting.</param>
    /// <param name="expectIdentity">Optional captured native identity ID; refuses a changed principal before deleting.</param>
    [Command("bench delete")]
    public async Task<int> BenchDelete([Argument] string name, string? confirm = null, string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, string? expectContents = null, CancellationToken ct = default)
        => await services.GetRequiredService<BenchCommand>().DeleteAsync(name, confirm, output, ct, expectBench, expectBinding, expectIdentity, expectContents);

    /// <summary>Read full, richly rendered cached detail for an explicitly selected Bench work item.</summary>
    /// <param name="id">Work item ID or local seed ID; never changes the active item.</param>
    /// <param name="width">Terminal rendering width in columns (default 80).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench ID; refuses a changed current Bench.</param>
    /// <param name="expectBinding">Optional captured binding ID; never selects a connection.</param>
    /// <param name="expectIdentity">Optional captured identity ID; never selects a principal.</param>
    /// <param name="sync">Explicitly pull this positive item and its links before rendering (default false); never flush pending edits or fetch related targets.</param>
    /// <param name="document">Export semantic JSON version 2 instead of ANSI (default false, requires -o json); may read server form metadata (never with --bench), but does not refresh the item or authorize edits.</param>
    /// <param name="bench">Explicit saved Bench name or decimal id for a cache-only document read; requires --document and -o json, is a usage error with --sync or other modes, never falls back to or switches the current Bench, and refuses non-member items.</param>
    [Command("bench detail")]
    public async Task<int> BenchDetail([Argument] int id, int width = 80, string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, bool sync = false, bool document = false, string? bench = null, CancellationToken ct = default)
        => await services.GetRequiredService<BenchDetailCommand>().ExecuteAsync(id: id, width: width, output: output, expectBench: expectBench, expectBinding: expectBinding, expectIdentity: expectIdentity, sync: sync, ct: ct, document: document, bench: bench);

    /// <summary>Read one remote discussion page for an explicitly qualified cached Bench member, without saving or refreshing.</summary>
    /// <param name="stdin">Read strict version 1 JSON through EOF (required, default false), at most 262144 UTF-8 bytes/depth 32. The cursor is untrusted paging data, not authority.</param>
    /// <param name="output">-o, Output format: human (default), json, minimal; no ids. Reads at most 50 comments/2 MiB per page, not a consistent whole-thread snapshot.</param>
    /// <param name="ct">Cancellation token; never changes the current item or Bench, posts comments, or follows supplied URLs.</param>
    [Command("bench discussion")]
    public async Task<int> BenchDiscussion(bool stdin = false, string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<BenchDiscussionCommand>().ExecuteAsync(stdin, output, ct);

    /// <summary>Prepare a v1 append-comment proposal from fresh qualified native context, without posting, journal import or file writes.</summary>
    /// <param name="stdin">Read strict version 1 JSON through EOF (required, default false), at most 262144 UTF-8 bytes/depth 32: context, format (plain or markdown), and exact text (at most 16KiB UTF-8). Stale or unqualified context refuses.</param>
    /// <param name="output">-o, Output format: human (default), json, minimal; no ids. Includes native formatted body and canonical digest; effective body limit 64KiB UTF-8.</param>
    /// <param name="ct">Cancellation token. No comment POST, authorization, retry, active-item or current-Bench change.</param>
    [Command("bench discussion prepare-post")]
    public async Task<int> BenchDiscussionPreparePost(bool stdin = false, string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<BenchDiscussionPostCommand>().ExecuteAsync(stdin, output, ct);

    /// <summary>Read qualified Bench-member attachment inventory or stream one bounded download frame.</summary>
    /// <param name="stdin">Read strict version 1 JSON through EOF (required, default false), at most 262144 UTF-8 bytes/depth 32.</param>
    /// <param name="output">-o, Output format: human, minimal or JSON for inventory; human is required with framed downloads.</param>
    /// <param name="framed">Write a bounded binary response frame for a download. No local destination is accepted.</param>
    /// <param name="ct">Cancellation token; never changes the current item or Bench, and never writes an attachment to disk.</param>
    [Command("bench attachments")]
    public async Task<int> BenchAttachments(bool stdin = false, string output = OutputFormatterFactory.DefaultFormat,
        bool framed = false,
        CancellationToken ct = default)
        => await services.GetRequiredService<BenchAttachmentCommand>().ExecuteAsync(stdin, output, framed, ct);

    /// <summary>Open a shared recoverable draft using an explicit saved Bench and the native ADO form.</summary>
    /// <param name="id">Positive native ADO item ID; no active-item default or cross-provider fallback.</param>
    /// <param name="bench">Explicit saved Bench name or decimal ID; never switches the current Bench.</param>
    /// <param name="output">-o, Output format: human (default), json, minimal. JSON returns editing envelope version 1.</param>
    /// <param name="expectBinding">Expected admitted binding ID (optional); a mismatch refuses without switching connections.</param>
    /// <param name="expectIdentity">Expected admitted identity ID (optional); never selects an identity.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("draft open")]
    public async Task<int> DraftOpen([Argument] int id, string bench,
        string output = OutputFormatterFactory.DefaultFormat, string? expectBinding = null,
        string? expectIdentity = null, CancellationToken ct = default)
        => await services.GetRequiredService<DraftEditingCommand>().OpenAsync(id, bench, output, expectBinding, expectIdentity, ct);

    /// <summary>Atomically stage nullable fields into a recoverable draft, never pending changes or the server.</summary>
    /// <param name="stdin">Read one strict version 1 JSON request through EOF (required); maximum 262144 UTF-8 bytes, 128 ordered changes. Close stdin after writing; cancellation aborts the read.</param>
    /// <param name="output">-o, Output format: human (default), json, minimal. JSON always reports committed and structured errors.</param>
    /// <param name="ct">Cancellation token. No retry, rebase, authorization or publication is performed.</param>
    [Command("draft stage")]
    public async Task<int> DraftStage(bool stdin = false, string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<DraftEditingCommand>().StageAsync(stdin, output, ct);

    /// <summary>Retain one exact qualified recoverable draft as local proposal intent, never authorize or apply.</summary>
    /// <param name="stdin">Read one strict version 1 JSON request through EOF (required, default false); maximum 262144 UTF-8 bytes and depth 32. No changes or authorizer may be supplied.</param>
    /// <param name="output">-o, Output format: human (default), json, minimal. JSON reports committed and structured refusals.</param>
    /// <param name="ct">Cancellation token; no retry, rebase, export or remote publication.</param>
    [Command("draft retain")]
    public async Task<int> DraftRetain(bool stdin = false, string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<DraftEditingCommand>().RetainAsync(stdin, output, ct);

    /// <summary>Consume the exact retained capture following a separate explicit workbench Save gesture; never recapture or export.</summary>
    /// <param name="stdin">Read the workbench's strict digest-and-caller request through EOF; required. No replacement file, effects, identity selection or approval record.</param>
    /// <param name="output">-o, Output format: human (default), json, minimal; ids is unsupported.</param>
    /// <param name="ct">Cancellation token; uncertain effects are never automatically replayed.</param>
    [Command("draft save")]
    public async Task<int> DraftSave(bool stdin = false,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<DraftSaveCommand>().ExecuteAsync(stdin, output, ct);

    /// <summary>Review the exact effects of one retained native capture without exporting or changing its journal.</summary>
    /// <param name="digest">Lowercase 64-character SHA-256 digest returned by <c>draft retain</c>; omitted values produce a usage refusal. No proposal file or path is read.</param>
    /// <param name="output">-o, Output format: human (default) shows exact nullable field effects and freshness/authority limits; minimal shows digest and effect count; json emits the closed review envelope.</param>
    /// <param name="ct">Cancellation token; no authorization, apply, preview timestamp or journal mutation is performed.</param>
    [Command("draft review")]
    public async Task<int> DraftReview([Argument] string? digest = null, string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<DraftEditingCommand>().ReviewAsync(digest, output, ct);

    /// <summary>Inspect an existing native recoverable draft without changing intent or applying it.</summary>
    /// <param name="id">Native integer work-item ID or negative seed alias; no default or active-item selection.</param>
    /// <param name="output">-o, Output format: human (default), json, minimal.</param>
    /// <param name="document">Export semantic draft document version 1 (default false, requires -o json); may CAS local observation/conflict metadata, never creates a draft or mutation authority.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("draft show")]
    public async Task<int> DraftShow([Argument] int id, string output = OutputFormatterFactory.DefaultFormat,
        bool document = false, CancellationToken ct = default)
        => await services.GetRequiredService<DraftCommand>().ShowAsync(id, output, document, ct);

    /// <summary>Capture exact recoverable intent locally and export canonical proposal bytes; never authorize or apply.</summary>
    /// <param name="id">Published native work-item ID (required).</param>
    /// <param name="bench">Explicit saved Bench name or ID (required); never selects or switches the current Bench.</param>
    /// <param name="checkoutRoot">Exact native qualified checkout root from inspection (required).</param>
    /// <param name="connectionId">Exact native connection reference from inspection (required).</param>
    /// <param name="bindingId">Exact native binding ID from inspection (required).</param>
    /// <param name="identityId">Exact admitted native authentication identity ID (required).</param>
    /// <param name="generation">Exact Int64 draft generation from inspection (required; no rounding).</param>
    /// <param name="revision">Exact native baseline and observed revision (required).</param>
    /// <param name="output">-o, Output format: human (default), json, minimal.</param>
    /// <param name="recover">Retained capture digest for export recovery (default null); never imports or adopts changed intent.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("draft capture")]
    public async Task<int> DraftCapture([Argument] int id, string bench, string checkoutRoot,
        string connectionId, string bindingId, string identityId, long generation, int revision,
        string output = OutputFormatterFactory.DefaultFormat, string? recover = null, CancellationToken ct = default)
        => await services.GetRequiredService<DraftCommand>().CaptureAsync(id, bench, checkoutRoot,
           connectionId, bindingId, identityId, generation, revision, output, recover, ct);

    /// <summary>Open the native Bench browser standalone or route a captured native Herdr action. Windows/Linux tui uses the same application.</summary>
    /// <param name="cwd">Captured source working directory; defaults to invocation CWD. Never selects an authentication identity.</param>
    /// <param name="view">Initial tree or table view (default tree).</param>
    /// <param name="file">Explicit read-only proposal file; standalone opens Review after startup, Herdr uses it only with review intent.</param>
    /// <param name="herdr">Use source-scoped native Herdr action routing instead of a standalone terminal.</param>
    /// <param name="hostEntry">Manifest-owned native pane entry; requires herdr and admits its source from the private launch ticket, not override arguments.</param>
    /// <param name="intent">Herdr control: open, status, view, tree, table, review, exit-review, reconnect or close (default open). No control applies or authorizes.</param>
    /// <param name="sourceWorkspace">Captured native Herdr source workspace, validated against the actual socket topology.</param>
    /// <param name="sourceTab">Captured native Herdr source tab, validated against the actual socket topology.</param>
    /// <param name="sourcePane">Captured native Herdr source pane, validated against the actual socket topology.</param>
    /// <param name="sourceSocket">Captured native Herdr socket path; source IDs alone are not control authority.</param>
    /// <param name="output">-o, Herdr control result: human, json or minimal. Interactive terminal entry requires human output.</param>
    [Command("bench browser")]
    public async Task<int> BenchBrowser(string? cwd = null, string view = "tree", string? file = null,
        bool herdr = false, bool hostEntry = false, string? intent = null, string? sourceWorkspace = null,
        string? sourceTab = null, string? sourcePane = null, string? sourceSocket = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<BrowserHostCommand>().ExecuteAsync(cwd, view, file, herdr, hostEntry,
            intent, sourceWorkspace, sourceTab, sourcePane, sourceSocket, output, ct);

    /// <summary>Read the current Bench configuration and raw settings digest.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench ID; refuses a changed current Bench.</param>
    /// <param name="expectBinding">Optional captured binding ID; never selects a connection.</param>
    /// <param name="expectIdentity">Optional captured identity ID; never selects a principal.</param>
    [Command("bench configuration")]
    public async Task<int> BenchConfiguration(string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, CancellationToken ct = default)
        => await services.GetRequiredService<BenchConfigurationCommand>().ExecuteAsync(output: output, expectBench: expectBench, expectBinding: expectBinding, expectIdentity: expectIdentity, ct: ct);

    /// <summary>Read the configured team's official area paths without changing Bench or workspace settings.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench ID; refuses a changed current Bench.</param>
    /// <param name="expectBinding">Optional captured binding ID; never selects a connection.</param>
    /// <param name="expectIdentity">Optional captured identity ID; never selects a principal.</param>
    [Command("bench configuration area candidates")]
    public async Task<int> BenchConfigurationAreaCandidates(string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, CancellationToken ct = default)
        => await services.GetRequiredService<BenchAreaCandidatesCommand>().ExecuteAsync(output: output, expectBench: expectBench, expectBinding: expectBinding, expectIdentity: expectIdentity, ct: ct);

    /// <summary>Add a area filter on the captured Bench only.</summary>
    /// <param name="path">Validated area path.</param>
    /// <param name="exact">Exact area match instead of Under (default false).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench ID; refuses a changed current Bench.</param>
    /// <param name="expectBinding">Optional captured binding ID; never selects a connection.</param>
    /// <param name="expectIdentity">Optional captured identity ID; never selects a principal.</param>
    /// <param name="expectSettings">Optional raw saved-query settings digest; refuses stale forms.</param>
    [Command("bench configuration area add")]
    public async Task<int> BenchConfigurationAreaAdd([Argument] string path, bool exact = false, string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, string? expectSettings = null, CancellationToken ct = default)
        => await services.GetRequiredService<BenchConfigurationCommand>().ExecuteAsync(section: "area", action: "add", value: path, exact: exact, output: output, expectBench: expectBench, expectBinding: expectBinding, expectIdentity: expectIdentity, expectSettings: expectSettings, ct: ct);

    /// <summary>Remove a area filter on the captured Bench only.</summary>
    /// <param name="path">Validated area path.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench ID; refuses a changed current Bench.</param>
    /// <param name="expectBinding">Optional captured binding ID; never selects a connection.</param>
    /// <param name="expectIdentity">Optional captured identity ID; never selects a principal.</param>
    /// <param name="expectSettings">Optional raw saved-query settings digest; refuses stale forms.</param>
    [Command("bench configuration area remove")]
    public async Task<int> BenchConfigurationAreaRemove([Argument] string path, string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, string? expectSettings = null, CancellationToken ct = default)
        => await services.GetRequiredService<BenchConfigurationCommand>().ExecuteAsync(section: "area", action: "remove", value: path, output: output, expectBench: expectBench, expectBinding: expectBinding, expectIdentity: expectIdentity, expectSettings: expectSettings, ct: ct);

    /// <summary>Add a sprint filter on the captured Bench only.</summary>
    /// <param name="expression">Sprint expression: @Current, @Current±N, or an absolute iteration path.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench ID; refuses a changed current Bench.</param>
    /// <param name="expectBinding">Optional captured binding ID; never selects a connection.</param>
    /// <param name="expectIdentity">Optional captured identity ID; never selects a principal.</param>
    /// <param name="expectSettings">Optional raw saved-query settings digest; refuses stale forms.</param>
    [Command("bench configuration sprint add")]
    public async Task<int> BenchConfigurationSprintAdd([Argument] string expression, string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, string? expectSettings = null, CancellationToken ct = default)
        => await services.GetRequiredService<BenchConfigurationCommand>().ExecuteAsync(section: "sprint", action: "add", value: expression, output: output, expectBench: expectBench, expectBinding: expectBinding, expectIdentity: expectIdentity, expectSettings: expectSettings, ct: ct);

    /// <summary>Remove a sprint filter on the captured Bench only.</summary>
    /// <param name="expression">Sprint expression: @Current, @Current±N, or an absolute iteration path.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench ID; refuses a changed current Bench.</param>
    /// <param name="expectBinding">Optional captured binding ID; never selects a connection.</param>
    /// <param name="expectIdentity">Optional captured identity ID; never selects a principal.</param>
    /// <param name="expectSettings">Optional raw saved-query settings digest; refuses stale forms.</param>
    [Command("bench configuration sprint remove")]
    public async Task<int> BenchConfigurationSprintRemove([Argument] string expression, string output = OutputFormatterFactory.DefaultFormat, string? expectBench = null, string? expectBinding = null, string? expectIdentity = null, string? expectSettings = null, CancellationToken ct = default)
        => await services.GetRequiredService<BenchConfigurationCommand>().ExecuteAsync(section: "sprint", action: "remove", value: expression, output: output, expectBench: expectBench, expectBinding: expectBinding, expectIdentity: expectIdentity, expectSettings: expectSettings, ct: ct);

    // ── Workspace Area Path Management ──

    /// <summary>Show the area-filtered workspace view.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace area")]
    public async Task<int> WorkspaceArea(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<AreaCommand>().ViewAsync(output, ct);

    /// <summary>Add an area path to workspace configuration.</summary>
    /// <param name="path">Area path to add (e.g., "Project\Team A").</param>
    /// <param name="exact">Use exact match semantics instead of subtree (under).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace area add")]
    public async Task<int> WorkspaceAreaAdd([Argument] string path, bool exact = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<AreaCommand>().AddAsync(path, exact, output, ct);

    /// <summary>Remove an area path from workspace configuration.</summary>
    /// <param name="path">Area path to remove.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace area remove")]
    public async Task<int> WorkspaceAreaRemove([Argument] string path, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<AreaCommand>().RemoveAsync(path, output, ct);

    /// <summary>List configured area paths with match semantics.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace area list")]
    public async Task<int> WorkspaceAreaList(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<AreaCommand>().ListAsync(output, ct);

    /// <summary>Fetch team area paths from ADO and replace configuration.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace area sync")]
    public async Task<int> WorkspaceAreaSync(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<AreaCommand>().SyncAsync(output, ct);

    // ── Workspace Sprint Iteration Management ──

    /// <summary>Add a sprint iteration expression to workspace configuration.</summary>
    /// <param name="expression">Sprint expression (e.g., "@current", "@current-1", "Project\Sprint 5").</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace sprint add")]
    public async Task<int> WorkspaceSprintAdd([Argument] string expression, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SprintCommand>().AddAsync(expression, output, ct);

    /// <summary>Remove a sprint iteration expression from workspace configuration.</summary>
    /// <param name="expression">Sprint expression to remove.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace sprint remove")]
    public async Task<int> WorkspaceSprintRemove([Argument] string expression, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SprintCommand>().RemoveAsync(expression, output, ct);

    /// <summary>List configured sprint iteration expressions.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace sprint list")]
    public async Task<int> WorkspaceSprintList(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<SprintCommand>().ListAsync(output, ct);

    // ── Area Path Management (deprecated aliases — use 'workspace area' instead) ──

    /// <summary>Show the area-filtered workspace view. Deprecated — use 'twig workspace area' instead.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
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
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Hidden]
    [Command("area add")]
    public async Task<int> AreaAdd([Argument] string path, bool exact = false, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        await Console.Error.WriteLineAsync("hint: 'twig area add' is deprecated. Use 'twig workspace area add' instead.");
        return await services.GetRequiredService<AreaCommand>().AddAsync(path, exact, output, ct);
    }

    /// <summary>Remove an area path. Deprecated — use 'twig workspace area remove' instead.</summary>
    /// <param name="path">Area path to remove.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Hidden]
    [Command("area remove")]
    public async Task<int> AreaRemove([Argument] string path, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        await Console.Error.WriteLineAsync("hint: 'twig area remove' is deprecated. Use 'twig workspace area remove' instead.");
        return await services.GetRequiredService<AreaCommand>().RemoveAsync(path, output, ct);
    }

    /// <summary>List configured area paths. Deprecated — use 'twig workspace area list' instead.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Hidden]
    [Command("area list")]
    public async Task<int> AreaList(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        await Console.Error.WriteLineAsync("hint: 'twig area list' is deprecated. Use 'twig workspace area list' instead.");
        return await services.GetRequiredService<AreaCommand>().ListAsync(output, ct);
    }

    /// <summary>Fetch team area paths from ADO. Deprecated — use 'twig workspace area sync' instead.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Hidden]
    [Command("area sync")]
    public async Task<int> AreaSync(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        await Console.Error.WriteLineAsync("hint: 'twig area sync' is deprecated. Use 'twig workspace area sync' instead.");
        return await services.GetRequiredService<AreaCommand>().SyncAsync(output, ct);
    }

    /// <summary>Show sprint items, grouped by assignee. Defaults to your items; use --all for the full team.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="all">Show all team members' items, not just yours.</param>
    /// <param name="refresh">Sync from ADO before displaying, instead of reading cache only.</param>
    /// <param name="flat">Use flat (non-tree) output instead of hierarchical rendering.</param>
    /// <param name="tree">Render full backlog hierarchy tree instead of sprint table.</param>
    public async Task<int> Sprint(string output = OutputFormatterFactory.DefaultFormat, bool all = false, bool refresh = false, bool flat = false, bool tree = false, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, all, refresh: refresh, ct: ct, sprintLayout: true, flat: flat, tree: tree);

    /// <summary>Read or set a configuration value.</summary>
    /// <param name="key">Configuration key to read or set (e.g., organization, display.icons). Enroll credentials with auth login or auth pat instead.</param>
    /// <param name="value">Value to set; omit to read the current value.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="global">Read or set the home-wide display.icons preference.</param>
    /// <param name="unset">Clear the selected display.icons override so it inherits the next layer.</param>
    [Command("config")]
    public async Task<int> Config([Argument] string key, [Argument] string? value = null, string output = OutputFormatterFactory.DefaultFormat, bool global = false, bool unset = false, CancellationToken ct = default)
    {
        // The normal command resolves a workspace-bound prompt writer. A global setting
        // must remain usable even when no Twig workspace has been initialized.
        var command = global
            ? new ConfigCommand(services.GetRequiredService<TwigConfiguration>(), services.GetRequiredService<TwigPaths>(), services.GetRequiredService<OutputFormatterFactory>())
            : services.GetRequiredService<ConfigCommand>();
        return await command.ExecuteAsync(key, value, output, ct, global, unset);
    }

    /// <summary>Split a legacy .twig/config into a committed twig.json (repo coords) and gitignored .twig/config (user prefs). AB#3296.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="dryRun">Preview the changes without modifying any files.</param>
    [Command("migrate-config")]
    public async Task<int> MigrateConfig(string output = OutputFormatterFactory.DefaultFormat, bool dryRun = false, CancellationToken ct = default)
        => await services.GetRequiredService<MigrateConfigCommand>().ExecuteAsync(output, dryRun, ct);

    /// <summary>Configure which fields appear in status view.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("config status-fields")]
    public async Task<int> ConfigStatusFields(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConfigStatusFieldsCommand>().ExecuteAsync(output, ct);

    /// <summary>Inspect local authentication state without refreshing. Use --provider github for a selected GitHub App identity.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="provider">Authentication provider: ado (default) or github.</param>
    /// <param name="identity">Explicit GitHub OAuth identity alias; defaults to github.oauthidentity.</param>
    /// <param name="clientId">Registered GitHub App client ID; defaults to github.appclientid.</param>
    [Command("auth status")]
    public async Task<int> AuthStatus(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default,
        string provider = "ado", string? identity = null, string? clientId = null)
        => provider == "github"
            ? await services.GetRequiredService<AuthGitHubCommand>().ExecuteAsync("status", identity, clientId, output, ct)
            : provider == "ado" && identity is null && clientId is null
                ? await services.GetRequiredService<ConnectionStatusCommand>().ExecuteAsync(output, ct)
                : AuthProviderUsageError(output);

    /// <summary>Clear the selected admission proof. Without --identity, invalidates the attached binding's cached access token (unchanged pre-#1105 behavior). With --identity, clears the named identity's cached admission proof via the shared binding service; stored credentials are untouched.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="identity">Alias of a registered identity whose cached admission proof should be cleared. Omit to clear the attached binding's cached access token.</param>
    [Command("auth clear")]
    public async Task<int> AuthClear(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default, string? identity = null)
        => await services.GetRequiredService<AuthClearCommand>().ExecuteAsync(identity, output, ct);

    /// <summary>Enroll an explicit identity. ADO uses loopback PKCE by default; --provider github uses Twig's GitHub App device flow without a shipped secret.</summary>
    /// <param name="deviceCode">Use the OAuth device authorization grant instead of loopback PKCE. Required on headless or sandboxed environments where a browser cannot be opened, but often blocked by enterprise Conditional Access policy.</param>
    /// <param name="tenant">AAD tenant ID, domain, or 'organizations' (default). Use a specific tenant when your account is a guest in multiple directories.</param>
    /// <param name="noBrowser">Print the authorize URL instead of launching the system browser. Use when running over SSH or when the OS browser launcher is unreliable.</param>
    /// <param name="identity">Alias of the identity to register or re-enroll. Required unless an attached binding already names one; a named login never clobbers an existing global/sibling account.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="provider">Authentication provider: ado (default) or github. GitHub always uses device flow.</param>
    /// <param name="clientId">Registered GitHub App client ID (github only); defaults to github.appclientid.</param>
    [Command("auth login")]
    public async Task<int> AuthLogin(
        bool deviceCode = false,
        string? tenant = null,
        bool noBrowser = false,
        string? identity = null,
        string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default,
        string provider = "ado",
        string? clientId = null)
        => provider == "github" && tenant is null
            ? await services.GetRequiredService<AuthGitHubCommand>().ExecuteAsync("login", identity, clientId, output, ct)
            : provider == "ado" && clientId is null
                ? await services.GetRequiredService<AuthLoginCommand>().ExecuteAsync(deviceCode, tenant, noBrowser, identity, output, ct)
                : AuthProviderUsageError(output);

    /// <summary>Remove selected local GitHub OAuth credentials. Does not revoke GitHub authorization or uninstall the app.</summary>
    /// <param name="provider">Required explicit authentication provider: github.</param>
    /// <param name="identity">GitHub OAuth identity alias; defaults to github.oauthidentity.</param>
    /// <param name="clientId">Registered GitHub App client ID; defaults to github.appclientid.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("auth logout")]
    public async Task<int> AuthLogout(string provider = "", string? identity = null, string? clientId = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => provider == "github"
            ? await services.GetRequiredService<AuthGitHubCommand>().ExecuteAsync("logout", identity, clientId, output, ct)
            : AuthProviderUsageError(output);

    private int AuthProviderUsageError(string output)
    {
        Console.Error.WriteLine(AuthGitHubCommand.FormatError(
            services.GetRequiredService<OutputFormatterFactory>().GetFormatter(output), output,
            "Choose --provider ado or --provider github with compatible options; logout supports --provider github only."));
        return 2;
    }

    /// <summary>List every registered identity (AAD and PAT) with safe principal metadata. Credential blobs are never surfaced; AAD rows show tenant/objectId/authorityHost, PAT rows show adoPrincipalId/adoAuthority.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("auth identities")]
    public async Task<int> AuthIdentities(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<AuthIdentitiesCommand>().ExecuteAsync(output, ct);

    /// <summary>Enroll or renew a Personal Access Token identity. The PAT is read from a non-echoing Spectre prompt on a TTY, or from stdin when --stdin is set or stdin is redirected; it is never accepted as an argument. The service performs authoritative read-only principal attestation before touching any stored credential.</summary>
    /// <param name="identity">Alias of the PAT identity to register or renew. Required outside an attached workspace; defaults to the attached binding's identity (and refuses when its method is AAD).</param>
    /// <param name="org">Azure DevOps organization name or HTTPS endpoint. Omit to use the named identity's registered PAT authority; a new alias can use the attached organization. Required for new enrollment outside attachment.</param>
    /// <param name="stdin">Read the PAT from standard input instead of prompting. Automatic when stdin is redirected (e.g. piped).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("auth pat")]
    public async Task<int> AuthPat(
        string? identity = null,
        string? org = null,
        bool stdin = false,
        string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<AuthPatCommand>().ExecuteAsync(identity, org, stdin, output, ct);

    /// <summary>Bind an endpoint (org/project) to a registered identity. Use --default to request this as the initial default; the service refuses to switch an existing different default.</summary>
    /// <param name="org">Azure DevOps organization. Falls back to the workspace's checked-in Organization when omitted together with --project.</param>
    /// <param name="project">Azure DevOps project. Falls back to the workspace's checked-in Project when omitted together with --org.</param>
    /// <param name="identity">Alias of a registered identity (see 'twig auth identities'). Required.</param>
    /// <param name="default">Request this binding as the initial default for the endpoint. Refused when a different default is already set.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("connection bind")]
    public async Task<int> ConnectionBind(
        string? identity = null,
        string? org = null,
        string? project = null,
        bool @default = false,
        string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionBindCommand>().ExecuteAsync(org, project, identity, @default, output, ct);

    /// <summary>List bindings for an endpoint (org/project). Falls back to the workspace's checked-in coordinates when both --org and --project are omitted.</summary>
    /// <param name="org">Azure DevOps organization. Falls back to the workspace's checked-in Organization when omitted together with --project.</param>
    /// <param name="project">Azure DevOps project. Falls back to the workspace's checked-in Project when omitted together with --org.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("connection list")]
    public async Task<int> ConnectionList(
        string? org = null,
        string? project = null,
        string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionListCommand>().ExecuteAsync(org, project, output, ct);

    /// <summary>Show the identity + binding the current attached worktree resolves to. Fails with setup guidance when there is no attachment or the binding is ambiguous; no fallback.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("connection status")]
    public async Task<int> ConnectionStatus(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionStatusCommand>().ExecuteAsync(output, ct);

    /// <summary>Inspect all unfinished-work and claim prerequisites for a later binding transition. Read-only; no publication, discard, release, switch or force bypass. Eligible exits 0; blocked or unreadable inspection exits 1.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("connection check")]
    public async Task<int> ConnectionCheck(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionCheckCommand>().ExecuteAsync(output, ct);

    /// <summary>Observe a GitHub repository's immutable identity and current local selection. Writes no target state. Example: twig provider discover github --host github.com --repository owner/repo -o json.</summary>
    /// <param name="host">Required bare lowercase GitHub host. No remote/config inference.</param>
    /// <param name="repository">Required owner/name lookup locator, not durable identity.</param>
    /// <param name="output">-o, Output format: human (default), json, minimal.</param>
    /// <param name="ct">Cancellation token; no target selection or remote mutation.</param>
    [Command("provider discover github")]
    public Task<int> ProviderDiscoverGitHub(string? host = null, string? repository = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default) =>
        services.GetRequiredService<ProviderSetupCommand>().DiscoverAsync("github", host, repository, output, ct);

    /// <summary>Bind the freshly observed GitHub identity locally using strict attachment CAS. Example: twig provider bind github --host github.com --repository owner/repo --expected-owner-id O_id --expected-repository-id R_id --expected-attachment-revision 0. Does not change provider config or remote permissions.</summary>
    /// <param name="host">Required bare lowercase host from discovery.</param>
    /// <param name="repository">Required owner/name locator for a fresh fixed-query lookup.</param>
    /// <param name="expectedOwnerId">Required exact opaque owner ID from discovery.</param>
    /// <param name="expectedRepositoryId">Required exact opaque repository ID from discovery.</param>
    /// <param name="expectedAttachmentRevision">Required nonnegative revision from discovery, including detached tombstones; no force bypass.</param>
    /// <param name="replaceBinding">Exact current binding ID from discovery; required when changing attached identity or host.</param>
    /// <param name="output">-o, Output format: human (default), json, minimal.</param>
    /// <param name="ct">Cancellation token; durable row precedes atomic local pointer replacement.</param>
    [Command("provider bind github")]
    public Task<int> ProviderBindGitHub(string? host = null, string? repository = null,
        string? expectedOwnerId = null, string? expectedRepositoryId = null,
        long? expectedAttachmentRevision = null, string? replaceBinding = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default) =>
        services.GetRequiredService<ProviderSetupCommand>().BindAsync("github", host, repository, expectedOwnerId,
            expectedRepositoryId, expectedAttachmentRevision, replaceBinding, output, ct);

    /// <summary>Report relationship and fixed proposal mutation capabilities for the active provider; declarations are not write permission. GitHub proposals require explicit observed-non-atomic concurrency. Optionally discover GitHub schema support with a read-only authenticated query.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="githubSchema">Discover GitHub Issue fields, states and relationship schema. Requires the active GitHub provider; does not discover issue-type, label, assignee or milestone values.</param>
    [Command("connection capabilities")]
    public async Task<int> ConnectionCapabilities(
        string output = OutputFormatterFactory.DefaultFormat,
        bool githubSchema = false,
        CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionCapabilitiesCommand>().ExecuteAsync(output, githubSchema, ct);

    /// <summary>Preview or apply a versioned legacy migration. Activation requires explicit identity mapping, verified principal evidence and actual legacy host/store closure. No force, publish, discard or process-kill path.</summary>
    /// <param name="identity">Explicit registered identity alias, or alias to enroll from the chosen legacy method. Never inferred from global login.</param>
    /// <param name="method">Legacy credential import method: aad or pat. Omit for an already registered identity.</param>
    /// <param name="confirm">Exact digest from a clean migration preview. Omit to inspect without activating. Reusing it cannot bypass changed sources or host blockers.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("connection migrate")]
    public async Task<int> ConnectionMigrate(string? identity = null, string? method = null, string? confirm = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionMigrateCommand>().ExecuteAsync(identity, method, confirm, output, ct);

    /// <summary>Report preservation movement and rollback conditions for an admitted ADO workspace. Cutover requires its exact reviewed digest; never initializes or publishes provider work.</summary>
    /// <param name="confirm">Exact lowercase preview digest. Omit for read-only inspection; reuse the original digest to resume interrupted preparation.</param>
    /// <param name="rollback">Return to protected original readers only if generation, parity and coverage still match; requires confirm.</param>
    /// <param name="cancelIntent">Exact preparing intent ID to cancel without deleting originals or backups; requires its original confirm digest.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace migrate")]
    public Task<int> WorkspaceMigrate(string? confirm = null, bool rollback = false, string? cancelIntent = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => services.GetRequiredService<WorkspaceMigrationCommand>().ExecuteAsync(confirm, rollback, cancelIntent, output, ct);

    /// <summary>Preview a guarded checkout-local binding pin, or apply the exact preview. Unfinished work and in-flight operations block switching; affected live hosts require explicit reconnect.</summary>
    /// <param name="binding">Registered binding ID for the declared endpoint; required. Inspect connection list for IDs.</param>
    /// <param name="confirm">Exact eligible preview digest. Omit for read-only preview; reuse the original digest to recover an interrupted native intent.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("connection pin")]
    public async Task<int> ConnectionPin(string? binding = null, string? confirm = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionPinCommand>().ExecuteAsync(binding, false, confirm, output, ct);

    /// <summary>Preview removal of this checkout's binding pin, or apply the exact preview to use the declared endpoint's default. No automatic publication, discard or live account swap.</summary>
    /// <param name="confirm">Exact eligible preview digest. Omit for read-only preview; interrupted transitions resume their original native intent.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("connection unpin")]
    public async Task<int> ConnectionUnpin(string? confirm = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionPinCommand>().ExecuteAsync(null, true, confirm, output, ct);

    /// <summary>Preview or apply a guarded central binding default across every affected unpinned worktree; any blocked or unreachable member refuses the whole family.</summary>
    /// <param name="binding">Registered binding ID for the declared endpoint; required.</param>
    /// <param name="confirm">Exact eligible family preview digest. Omit for read-only preview; reuse original digest for native crash recovery.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("connection default")]
    public async Task<int> ConnectionDefault(string? binding = null, string? confirm = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionDefaultCommand>().ExecuteAsync(binding, confirm, output, ct);

    /// <summary>Inspect native remote-write intents, immutable acknowledgments and uncertainty blockers for the attached checkout. Read-only; never replay, expire or discard an unknown write.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("connection writes")]
    public async Task<int> ConnectionWrites(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionWritesCommand>().ExecuteAsync(false, output: output, ct: ct);

    /// <summary>Reconcile an exact native request through original-actor authoritative readback. Confirmed seeds can retry revision-guarded temporary metadata cleanup without replacing their receipt. Missing attribution, a live operation or unexhausted CAS stays blocked; no force, create replay or inferred outcome.</summary>
    /// <param name="intent">Opaque native write intent ID from connection writes; required.</param>
    /// <param name="confirm">Exact immutable request digest from connection writes; required.</param>
    /// <param name="rationale">Truthful evidence rationale; required. Rationale alone never settles a write.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("connection reconcile-write")]
    public async Task<int> ConnectionReconcileWrite(string? intent = null, string? confirm = null,
        string? rationale = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionWritesCommand>().ExecuteAsync(true, intent, confirm, rationale, output, ct);

    /// <summary>Show the current version.</summary>
    public Task<int> Version()
    {
        Console.WriteLine(VersionHelper.GetVersion());
        return Task.FromResult(0);
    }

    /// <summary>Check for and apply updates from GitHub Releases.</summary>
    /// <param name="force">-f, Terminate any process holding a peer binary open (e.g. running twig-mcp) before applying the update.</param>
    public async Task<int> Upgrade(bool force = false, CancellationToken ct = default)
        => await services.GetRequiredService<SelfUpdateCommand>().ExecuteAsync(force, ct);

    /// <summary>Display recent release notes from GitHub Releases.</summary>
    /// <param name="count">Number of releases to display.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    public async Task<int> Changelog(int count = 5, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<ChangelogCommand>().ExecuteAsync(count, output, ct);

    /// <summary>Open the in-process native TUI on Windows/Linux; macOS support is suspended.</summary>
    /// <param name="cwd">Native source working directory; defaults to invocation CWD. Never selects an authentication identity.</param>
    /// <param name="view">Native initial tree or table view (default tree).</param>
    /// <param name="file">Explicit read-only proposal file; standalone opens Review after startup, Herdr uses it only with review intent.</param>
    /// <param name="herdr">Use source-scoped native Herdr action routing instead of a standalone terminal.</param>
    /// <param name="hostEntry">Manifest-owned native pane entry; requires herdr and admits its source from the private launch ticket, not override arguments.</param>
    /// <param name="intent">Herdr control: open, status, view, tree, table, review, exit-review, reconnect or close (default open). No control applies or authorizes.</param>
    /// <param name="sourceWorkspace">Captured native Herdr source workspace, validated against the actual socket topology.</param>
    /// <param name="sourceTab">Captured native Herdr source tab, validated against the actual socket topology.</param>
    /// <param name="sourcePane">Captured native Herdr source pane, validated against the actual socket topology.</param>
    /// <param name="sourceSocket">Captured native Herdr socket path; source IDs alone are not control authority.</param>
    /// <param name="output">-o, Herdr control result: human, json or minimal. Interactive terminal entry requires human output.</param>
    public Task<int> Tui(string? cwd = null, string view = "tree", string? file = null,
        bool herdr = false, bool hostEntry = false, string? intent = null, string? sourceWorkspace = null,
        string? sourceTab = null, string? sourcePane = null, string? sourceSocket = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            return services.GetRequiredService<BrowserHostCommand>().ExecuteAsync(cwd, view, file, herdr, hostEntry,
                intent, sourceWorkspace, sourceTab, sourcePane, sourceSocket, output, ct, commandName: "tui");

        Console.Error.WriteLine($"error: {PlatformHelper.UnsupportedPlatformMessage}");
        return Task.FromResult(1);
    }

    /// <summary>Launch the MCP server (requires twig-mcp binary).</summary>
    /// <param name="toolProfile">Advertised tool profile: compact (default) or full.</param>
    public Task<int> Mcp(string? toolProfile = null)
    {
        var arguments = toolProfile is null
            ? Array.Empty<string>()
            : ["--tool-profile", toolProfile];
        return Task.FromResult(BinaryLauncher.Launch("twig-mcp", "Twig.Mcp", arguments: arguments));
    }

    // ── Proposal lifecycle (native, formerly "plan") ──────────────────

    /// <summary>Assess cached current compliance and explicit repair alternatives. Never mutates ADO.</summary>
    /// <param name="ids">Required comma-separated positive published item IDs (1 to 200); no implicit active item or board-wide claim.</param>
    /// <param name="output">-o, Output format: human (default), json, minimal.</param>
    [Command("policy assess")]
    public async Task<int> PolicyAssess(string ids, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<PolicyCommand>().AssessAsync(ids, output, ct);

    /// <summary>Render one explicitly selected policy repair as a native proposal. No authorization or apply.</summary>
    /// <param name="ids">Required comma-separated assessed positive IDs; include a desired parent and its ancestors.</param>
    /// <param name="evidence">Required evidence digest from policy assess; changed facts/policy refuse.</param>
    /// <param name="finding">Required finding ID from the same assessment.</param>
    /// <param name="strategy">Required strategy ID from that finding; no default material choice.</param>
    /// <param name="parent">Explicit replacement parent ID, only for reparent; default none.</param>
    /// <param name="output">-o, Output format: human (default), json, minimal.</param>
    /// <param name="proposalOnly">With JSON output, emit only native proposal JSON for redirection to a new file; default false.</param>
    [Command("policy reconcile")]
    public async Task<int> PolicyReconcile(string ids, string evidence, string finding, string strategy,
        int? parent = null, string output = OutputFormatterFactory.DefaultFormat, bool proposalOnly = false, CancellationToken ct = default)
        => await services.GetRequiredService<PolicyCommand>().ReconcileAsync(ids, evidence, finding, strategy, parent, output, proposalOnly, ct);

    /// <summary>Validate a proposal v1 file. No ADO mutation. Canonical verb is <c>proposal validate</c>; <c>plan validate</c> is a retained deprecated alias.</summary>
    /// <param name="file">Path to the proposal v1 JSON file. Must resolve inside the current workspace root.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("proposal validate|plan validate")]
    public async Task<int> PlanValidate(string? file = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<PlanCommand>().ValidateAsync(file, output, ct);

    /// <summary>Preview a proposal with an optional digest guard. No ADO mutation.</summary>
    /// <param name="file">Path to the proposal v1 JSON file. Must resolve inside the current workspace root.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="full">Show exact full description bodies instead of brief effects.</param>
    /// <param name="interactive">Opt into a terminal-only Details/Back/Cancel review loop. Never applies.</param>
    /// <param name="includeRendering">Add presentation version 1 with native brief/full frames to JSON output, captured with the same review model. Never applies.</param>
    /// <param name="width">Render width in columns, 20–400. Defaults to 120 for included frames; human output remains unbounded unless specified.</param>
    /// <param name="color">Explicit color policy for human output or included frames: always (ANSI truecolor) or never (plain). Default never; no terminal/environment detection.</param>
    /// <param name="expectDigest">Refuse unless the current canonical digest matches exactly, before import or review.</param>
    [Command("proposal preview|plan preview")]
    public async Task<int> PlanPreview(
        string? file = null, string output = OutputFormatterFactory.DefaultFormat, bool full = false,
        bool interactive = false, bool includeRendering = false, int? width = null, string color = "never",
        string? expectDigest = null, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
        {
            if (interactive || includeRendering)
            {
                CommandError.Write(new RendererFactory(), Console.Error, output,
                    "Core interactive proposal presentation is not yet connected in this in-progress cutover.");
                return 1;
            }
            return await core.PreviewProposalAsync(file, output, ct, expectDigest);
        }
        return await services.GetRequiredService<PlanCommand>().PreviewAsync(
            file, output, full, interactive, ct, includeRendering, width, color, expectDigest);
    }

    /// <summary>Show the latest successfully previewed unresolved proposal in the current workspace.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("proposal latest")]
    public async Task<int> PlanLatest(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<PlanCommand>().LatestAsync(output, ct);

    /// <summary>Apply a proposal when --confirm &lt;digest&gt; matches the current file digest exactly. Canonical verb is <c>proposal apply</c>; <c>plan apply</c> is a retained deprecated alias.</summary>
    /// <param name="file">Path to the proposal v1 JSON file. Must resolve inside the current workspace root.</param>
    /// <param name="confirm">Lowercase-hex SHA-256 digest of the canonical proposal bytes. Must match exactly.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("proposal apply|plan apply")]
    public async Task<int> PlanApply(string? file = null, string? confirm = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.ApplyProposalAsync(file, confirm, output, ct);
        return await services.GetRequiredService<PlanCommand>().ApplyAsync(file, confirm, output, ct);
    }

    /// <summary>Show journal state for a proposal file. Exit 1 when no journal exists for its digest. Canonical verb is <c>proposal status</c>; <c>plan status</c> is a retained deprecated alias.</summary>
    /// <param name="file">Path to the proposal v1 JSON file. Must resolve inside the current workspace root.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("proposal status|plan status")]
    public async Task<int> PlanStatus(string? file = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<PlanCommand>().StatusAsync(file, output, ct);

    /// <summary>Describe a staged seed (identity + fingerprint) for proposal authoring. Canonical verb is <c>proposal seed</c>; <c>plan seed</c> is a retained deprecated alias.</summary>
    /// <param name="id">Negative display alias of a currently-staged seed.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("proposal seed|plan seed")]
    public async Task<int> PlanSeed(int? id = null, string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<PlanCommand>().DescribeSeedAsync(id, output, ct);

    /// <summary>Settle a never-admitted operation as retired, by fresh readback, or as superseded by a Verified replacement. Append-only native receipt; never masquerades as apply success.</summary>
    /// <param name="file">Path to the proposal v1 JSON file. Must resolve inside the current workspace root.</param>
    /// <param name="confirm">Lowercase-hex SHA-256 digest of the original canonical proposal bytes the receipt is bound to. Must match the journalled digest exactly.</param>
    /// <param name="operation">Op id of the original journalled operation being settled.</param>
    /// <param name="outcome">Outcome kind: retire | readback | supersede.</param>
    /// <param name="replacementDigest">For --outcome supersede only: canonical digest of the replacement proposal.</param>
    /// <param name="replacementOperation">For --outcome supersede only: op id of the Verified replacement operation.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("proposal reconcile")]
    public async Task<int> PlanReconcile(
        string? file = null,
        string? confirm = null,
        string? operation = null,
        string? outcome = null,
        string? replacementDigest = null,
        string? replacementOperation = null,
        string output = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
        => await services.GetRequiredService<PlanCommand>().ReconcileAsync(
            file, confirm, operation, outcome, replacementDigest, replacementOperation, output, ct);

    /// <summary>List raw staged pending changes in exact staging order. Read-only.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("pending")]
    public async Task<int> Pending(string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await services.GetRequiredService<PendingCommand>().ExecuteAsync(output, ct);
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
/// Accepted command paths for pre-routing guards, including compatibility aliases.
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
        "sprint",

        // Workspace
        "workspace",
        "ws",
        "workspace sync",
        "workspace migrate",
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

        // Bench (ADO #148, #149, #150)
        "bench create",
        "bench list",
        "bench switch",
        "bench delete",
        "bench detail",
        "bench discussion",
        "bench discussion prepare-post",
        "bench attachments",
        "draft show",
        "draft open",
        "draft stage",
        "draft retain",
        "draft save",
        "draft review",
        "draft capture",
        "bench browser",
        "bench configuration",
        "bench configuration area candidates",
        "bench configuration area add",
        "bench configuration area remove",
        "bench configuration sprint add",
        "bench configuration sprint remove",

        // Context
        "set",
        "show",
        "show-batch",
        "tree-set",
        "query",
        "web",
        "history",

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
        "process",
        "process layout",
        "process description",
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
        "link predecessor",
        "link successor",
        "link related",
        "link unrelate",
        "link local related",
        "link local unrelate",
        "link local list",
        "link unlink",
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
        "migrate-config",
        "auth",
        "auth status",
        "auth clear",
        "auth login",
        "auth logout",
        "auth identities",
        "auth pat",
        "connection",
        "connection bind",
        "connection list",
        "connection status",
        "connection check",
        "connection capabilities",
        "provider",
        "provider discover",
        "provider discover github",
        "provider bind",
        "provider bind github",
        "connection migrate",
        "connection pin",
        "connection unpin",
        "connection default",
        "connection writes",
        "connection reconcile-write",
        "version",
        "upgrade",
        "changelog",

        // Offline guidance lifecycle
        "skills",
        "skills install",
        "skills update",
        "skills configure",
        "skills status",
        "skills add",

        // Experimental
        "tui",
        "mcp",
        "ohmyposh",
        "ohmyposh init",

        // Pseudo-command (early-exit handles single-arg; multi-arg falls through)
        "help",

        // Hidden backward-compat aliases (still accepted by the CLI)
        "tree",
        "up",
        "down",
        "next",
        "prev",
        "back",
        "fore",
        "seed",
        "refresh",
        "area",
        "area add",
        "area remove",
        "area list",
        "area sync",

        // Proposal lifecycle (native, formerly "plan"). Legacy `plan <verb>` entries
        // stay registered so the alias resolves in grouped help and SubcommandGuard.
        "proposal",
        "policy",
        "policy assess",
        "policy reconcile",
        "proposal validate",
        "proposal latest",
        "proposal preview",
        "proposal apply",
        "proposal status",
        "proposal seed",
        "proposal reconcile",
        "plan",
        "plan validate",
        "plan preview",
        "plan apply",
        "plan status",
        "plan seed",
        "pending",

        // Group prefixes for compound commands without standalone handlers
        "link",
        "draft",
        // ADO #148-150. `bench` has no bare handler — every verb is `bench <verb>` — but the
        // dispatcher only ever inspects args[0], so without this entry the whole group is
        // unreachable and `twig bench list` answers "Unknown command: 'bench'".
        "bench",
    ];

    /// <summary>
    /// Returns <c>true</c> when <paramref name="args"/> begins with a recognized command name.
    /// Checks <c>args[0]</c> only, which is sufficient for its one job — deciding whether to
    /// print "Unknown command" — because every compound prefix is also a top-level entry.
    /// </summary>
    /// <remarks>
    /// 🔴 AB#79: that sufficiency is the whole trap. It answers "is this a known GROUP",
    /// not "is this a known COMMAND", so <c>twig link bogus 5</c> passes and exits 0 having
    /// done nothing. This predicate is deliberately NOT the place to fix that — it is also the
    /// source of truth for help rendering, and an unknown subcommand is a usage error rather
    /// than an unknown command. <see cref="SubcommandGuard"/> owns the compound case and runs
    /// immediately after this check in <c>Program.cs</c>.
    /// </remarks>
    public static bool IsKnownCommand(string[] args)
        => args.Length > 0 && KnownCommands.Contains(args[0]);

}

/// <summary>
/// Locates and launches a companion binary (e.g. <c>twig-tui</c>, <c>twig-mcp</c>).
/// Searches the directory containing the running <c>twig</c> binary first, then falls back to PATH.
/// Stdin/stdout are inherited for MCP stdio transport.
/// </summary>
internal static class BinaryLauncher
{
    internal static int Launch(
        string binaryName,
        string projectName,
        TextWriter? stderr = null,
        IReadOnlyList<string>? arguments = null)
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

            if (arguments is not null)
            {
                foreach (var argument in arguments)
                    psi.ArgumentList.Add(argument);
            }

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
