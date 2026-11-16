using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Formatters;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Rendering;

if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("error: Twig supports Windows and Linux only; macOS support is suspended.");
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

// Previously shipped UI aliases must refuse, never open an alternate application.
var nativeTuiPlatform = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
var executableName = Path.GetFileName(Environment.ProcessPath);
if (nativeTuiPlatform && (OperatingSystem.IsWindows()
    ? string.Equals(executableName, "twig-tui.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(executableName, "twig-bench-tui.exe", StringComparison.OrdinalIgnoreCase)
    : executableName is "twig-tui" or "twig-bench-tui"))
    args = ["tui", .. args];

// Enable UTF-8 output for the retained static terminal renderers.
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (Exception)
{
    // Restricted terminals still receive Unicode bytes.
}

var runApp = Twig.TwigConsoleApp.CreateRunner(services =>
    {
        // CAF resolves services for leaf help too; keep all offline routes independent
        // of workspace configuration, even when the discovered workspace is invalid.
        if (ProgressiveHelp.IsHelpRequest(args)
            || (args.Length > 0 && args[0] is "skills" or "--help-all")) return;

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

// Retired surfaces fail before workspace discovery, provider composition or input reads.
if (Twig.CoreMutationAdmissionGuard.ValidateCatalog(args) is { } retiredError)
{
    CommandError.Write(new RendererFactory(), Console.Error,
        StructuredMutationArgumentValidator.GetOutputFormat(args) ?? OutputFormats.Default, retiredError);
    Environment.ExitCode = 1;
    return;
}

// Unknown commands provide a compact discovery hint without dumping the catalog.
if (args.Length > 0 && !args[0].StartsWith('-') && !CommandCatalog.IsKnownCommand(args))
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

// Init owns fresh-state preflight and native bootstrap, including existing-marker refusals.
// It must never enter update cleanup, companion installation or the runtime catalog gate.
if (args.Length > 0 && args[0] == "init")
{
    SQLitePCL.Batteries.Init();
    runApp(args);
    return;
}

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

// Native persistence is needed only for real command execution, never help.
SQLitePCL.Batteries.Init();

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
            using var error = new StringWriter();
            ExceptionHandler.Handle(ex, error);
            ConsoleApp.LogError(error.ToString().TrimEnd());
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

        // FM-002 / FM-003: Authentication errors
        if (ex is Twig.Infrastructure.Ado.Exceptions.AdoAuthenticationException authEx)
        {
            stderr.WriteLine($"error: {authEx.Message}");
            stderr.WriteLine("Inspect 'twig auth status'; renew the selected identity with 'twig auth login' (AAD) or 'twig auth pat' (PAT).");
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
                stderr.WriteLine("\u26a0 Database is unreadable. Preserve its files and evidence; initialize a fresh isolated Core workspace. Reset/reinitialize is retired.");
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
                stderr.WriteLine("hint: Another process is using the database. Close that process and retry.");
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
public sealed class TwigCommands(IServiceProvider services)
{
    private static int MissingCore(string output)
    {
        CommandError.Write(new RendererFactory(), Console.Error, output,
            "A fresh initialized Core workspace is required; no legacy command fallback is available.");
        return 1;
    }

    private static int Unsupported(string output = OutputFormats.Default)
    {
        CommandError.Write(new RendererFactory(), Console.Error, output,
            "Unsupported: this surface is retired from current delivery. Use the retained Core commands; no legacy workflow was opened.");
        return 1;
    }

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

    /// <summary>Initialize one fresh native Core workspace at .twig/core.db. No legacy cache, migration or reset.</summary>
    /// <param name="orgArg">Organization, positionally: twig init &lt;org&gt; &lt;project&gt;. The shipped examples document this spelling.</param>
    /// <param name="projectArg">Project, positionally, as the second argument.</param>
    /// <param name="org">Azure DevOps organization name (e.g., contoso).</param>
    /// <param name="project">Azure DevOps project name.</param>
    /// <param name="team">Optional ADO team configuration; no team or item discovery is performed.</param>
    /// <param name="gitProject">ADO project that hosts the git repository, if different from the work-item project.</param>
    /// <param name="force">Retired; explicitly refused without writes.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal. Bare ids is unsupported.</param>
    /// <param name="sprint">Retired; initialization does not configure or prefill sprints.</param>
    /// <param name="area">Retired; initialization does not configure or prefill areas.</param>
    /// <param name="reinitialize">Retired; existing or partial workspace state is preserved and refused.</param>
    /// <param name="profile">Retired; no legacy profile selection or materialization.</param>
    /// <param name="provider">Explicit work-item provider: ado (default) or github. Never inferred from git remotes.</param>
    /// <param name="host">Explicit lowercase GitHub host, used with --provider github.</param>
    /// <param name="repository">Explicit GitHub owner/name, used with --provider github. Initialization always observes and binds native identity.</param>
    /// <param name="binding">Registered ADO binding ID to pin locally in a fresh checkout (default: endpoint default). Cannot combine with --force/--reinitialize or replace existing local state.</param>
    /// <param name="coreStore">Optional new absolute database file override, with existing parent directory. Default: .twig/core.db. Existing data is never adopted or reset.</param>
    /// <param name="account">Explicit gh account for fresh GitHub Core initialization; never inferred from the active gh account.</param>
    public async Task<int> Init([Argument] string? orgArg = null, [Argument] string? projectArg = null, string? org = null, string? project = null, string? team = null, string? gitProject = null, bool force = false, string output = OutputFormats.Default, string? sprint = null, string? area = null, bool reinitialize = false, CancellationToken ct = default, string? profile = null, string? provider = null, string? host = null, string? repository = null, string? binding = null, string? coreStore = null, string? account = null)
    {
        if (orgArg is not null && org is not null && orgArg != org
            || projectArg is not null && project is not null && projectArg != project)
        {
            CommandError.Write(new RendererFactory(), Console.Error, output, "Positional and named init coordinates conflict.");
            return 2;
        }
        var resolvedOrg = org ?? orgArg;
        var resolvedProject = project ?? projectArg;
        return await services.GetRequiredService<InitCommand>().ExecuteAsync(resolvedOrg ?? "", resolvedProject ?? "", team, gitProject, force, output, sprint, area, reinitialize, ct, profile: profile, provider: provider, host: host, repository: repository, binding: binding, coreStore: coreStore, account: account);
    }

    /// <summary>Set the active work item by ID or title pattern.</summary>
    /// <param name="idOrPattern">Work item ID (e.g., 1234) or title substring to match.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    public async Task<int> Set([Argument] string idOrPattern, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core) return await core.SetAsync(idOrPattern, output, ct);
        return MissingCore(output);
    }

    /// <summary>Display an explicitly cached Core item without changing context; --local-id retains original draft UUIDs and permanent mappings.</summary>
    /// <param name="idArg">Work item ID, positionally. Omit only in human output to show the active work item.</param>
    /// <param name="id">Explicit work item ID; may also be supplied positionally. Conflicting IDs are refused.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal.</param>
    /// <param name="tree">Display as hierarchy tree instead of detail card.</param>
    /// <param name="refresh">Explicitly refresh the native target before displaying; never flushes pending drafts.</param>
    /// <param name="fields">Comma-separated field reference names for opt-in compact JSON.</param>
    /// <param name="sections">Comma-separated links, children, parent sections for compact JSON.</param>
    /// <param name="localId">Original canonical lowercase GUIDv7 of a Core draft or permanent mapping; cache-only, without rewriting native identity.</param>
    public async Task<int> Show([Argument] int? idArg = null, string output = OutputFormats.Default, bool tree = false, bool refresh = false, CancellationToken ct = default, string? fields = null, string? sections = null, string? localId = null, int? id = null)
    {
        id ??= idArg;
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.ShowAsync(id, output, tree, refresh, fields, sections, localId, ct);
        return MissingCore(output);
    }

    /// <summary>Display multiple work items by ID (cache-only). Missing IDs are disclosed and exit nonzero.</summary>
    /// <param name="batchArg">Comma-separated work item IDs, positionally: twig show-batch 1234,5678,9012.</param>
    /// <param name="batch">Comma-separated work item IDs (e.g., 1234,5678,9012).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="fields">Comma-separated field reference names for opt-in compact JSON.</param>
    /// <param name="sections">Comma-separated links, children, parent sections for compact JSON.</param>
    /// <param name="includeFields">Export every stored target field, core and link metadata; requires -o json, no fields/sections/refresh.</param>
    [Command("show-batch")]
    public async Task<int> ShowBatch([Argument] string? batchArg = null, string? batch = null, string output = OutputFormats.Default, CancellationToken ct = default, string? fields = null, string? sections = null, bool includeFields = false)
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
        return MissingCore(output);
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

    /// <summary>Search using bounded provider-qualified Core queries. GitHub supports title/body/state only, with at most 100 total candidates.</summary>
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
    public async Task<int> Query([Argument] string? searchText = null, string? title = null, string? description = null, string? type = null, string? state = null, string? assignedTo = null, string? areaPath = null, string? iterationPath = null, string? createdSince = null, string? changedSince = null, int top = 25, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.QueryAsync(searchText, title, description, type, state, assignedTo, areaPath, iterationPath, createdSince, changedSince, top, output, ct);
        return MissingCore(output);
    }

    /// <summary>Change the state of the active work item by name.</summary>
    /// <param name="name">Target state name (e.g., Active, Resolved, Closed).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="id">Work item ID to target; omit to use the active work item.</param>
    public async Task<int> State([Argument] string name, string output = OutputFormats.Default, int? id = null, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.StateAsync(name, id, output, ct);
        return MissingCore(output);
    }

    /// <summary>Show process configuration: list types (no args) or type details (with type name).</summary>
    /// <param name="type">Work item type name to show details for (omit to list all types).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="org">Legacy target option; requires --project. Pinned Core schema overrides explicitly refuse before HTTP; initialize an isolated target workspace instead.</param>
    /// <param name="project">Legacy target option; requires --org. No ambient target is substituted on refusal.</param>
    /// <param name="includeHidden">Include types ADO reserves for its own tooling (Code Review, Feedback, Test Case and friends). Excluded by default: they cannot be created by hand.</param>
    public async Task<int> Process([Argument] string? type = null, string output = OutputFormats.Default, string? org = null, string? project = null, bool includeHidden = false, CancellationToken ct = default)
    {
        if (org is not null || project is not null)
        {
            var invalidPair = string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(project);
            var config = invalidPair ? null : services.GetService<TwigConfiguration>();
            var conflict = config is not null && !string.IsNullOrWhiteSpace(config.Organization)
                && (!string.Equals(config.Organization, org, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(config.Project, project, StringComparison.OrdinalIgnoreCase));
            CommandError.Write(new RendererFactory(), Console.Error, output, invalidPair
                ? "--org and --project must both be nonempty when an explicit target is supplied; no ambient fallback was used."
                : conflict ? "--org or --project conflicts with existing twig.json values. The manifest is authoritative."
                : "Process target override is not qualified for the pinned Core workspace; no ambient fallback or override HTTP was attempted. Initialize a fresh isolated Core workspace pinned to the requested target.");
            return invalidPair ? 2 : 1;
        }
        return await services.GetRequiredService<ProcessCommand>().ExecuteAsync(type, output, includeHidden, ct);
    }

    /// <summary>Describe the admitted provider's Core schema (core-schema-1), with explicit scope and presentation gaps.</summary>
    /// <param name="type">Work item type REFERENCE name (e.g. Niflheim.Grilling); omit to describe every type.</param>
    /// <param name="out">Write the rendered description to this file instead of stdout.</param>
    /// <param name="output">-o, Output format: human, json (and aliases), minimal. JSON carries typed rules; this is not the legacy complete layout/behavior descriptor.</param>
    /// <param name="sections">Comma-separated fields, requirements sections for opt-in compact JSON; requires a type.</param>
    /// <param name="fields">Comma-separated field reference names to select in compact JSON.</param>
    [Command("process description")]
    public async Task<int> ProcessDescription([Argument] string? type = null, string? @out = null, string output = OutputFormats.Default, CancellationToken ct = default, string? sections = null, string? fields = null)
        => await services.GetRequiredService<ProcessDescriptionCommand>().ExecuteAsync(type, @out, output, ct, sections: sections, fields: fields);

    /// <summary>Human-only alias for the active work item's states; structured callers must use process &lt;type&gt;.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    public async Task<int> States(string output = OutputFormats.Default, CancellationToken ct = default)
        => await services.GetRequiredService<ProcessCommand>().ExecuteStatesAsync(output, ct);

    /// <summary>Display a targeted work item tree (hidden alias). The legacy --all workspace layout is unsupported.</summary>
    /// <param name="idArg">Positional work item ID; required for structured output unless --id or --all is supplied.</param>
    /// <param name="id">Explicit work item ID; conflicting positional and named IDs are refused.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="depth">Maximum tree depth to display.</param>
    /// <param name="all">Unsupported legacy workspace layout; refuses before HTTP.</param>
    /// <param name="refresh">Sync from ADO before displaying, instead of reading cache only.</param>
    public async Task<int> Tree([Argument] int? idArg = null, string output = OutputFormats.Default, int? depth = null, bool all = false, bool refresh = false, CancellationToken ct = default, int? id = null)
    {
        if (all) return Unsupported(output);
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.TreeAsync(id ?? idArg, all, depth, refresh, output, ct);
        return MissingCore(output);
    }

    /// <summary>Navigate to the parent work item.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("nav up")]
    public async Task<int> NavUp(string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core) return await core.NavigateAsync("up", null, output, ct);
        return MissingCore(output);
    }

    /// <summary>Navigate to a child work item.</summary>
    /// <param name="idOrPattern">Child work item ID or title substring to match.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("nav down")]
    public async Task<int> NavDown([Argument] string? idOrPattern = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core) return await core.NavigateAsync("down", idOrPattern, output, ct);
        return MissingCore(output);
    }

    /// <summary>Navigate to the next sibling work item.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("nav next")]
    public async Task<int> NavNext(string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core) return await core.NavigateAsync("next", null, output, ct);
        return MissingCore(output);
    }

    /// <summary>Navigate to the previous sibling work item.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("nav prev")]
    public async Task<int> NavPrev(string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core) return await core.NavigateAsync("prev", null, output, ct);
        return MissingCore(output);
    }

    // ── Backward-compat aliases (bare up/down/next/prev/back/fore/history) ─

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
    public async Task<int> SeedNew(string? title = null, string? type = null, bool editor = false, int? parent = null, bool noParent = false, string? description = null, string[]? field = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SeedNewAsync(title, type, editor, parent, noParent, description, field, output, ct);
        return MissingCore(output);
    }

    /// <summary>Edit seed fields in an external editor.</summary>
    /// <param name="id">Seed ID to edit.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed edit")]
    public async Task<int> SeedEdit([Argument] int id, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core) return await core.SeedEditAsync(id, output, ct);
        return MissingCore(output);
    }

    /// <summary>Discard (delete) a local seed.</summary>
    /// <param name="id">Seed ID to discard.</param>
    /// <param name="yes">Skip confirmation prompt.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed discard")]
    public async Task<int> SeedDiscard([Argument] int id, bool yes = false, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SeedDiscardAsync(id, yes, output, ct);
        return MissingCore(output);
    }

    /// <summary>Show seed dashboard grouped by parent.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed view")]
    public async Task<int> SeedView(string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core) return await core.SeedViewAsync(output, ct);
        return MissingCore(output);
    }

    /// <summary>Create a virtual link between two items (at least one must be a seed).</summary>
    /// <param name="sourceId">Source item ID for the link.</param>
    /// <param name="targetId">Target item ID for the link.</param>
    /// <param name="type">Link type: parent-child, blocks, blocked-by, depends-on, depended-on-by, related (default), successor, or predecessor. For parent-child, source is the child and target is the parent.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed link")]
    public async Task<int> SeedLink([Argument] int sourceId, [Argument] int targetId, string? type = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SeedLinkAsync(sourceId, targetId, type, false, output, ct);
        return MissingCore(output);
    }

    /// <summary>Remove a virtual link between two items.</summary>
    /// <param name="sourceId">Source item ID of the link to remove.</param>
    /// <param name="targetId">Target item ID of the link to remove.</param>
    /// <param name="type">Link type to remove: parent-child, blocks, blocked-by, depends-on, depended-on-by, related (default), successor, or predecessor. For parent-child, source is the child and target is the parent.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed unlink")]
    public async Task<int> SeedUnlink([Argument] int sourceId, [Argument] int targetId, string? type = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SeedLinkAsync(sourceId, targetId, type, true, output, ct);
        return MissingCore(output);
    }

    /// <summary>List virtual links, optionally filtered by item ID.</summary>
    /// <param name="id">Item ID to filter links for; omit to show all links.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed links")]
    public async Task<int> SeedLinks([Argument] int? id = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SeedLinksAsync(id, output, ct);
        return MissingCore(output);
    }

    /// <summary>Create a chain of linked seeds — interactively or from explicit titles.</summary>
    /// <param name="parent">Parent work item ID to link the chain under.</param>
    /// <param name="type">Work item type for each seed in the chain.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="titles">Seed titles, comma-separated. Quote the value: twig seed chain "design api,build api".</param>
    [Command("seed chain")]
    public async Task<int> SeedChain([Argument] string? titles = null, int? parent = null, string? type = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SeedChainAsync(SplitSeedTitles(titles), parent, type, output, ct);
        return MissingCore(output);
    }

    /// <summary>Validate seeds against publish rules.</summary>
    /// <param name="id">Seed ID to validate; omit to validate all seeds.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed validate")]
    public async Task<int> SeedValidate([Argument] int? id = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SeedValidateAsync(id, output, ct);
        return MissingCore(output);
    }

    /// <summary>Publish seeds through a validated proposal. Use --all for batch, --dry-run to preview, --link-branch to link to a git branch.</summary>
    /// <param name="id">Seed ID to publish; omit when using --all.</param>
    /// <param name="all">Publish all seeds in dependency order.</param>
    /// <param name="dryRun">Preview what would be published without making changes.</param>
    /// <param name="linkBranch">Link published work items to this branch name (e.g. feature/my-branch). Creates an ADO artifact link to the branch ref.</param>
    /// <param name="repo">Repository name for branch linking. When omitted, uses the default repository for the project.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed publish")]
    public async Task<int> SeedPublish([Argument] int? id = null, bool all = false, bool dryRun = false, string? linkBranch = null, string? repo = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SeedPublishAsync(id, all, dryRun, linkBranch, repo, output, ct);
        return MissingCore(output);
    }

    /// <summary>Reconcile stale seed links and parent references after partial publishes.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("seed reconcile")]
    public async Task<int> SeedReconcile(string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SeedReconcileAsync(output, ct);
        return MissingCore(output);
    }

    // ── Link commands (published work items) ────────────────────────

    /// <summary>Set the parent of an unparented published item through a verified Core proposal.</summary>
    /// <param name="targetId">Work item ID to set as the parent.</param>
    /// <param name="childId">Optional positional child ID; conflicts with different --id/--child targets.</param>
    /// <param name="child">Explicit child ID option; conflicts with a different positional child or --id.</param>
    /// <param name="id">Explicit positive child ID; required for all machine output, even when --child is supplied.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal. Bare ids refused.</param>
    [Command("link parent")]
    public async Task<int> LinkParent([Argument] int targetId, [Argument] int? childId = null, int? id = null, int? child = null, string output = OutputFormats.Default, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().ParentAsync(targetId, id, output, ct, child, childId);

    /// <summary>Remove the one observed native parent through a verified Core proposal.</summary>
    /// <param name="childId">Optional positional child ID; conflicts with different --id/--child targets.</param>
    /// <param name="child">Explicit child ID option; conflicts with a different positional child or --id.</param>
    /// <param name="id">Explicit positive child ID; required for all machine output, even when --child is supplied.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal. Bare ids refused.</param>
    [Command("link unparent")]
    public async Task<int> LinkUnparent([Argument] int? childId = null, int? id = null, int? child = null, string output = OutputFormats.Default, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().UnparentAsync(id, output, ct, child, childId);

    /// <summary>Parent replacement is unsupported; refuses before HTTP without removing any link.</summary>
    /// <param name="targetId">Work item ID to set as the new parent.</param>
    /// <param name="childId">Optional positional child ID; conflicts with different --id/--child targets.</param>
    /// <param name="child">Explicit child ID option; conflicts with a different positional child or --id.</param>
    /// <param name="id">Explicit positive child ID; required for all machine output, even when --child is supplied.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal. Bare ids refused.</param>
    [Command("link reparent")]
    public async Task<int> LinkReparent([Argument] int targetId, [Argument] int? childId = null, int? id = null, int? child = null, string output = OutputFormats.Default, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().ReparentAsync(targetId, id, output, ct, child, childId);

    /// <summary>Mark the active work item as blocked by another item (Predecessor link).</summary>
    /// <param name="targetId">Work item ID that must complete first (the blocker).</param>
    /// <param name="id">Explicit positive source ID; required for machine output. Human output may use Core active context.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal. Bare ids refused.</param>
    [Command("link predecessor")]
    public async Task<int> LinkPredecessor([Argument] int targetId, int? id = null, string output = OutputFormats.Default, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().DependencyAsync("Predecessor", targetId, id, output, ct);

    /// <summary>Mark the active work item as blocking another item (Successor link).</summary>
    /// <param name="targetId">Work item ID that this item blocks.</param>
    /// <param name="id">Explicit positive source ID; required for machine output. Human output may use Core active context.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal. Bare ids refused.</param>
    [Command("link successor")]
    public async Task<int> LinkSuccessor([Argument] int targetId, int? id = null, string output = OutputFormats.Default, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().DependencyAsync("Successor", targetId, id, output, ct);

    /// <summary>Relate the active work item to another item (symmetric Related link).</summary>
    /// <param name="targetId">Work item ID to relate this item to.</param>
    /// <param name="comment">-c, Retained syntax; unsupported by Core relationship intent, so any supplied value refuses before HTTP.</param>
    /// <param name="id">Explicit positive source ID; required for machine output. Human output may use Core active context.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal. Bare ids refused.</param>
    [Command("link related")]
    public async Task<int> LinkRelated([Argument] int targetId, string? comment = null, int? id = null, string output = OutputFormats.Default, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().RelatedAsync(targetId, id, comment, output, ct);

    /// <summary>Remove a Related link between the active work item and another item.</summary>
    /// <param name="targetId">Work item ID at the other end of the link.</param>
    /// <param name="id">Explicit positive source ID; required for machine output. Human output may use Core active context.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal. Bare ids refused.</param>
    [Command("link unrelate")]
    public async Task<int> LinkUnrelate([Argument] int targetId, int? id = null, string output = OutputFormats.Default, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().UnrelateAsync(targetId, id, output, ct);

    /// <summary>Create an explicit LOCAL, non-native Related association through a Verified local CAS proposal. May acquire native metadata/observations; never mutates native links.</summary>
    /// <param name="target">Explicit native number, negative seed alias or original canonical lowercase GUIDv7.</param>
    /// <param name="id">Required explicit source, using the same locator forms. No active-item fallback.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal. Bare ids refused.</param>
    [Command("link local related")]
    public async Task<int> LinkLocalRelated(
        [Argument] string target,
        string? id = null,
        string output = OutputFormats.Default,
        CancellationToken ct = default)
        => await services.GetRequiredService<LocalRelationshipCommand>()
            .AddRelatedAsync(id, target, output, ct);

    /// <summary>Tombstone an existing LOCAL Related association through its exact local CAS. Native reads may occur; no native mutation.</summary>
    /// <param name="target">Explicit native number, negative seed alias or original canonical lowercase GUIDv7.</param>
    /// <param name="id">Required explicit source. No active-item fallback.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal. Bare ids refused.</param>
    [Command("link local unrelate")]
    public async Task<int> LinkLocalUnrelate(
        [Argument] string target,
        string? id = null,
        string output = OutputFormats.Default,
        CancellationToken ct = default)
        => await services.GetRequiredService<LocalRelationshipCommand>()
            .UnrelateAsync(id, target, output, ct);

    /// <summary>List cached LOCAL associations for an explicit item. Zero HTTP; no schema acquisition or context change.</summary>
    /// <param name="target">Explicit cached native number, negative seed alias or original canonical lowercase GUIDv7.</param>
    /// <param name="id">Alternative explicit target; conflicting targets are refused.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal, ids. IDs require uniquely cached neighbors.</param>
    [Command("link local list")]
    public async Task<int> LinkLocalList(
        [Argument] string? target = null,
        string? id = null,
        string output = OutputFormats.Default,
        CancellationToken ct = default)
        => await services.GetRequiredService<LocalRelationshipCommand>()
            .ListAsync(target, id, output, ct);

    /// <summary>Remove a non-hierarchy link (predecessor, successor, or related) from a work item.</summary>
    /// <param name="linkType">Link type to remove: predecessor, successor, or related.</param>
    /// <param name="targetId">Work item ID at the other end of the link.</param>
    /// <param name="id">Explicit positive source ID; required for machine output. Human output may use Core active context.</param>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal. Bare ids refused.</param>
    [Command("link unlink")]
    public async Task<int> LinkUnlink([Argument] string linkType, [Argument] int targetId, int? id = null, string output = OutputFormats.Default, CancellationToken ct = default)
        => await services.GetRequiredService<LinkCommand>().UnlinkDependencyAsync(linkType, targetId, id, output, ct);

    /// <summary>Add an artifact link (URL or vstfs:// URI) to a work item.</summary>
    /// <param name="url">Artifact URL (http/https) or vstfs:// URI.</param>
    /// <param name="name">Display name for the link.</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("link artifact")]
    public async Task<int> LinkArtifact([Argument] string url, string? name = null, int? id = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.LinkResourceAsync(url, name, id, output, ct);
        return MissingCore(output);
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
    public async Task<int> Batch(string? state = null, string[]? set = null, string? note = null, int? id = null, string? ids = null, string output = OutputFormats.Default, string? format = null, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.BatchAsync(state, set, note, id, ids, output, format, ct);
        return MissingCore(output);
    }

    /// <summary>Add a note to the active work item.</summary>
    /// <param name="textArg">Note text, positionally. Quote multi-word text: twig note "hello world".</param>
    /// <param name="text">Note text to add; omit to open an editor.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="id">Work item ID to target; omit to use the active work item.</param>
    /// <param name="format">Convert the note text before sending. Supported: "raw" to send pre-rendered HTML or plain text unchanged; "markdown" (default) converts Markdown to HTML.</param>
    /// <param name="file">Read the note body from a file instead of an inline argument.</param>
    /// <param name="stdin">Read the note body from piped standard input.</param>
    public async Task<int> Note([Argument] string? textArg = null, string? text = null, string output = OutputFormats.Default, int? id = null, string? format = null, string? file = null, bool stdin = false, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.NoteAsync(text ?? textArg, file, stdin, id, output, format, ct);
        return MissingCore(output);
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
    public async Task<int> Update([Argument] string field, [Argument] string? value = null, string output = OutputFormats.Default, string? format = null, string? file = null, bool stdin = false, int? id = null, bool append = false, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.UpdateAsync(field, value, output, format, file, stdin, id, append, ct);
        return MissingCore(output);
    }

    /// <summary>Atomically patch multiple fields on a work item via JSON input.</summary>
    /// <param name="json">JSON object with field name → value pairs (e.g., '{"System.Title":"New title"}').</param>
    /// <param name="stdin">Read JSON from standard input instead of --json.</param>
    /// <param name="format">Convert values before sending. Supported: "markdown" (force-convert all fields), "raw" (pass through unchanged). Default: auto — converts each field individually when its ADO type is HTML.</param>
    /// <param name="workItemId">Work item ID as a positional argument (e.g. 'twig patch 1234 --json ...'), matching 'twig show'/'set'/'state'. Omit to use --id or the active work item.</param>
    /// <param name="id">Work item ID to target; equivalent to the positional form. Omit to use the active work item.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    public async Task<int> Patch([Argument] int? workItemId = null, string? json = null, bool stdin = false, string? format = null, int? id = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.PatchAsync(id ?? workItemId, json, stdin, format, output, ct);
        return MissingCore(output);
    }

    /// <summary>Edit work item fields in an external editor.</summary>
    /// <param name="fieldArg">Field to edit, positionally: twig edit System.Title. The shipped examples document this spelling.</param>
    /// <param name="field">Specific field to edit; omit to edit all editable fields.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="id">Explicit work item ID; required for structured output.</param>
    public async Task<int> Edit([Argument] string? fieldArg = null, string? field = null, string output = OutputFormats.Default, CancellationToken ct = default, int? id = null)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core) return await core.EditAsync(id, field ?? fieldArg, output, ct);
        return MissingCore(output);
    }

    /// <summary>Discard pending changes for a single item or all dirty items.</summary>
    /// <param name="id">Work item ID to discard changes for.</param>
    /// <param name="all">Discard all pending changes (excludes seeds).</param>
    /// <param name="yes">Skip confirmation prompt.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    public async Task<int> Discard([Argument] int? id = null, bool all = false, bool yes = false, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.DiscardAsync(id, all, yes, output, ct);
        return MissingCore(output);
    }

    /// <summary>Publish retained drafts through the proposal journal without pulling the Bench.</summary>
    public async Task<int> Save([Argument] int? id = null, bool all = false, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SaveAsync(id, all, output, ct);
        throw new InvalidOperationException("An explicitly initialized Core workspace is required; no legacy save writer was registered.");
    }

    /// <summary>Save retained remote drafts through the proposal journal, then pull the selected Bench.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="pullOnly">Skip draft publication and only pull the selected Bench from the active provider. Default false; required for legacy GitHub.</param>
    public async Task<int> Sync(string output = OutputFormats.Default, bool pullOnly = false, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SyncAsync(output, pullOnly, ct);
        return MissingCore(output);
    }

    /// <summary>Refresh the local cache from the active provider. Routes through sync --pull-only.</summary>
    public async Task<int> Refresh(string output = OutputFormats.Default, CancellationToken ct = default)
        => await Sync(output, pullOnly: true, ct);

    /// <summary>Read cached Core Bench membership and all protected drafts as a static table. Acquires provider schema metadata.</summary>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal, ids.</param>
    /// <param name="refresh">Explicitly pull the current Bench through Core before rendering; never publishes drafts or adds active context.</param>
    /// <param name="view">Optional table; tree and other views are unsupported.</param>
    /// <param name="bench">Existing saved Bench name or GUID; never changes the current selection. Refresh requires this Bench to be current.</param>
    /// <param name="expectBench">Optional canonical GUID guard on the resolved current or named Bench, checked before refresh or schema HTTP.</param>
    public async Task<int> Workspace(string output = OutputFormats.Default, bool refresh = false, string? view = null, string? bench = null, string? expectBench = null, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, refresh, ct, view: view, bench: bench, expectBench: expectBench);

    /// <summary>Read the static cached Core Bench workspace (short alias); acquires provider schema metadata.</summary>
    /// <param name="output">-o, Output format: human, json, json-full, json-compact, minimal, ids.</param>
    /// <param name="refresh">Pull only the current Bench, without publishing drafts or adding active context.</param>
    /// <param name="view">Optional table; other views are unsupported.</param>
    /// <param name="bench">Existing Bench name or GUID; never changes selection. Refresh requires this Bench to be current.</param>
    /// <param name="expectBench">Optional canonical GUID guard on the resolved Bench, checked before refresh or schema HTTP.</param>
    public async Task<int> Ws(string output = OutputFormats.Default, bool refresh = false, string? view = null, string? bench = null, string? expectBench = null, CancellationToken ct = default)
        => await services.GetRequiredService<WorkspaceCommand>().ExecuteAsync(output, refresh, ct, view: view, bench: bench, expectBench: expectBench);

    /// <summary>Track a single work item by ID (pinned to workspace).</summary>
    /// <param name="id">Work item ID to track.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench storage ID; refuses a changed current Bench before writing.</param>
    [Command("workspace track")]
    public async Task<int> WorkspaceTrack([Argument] int id, string output = OutputFormats.Default, string? expectBench = null, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.PinAsync(id, false, false, output, ct, expectBench);
        return MissingCore(output);
    }

    /// <summary>Track a work item and its subtree.</summary>
    /// <param name="id">Work item ID to track (with descendants).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench storage ID; refuses a changed current Bench before writing.</param>
    [Command("workspace track-tree")]
    public async Task<int> WorkspaceTrackTree([Argument] int id, string output = OutputFormats.Default, string? expectBench = null, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.PinAsync(id, true, false, output, ct, expectBench);
        return MissingCore(output);
    }

    /// <summary>Remove explicit pins, optionally only single or tree; inherited membership remains.</summary>
    /// <param name="id">Work item ID to stop tracking.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench storage ID; refuses a changed current Bench before writing.</param>
    /// <param name="mode">Optional pin kind: single or tree (case-insensitive). Unset removes both explicit kinds.</param>
    [Command("workspace untrack")]
    public async Task<int> WorkspaceUntrack([Argument] int id, string output = OutputFormats.Default, string? expectBench = null, string? mode = null, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.PinAsync(id, mode == "tree", true, output, ct, expectBench, mode);
        return MissingCore(output);
    }

    /// <summary>Pull only the current Bench's query, pin, and bounded relationship scope; never flush pending writes.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench storage ID; refuses a changed current Bench before syncing.</param>
    [Command("workspace sync")]
    public async Task<int> WorkspaceSync(string output = OutputFormats.Default, string? expectBench = null, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SyncAsync(output, true, ct, true, expectBench);
        return MissingCore(output);
    }

    /// <summary>Exclude a work item from workspace view.</summary>
    /// <param name="id">Work item ID to exclude.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace exclude")]
    public async Task<int> WorkspaceExclude([Argument] int id, string output = OutputFormats.Default, CancellationToken ct = default)
        => services.GetService<WorkspaceExclusionCommand>() is { } exclusions
            ? await exclusions.ExecuteAsync(output, ct, add: id) : MissingCore(output);

    /// <summary>List all excluded work items.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="clear">Remove all exclusions.</param>
    /// <param name="remove">Remove a specific exclusion by work item ID.</param>
    [Command("workspace exclusions")]
    public async Task<int> WorkspaceExclusions(string output = OutputFormats.Default, bool clear = false, int? remove = null, CancellationToken ct = default)
        => services.GetService<WorkspaceExclusionCommand>() is { } exclusions
            ? await exclusions.ExecuteAsync(output, ct, remove: remove, clear: clear) : MissingCore(output);

    // ── Bench (ADO #148) ──

    /// <summary>Create a Bench with a name you will recognise later.</summary>
    /// <param name="name">Name for the new Bench, e.g. "release blockers".</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("bench create")]
    public async Task<int> BenchCreate([Argument] string name, string output = OutputFormats.Default, CancellationToken ct = default)
        => services.GetService<UserBenchCommand>() is { } native
            ? await native.ExecuteAsync("create", name, output, ct) : MissingCore(output);

    /// <summary>List the Benches that exist, marking the current one.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="includeManagement">Emit versioned management JSON with stable IDs, full contents digests, pins and query descriptions; requires -o json.</param>
    [Command("bench|bench list")]
    public async Task<int> BenchList(string output = OutputFormats.Default, bool includeManagement = false, CancellationToken ct = default)
        => services.GetService<UserBenchCommand>() is { } native
            ? await native.ExecuteAsync("list", null, output, ct, management: includeManagement) : MissingCore(output);

    /// <summary>Rename a user-wide Bench without changing its pins or context selections.</summary>
    /// <param name="name">Existing Bench name or canonical GUID.</param>
    /// <param name="newName">New unique Bench name (1-120 characters).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured Bench GUID; refuses a replacement under the same name.</param>
    /// <param name="expectContents">Optional complete contents digest from bench list --include-management.</param>
    [Command("bench rename")]
    public async Task<int> BenchRename([Argument] string name, [Argument] string newName,
        string output = OutputFormats.Default, string? expectBench = null, string? expectContents = null,
        CancellationToken ct = default)
        => services.GetService<UserBenchCommand>() is { } native
            ? await native.ExecuteAsync("rename", name, output, ct, newName: newName, expectBench: expectBench, expectContents: expectContents)
            : MissingCore(output);

    /// <summary>Stand on another Bench. Naming one that does not exist fails; nothing is created.</summary>
    /// <param name="name">Name of an existing Bench.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured target Bench storage ID; refuses deletion/recreation under the same name.</param>
    [Command("bench switch")]
    public async Task<int> BenchSwitch([Argument] string name, string output = OutputFormats.Default, string? expectBench = null, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.SwitchBenchAsync(name, output, ct, expectBench);
        return MissingCore(output);
    }

    /// <summary>Delete a Bench. One holding pins reports what it holds and deletes nothing.</summary>
    /// <param name="name">Name of the Bench to delete.</param>
    /// <param name="confirm">Re-type the Bench's name to delete one that holds pins. There is deliberately no --force.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectBench">Optional captured target Bench storage ID; refuses deletion/recreation under the same name.</param>
    /// <param name="expectContents">Optional complete stored selector digest from management listing; changed contents require fresh confirmation.</param>
    [Command("bench delete")]
    public async Task<int> BenchDelete([Argument] string name, string? confirm = null, string output = OutputFormats.Default, string? expectBench = null, string? expectContents = null, CancellationToken ct = default)
        => services.GetService<UserBenchCommand>() is { } native
            ? await native.ExecuteAsync("delete", name, output, ct, confirm: confirm, expectBench: expectBench, expectContents: expectContents)
            : MissingCore(output);

    /// <summary>List the exact shared Bench pins/rules and captured revision; no provider reads.</summary>
    /// <param name="name">Optional Bench name/GUID; otherwise the current native context's existing selection.</param>
    /// <param name="output">-o, Output format: human, json, minimal, ids (one-based rule indices).</param>
    [Command("bench rules")]
    public Task<int> BenchRules([Argument] string? name = null, string output = OutputFormats.Default, CancellationToken ct = default)
        => NativePlanning("rules", new(Bench: name), output, ct);

    /// <summary>Add one connection-qualified shared rule. Conditions inside it are AND; independent Bench pins/rules are OR.</summary>
    /// <param name="name">Existing shared Bench name or GUID.</param>
    /// <param name="area">Optional absolute native area path.</param>
    /// <param name="exact">Match exactly this area; default includes descendants.</param>
    /// <param name="iteration">Optional absolute path, @Current, @Current+N or @Current-N; relative forms require the cached native team calendar.</param>
    /// <param name="mine">Restrict this planning rule to the canonical native actor, never a display-name guess.</param>
    /// <param name="query">Optional native saved-query GUID; a standalone reference, not frozen result IDs.</param>
    /// <param name="backlog">Optional native backlog ID; a standalone exact-team reference.</param>
    /// <param name="expectRevision">Optional captured Bench revision; refuses a concurrent edit.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("bench rule add")]
    public Task<int> BenchRuleAdd([Argument] string name, string? area = null, bool exact = false,
        string? iteration = null, bool mine = false, string? query = null, string? backlog = null,
        long? expectRevision = null, string output = OutputFormats.Default, CancellationToken ct = default)
        => NativePlanning("add-rule", new(Bench: name, Area: area, Exact: exact, Iteration: iteration,
            Mine: mine, Query: query, Backlog: backlog, ExpectedRevision: expectRevision), output, ct);

    /// <summary>Remove one captured shared pin/rule without deleting any upstream item or local draft.</summary>
    /// <param name="name">Existing Bench name or GUID.</param>
    /// <param name="index">Required one-based index from a fresh bench rules listing.</param>
    /// <param name="expectRevision">Required captured Bench revision; index changes are never silently adopted.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("bench rule remove")]
    public Task<int> BenchRuleRemove([Argument] string name, int index, long expectRevision,
        string output = OutputFormats.Default, CancellationToken ct = default)
        => NativePlanning("remove-rule", new(Bench: name, Rule: index, ExpectedRevision: expectRevision), output, ct);

    /// <summary>Read cached native planning metadata without refreshing it or changing any Bench.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("bench planning")]
    public Task<int> BenchPlanning(string output = OutputFormats.Default, CancellationToken ct = default)
        => NativePlanning("metadata", new(), output, ct);

    /// <summary>Read the current context's cached native planning metadata; alias of bench planning.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("bench planning show")]
    public Task<int> BenchPlanningShow(string output = OutputFormats.Default, CancellationToken ct = default)
        => BenchPlanning(output, ct);

    /// <summary>Explicitly refresh native areas and the exact selected team's iteration calendar; no work items or Bench rules change.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("bench planning refresh")]
    public Task<int> BenchPlanningRefresh(string output = OutputFormats.Default, CancellationToken ct = default)
        => NativePlanning("refresh", new(), output, ct);

    /// <summary>Discover native saved-query hierarchy and exact-team backlogs; does not execute them or change selection.</summary>
    /// <param name="output">-o, Output format: human, json, minimal, ids (native source IDs).</param>
    [Command("bench sources")]
    public Task<int> BenchSources(string output = OutputFormats.Default, CancellationToken ct = default)
        => NativePlanning("sources", new(), output, ct);

    /// <summary>Explicitly acquire saved-query/backlog membership and item bodies into the native cache; never pins, selects or publishes.</summary>
    /// <param name="query">Native saved-query GUID from bench sources.</param>
    /// <param name="backlog">Native backlog ID from bench sources; requires the selected native team.</param>
    /// <param name="output">-o, Output format: human, json, minimal, ids (member IDs).</param>
    [Command("bench browse")]
    public Task<int> BenchBrowse(string? query = null, string? backlog = null,
        string output = OutputFormats.Default, CancellationToken ct = default)
        => NativePlanning("browse", new(Query: query, Backlog: backlog), output, ct);

    private Task<int> NativePlanning(string action, BenchPlanningArguments arguments, string output, CancellationToken ct)
        => services.GetService<BenchPlanningCommand>() is { } native
            ? native.ExecuteAsync(action, arguments, output, ct) : Task.FromResult(MissingCore(output));

    // ── Workspace Area Path Management ──

    /// <summary>Show the current Bench's cached view, including its shared planning rules and protected drafts.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace area")]
    public async Task<int> WorkspaceArea(string output = OutputFormats.Default, CancellationToken ct = default)
        => services.GetService<WorkspaceCommand>() is { } native
            ? await native.ExecuteAsync(output, ct: ct) : MissingCore(output);

    /// <summary>Add an area to the current Bench's planning rule; areas are OR, area/iteration/ownership categories are AND.</summary>
    /// <param name="path">Area path to add (e.g., "Project\Team A").</param>
    /// <param name="exact">Use exact match semantics instead of subtree (under).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="rule">Optional one-based planning rule index; required when several planning rules exist.</param>
    /// <param name="expectRevision">Captured Bench revision required with an explicit rule index.</param>
    [Command("workspace area add")]
    public Task<int> WorkspaceAreaAdd([Argument] string path, bool exact = false, string output = OutputFormats.Default,
        int? rule = null, long? expectRevision = null, CancellationToken ct = default)
        => NativePlanning("area-add", new(Value: path, Exact: exact, Rule: rule, ExpectedRevision: expectRevision), output, ct);

    /// <summary>Remove one area from the current Bench's planning rule; other pins, rules and workspace exclusions are preserved.</summary>
    /// <param name="path">Area path to remove.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="rule">Optional captured planning rule index.</param>
    /// <param name="expectRevision">Captured Bench revision required with an explicit rule index.</param>
    [Command("workspace area remove")]
    public Task<int> WorkspaceAreaRemove([Argument] string path, string output = OutputFormats.Default,
        int? rule = null, long? expectRevision = null, CancellationToken ct = default)
        => NativePlanning("area-remove", new(Value: path, Rule: rule, ExpectedRevision: expectRevision), output, ct);

    /// <summary>List saved shared area paths and exact/under semantics without fetching items.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace area list")]
    public Task<int> WorkspaceAreaList(string output = OutputFormats.Default, CancellationToken ct = default)
        => NativePlanning("area-list", new(), output, ct);

    /// <summary>Refresh native planning metadata only; does not replace or populate shared Bench rules.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace area sync")]
    public Task<int> WorkspaceAreaSync(string output = OutputFormats.Default, CancellationToken ct = default)
        => NativePlanning("refresh", new(), output, ct);

    // ── Workspace Sprint Iteration Management ──

    /// <summary>Add an iteration expression to the current Bench's planning rule; union iterations and preserve area/ownership constraints.</summary>
    /// <param name="expression">Sprint expression (e.g., "@current", "@current-1", "Project\Sprint 5").</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="rule">Optional captured planning rule index.</param>
    /// <param name="expectRevision">Captured Bench revision required with an explicit rule index.</param>
    [Command("workspace sprint add")]
    public Task<int> WorkspaceSprintAdd([Argument] string expression, string output = OutputFormats.Default,
        int? rule = null, long? expectRevision = null, CancellationToken ct = default)
        => NativePlanning("sprint-add", new(Value: expression, Rule: rule, ExpectedRevision: expectRevision), output, ct);

    /// <summary>Remove one saved iteration expression; never replace the last rule with an all-items query.</summary>
    /// <param name="expression">Sprint expression to remove.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="rule">Optional captured planning rule index.</param>
    /// <param name="expectRevision">Captured Bench revision required with an explicit rule index.</param>
    [Command("workspace sprint remove")]
    public Task<int> WorkspaceSprintRemove([Argument] string expression, string output = OutputFormats.Default,
        int? rule = null, long? expectRevision = null, CancellationToken ct = default)
        => NativePlanning("sprint-remove", new(Value: expression, Rule: rule, ExpectedRevision: expectRevision), output, ct);

    /// <summary>List saved absolute/relative iteration expressions and canonical teams without remote item acquisition.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("workspace sprint list")]
    public Task<int> WorkspaceSprintList(string output = OutputFormats.Default, CancellationToken ct = default)
        => NativePlanning("sprint-list", new(), output, ct);

    // ── Area Path Management (deprecated aliases — use 'workspace area' instead) ──

    /// <summary>Inspect local authentication state without refreshing. Use --provider github for a selected GitHub App identity.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="provider">Omit for recorded native binding/target status. Explicit github inspects a selected App credential, not the pinned gh actor; ado requires an ADO workspace scope.</param>
    /// <param name="identity">Explicit GitHub OAuth identity alias; defaults to github.oauthidentity.</param>
    /// <param name="clientId">Registered GitHub App client ID; defaults to github.appclientid.</param>
    [Command("auth status")]
    public async Task<int> AuthStatus(string output = OutputFormats.Default, CancellationToken ct = default,
        string? provider = null, string? identity = null, string? clientId = null)
        => provider == "github"
            ? await services.GetRequiredService<AuthGitHubCommand>().ExecuteAsync("status", identity, clientId, output, ct)
            : (provider is null || provider == "ado"
                && services.GetRequiredService<TwigConfiguration>().ResolveWorkItemProviderKind() == "ado")
                && identity is null && clientId is null
                ? await services.GetRequiredService<ConnectionStatusCommand>().ExecuteAsync(output, ct)
                : AuthProviderUsageError(output);

    /// <summary>Enroll an explicit identity. ADO uses loopback PKCE by default; --provider github uses Twig's GitHub App device flow without a shipped secret.</summary>
    /// <param name="deviceCode">Use the OAuth device authorization grant instead of loopback PKCE. Required on headless or sandboxed environments where a browser cannot be opened, but often blocked by enterprise Conditional Access policy.</param>
    /// <param name="tenant">AAD tenant ID, domain, or 'organizations' (default). Use a specific tenant when your account is a guest in multiple directories.</param>
    /// <param name="noBrowser">Print the authorize URL instead of launching the system browser. Use when running over SSH or when the OS browser launcher is unreliable.</param>
    /// <param name="identity">Required explicit alias of the identity to register or re-enroll; no attached or ambient account is inferred.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="provider">Authentication provider: ado (default) or github. GitHub always uses device flow.</param>
    /// <param name="clientId">Registered GitHub App client ID (github only); defaults to github.appclientid.</param>
    [Command("auth login")]
    public async Task<int> AuthLogin(
        bool deviceCode = false,
        string? tenant = null,
        bool noBrowser = false,
        string? identity = null,
        string output = OutputFormats.Default,
        CancellationToken ct = default,
        string provider = "ado",
        string? clientId = null)
        => provider == "github" && tenant is null
            ? await services.GetRequiredService<AuthGitHubCommand>().ExecuteAsync("login", identity, clientId, output, ct)
            : provider == "ado" && clientId is null
                ? await services.GetRequiredService<AuthLoginCommand>().ExecuteAsync(deviceCode, tenant, noBrowser, identity, output, ct)
                : AuthProviderUsageError(output);

    private int AuthProviderUsageError(string output)
    {
        CommandError.Write(new RendererFactory(), Console.Error, output,
            "Choose --provider ado or --provider github with compatible options.");
        return 2;
    }

    /// <summary>List every registered identity (AAD and PAT) with safe principal metadata. Credential blobs are never surfaced; AAD rows show tenant/objectId/authorityHost, PAT rows show adoPrincipalId/adoAuthority.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("auth identities")]
    public async Task<int> AuthIdentities(string output = OutputFormats.Default, CancellationToken ct = default)
        => await services.GetRequiredService<AuthIdentitiesCommand>().ExecuteAsync(output, ct);

    /// <summary>Enroll or renew a Personal Access Token identity. The PAT is read from a non-echoing Spectre prompt on a TTY, or from stdin when --stdin is set or stdin is redirected; it is never accepted as an argument. The service performs authoritative read-only principal attestation before touching any stored credential.</summary>
    /// <param name="identity">Required explicit alias of the PAT identity to register or renew; no attached or ambient account is inferred.</param>
    /// <param name="org">Azure DevOps organization name or HTTPS endpoint. Omit to use the named identity's registered PAT authority; a new alias can use the attached organization. Required for new enrollment outside attachment.</param>
    /// <param name="stdin">Read the PAT from standard input instead of prompting. Automatic when stdin is redirected (e.g. piped).</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("auth pat")]
    public async Task<int> AuthPat(
        string? identity = null,
        string? org = null,
        bool stdin = false,
        string output = OutputFormats.Default,
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
        string output = OutputFormats.Default,
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
        string output = OutputFormats.Default,
        CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionListCommand>().ExecuteAsync(org, project, output, ct);

    /// <summary>Show the identity + binding the current attached worktree resolves to. Fails with setup guidance when there is no attachment or the binding is ambiguous; no fallback.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("connection status")]
    public async Task<int> ConnectionStatus(string output = OutputFormats.Default, CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionStatusCommand>().ExecuteAsync(output, ct);

    /// <summary>Inspect native remote-write intents, immutable acknowledgments and uncertainty blockers for the attached checkout. Read-only; never replay, expire or discard an unknown write.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("connection writes")]
    public async Task<int> ConnectionWrites(string output = OutputFormats.Default, CancellationToken ct = default)
        => await services.GetRequiredService<ConnectionWritesCommand>().ExecuteAsync(output: output, ct: ct);

    /// <summary>Show the current version.</summary>
    public Task<int> Version()
    {
        Console.WriteLine(VersionHelper.GetVersion());
        return Task.FromResult(0);
    }

    // ── Proposal lifecycle (native, formerly "plan") ──────────────────

    /// <summary>Validate a proposal's canonical file format offline. Preview separately checks application evidence; no mutation.</summary>
    /// <param name="file">Required proposal JSON file inside the current workspace root; Core requires its current format without translating older bytes.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("proposal validate")]
    public async Task<int> ProposalValidate(string? file = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.ValidateProposalAsync(file, output, ct);
        return MissingCore(output);
    }

    /// <summary>Preview a proposal with an optional digest guard. No ADO mutation.</summary>
    /// <param name="file">Path to a current canonical Core proposal JSON file inside the workspace root.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    /// <param name="expectDigest">Refuse unless the current canonical digest matches exactly, before import or review.</param>
    [Command("proposal preview")]
    public async Task<int> ProposalPreview(
        string? file = null, string output = OutputFormats.Default,
        string? expectDigest = null, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
        {
            return await core.PreviewProposalAsync(file, output, ct, expectDigest);
        }
        return MissingCore(output);
    }

    /// <summary>Apply a proposal only when --confirm &lt;digest&gt; matches its current canonical digest exactly.</summary>
    /// <param name="file">Path to a current canonical Core proposal JSON file inside the workspace root.</param>
    /// <param name="confirm">Lowercase-hex SHA-256 digest of the canonical proposal bytes. Must match exactly.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("proposal apply")]
    public async Task<int> ProposalApply(string? file = null, string? confirm = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.ApplyProposalAsync(file, confirm, output, ct);
        return MissingCore(output);
    }

    /// <summary>Inspect journal states and receipts for an exact proposal digest without applying or settling it; exit 1 when absent.</summary>
    /// <param name="file">Required proposal JSON file inside the current workspace root; its current canonical digest selects the journal.</param>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("proposal status")]
    public async Task<int> ProposalStatus(string? file = null, string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.ProposalStatusAsync(file, output, ct);
        return MissingCore(output);
    }

    /// <summary>Inspect captured typed draft fields, original baselines and seed relationships in retained draft order. Read-only.</summary>
    /// <param name="output">-o, Output format: human, json, minimal.</param>
    [Command("pending")]
    public async Task<int> Pending(string output = OutputFormats.Default, CancellationToken ct = default)
    {
        if (services.GetService<CoreCommandAdapter>() is { } core)
            return await core.PendingAsync(output, ct);
        return MissingCore(output);
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
