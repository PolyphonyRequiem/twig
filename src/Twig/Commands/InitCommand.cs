using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>Creates one fresh, natively bound Core store. Existing state is never adopted or reset.</summary>
public sealed class InitCommand
{
    private readonly TwigPaths _paths;
    private readonly RendererFactory _renderers;
    private readonly Func<IConnectionBindingService> _bindings;
    private readonly ITelemetryClient? _telemetry;
    private readonly CoreTransportOverride? _transport;
    private readonly IBoundedProcessRunner? _githubProcesses;

    internal InitCommand(TwigPaths paths, RendererFactory renderers, Func<IConnectionBindingService> bindings,
        ITelemetryClient? telemetry = null, CoreTransportOverride? transport = null, IBoundedProcessRunner? githubProcesses = null)
    {
        _paths = paths;
        _renderers = renderers;
        _bindings = bindings;
        _telemetry = telemetry;
        _transport = transport;
        _githubProcesses = githubProcesses;
    }

    public async Task<int> ExecuteAsync(string org, string project, string? team = null, string? gitProject = null,
        bool force = false, string outputFormat = OutputFormatterFactory.DefaultFormat, string? sprint = null,
        string? area = null, bool reinitialize = false, CancellationToken ct = default, string? profile = null,
        string? provider = null, string? host = null, string? repository = null, string? binding = null,
        string? coreStore = null, string? account = null)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        try
        {
            exit = await InitializeAsync();
            return exit;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or TwigConfigurationException or System.Text.Json.JsonException
            or Microsoft.Data.Sqlite.SqliteException or HttpRequestException
            or Twig.Infrastructure.Ado.Exceptions.AdoException)
        {
            return Fail("Native Core initialization did not complete; any created evidence is preserved. " + ex.Message);
        }
        finally
        {
            // Optional instrumentation cannot change the command's outcome.
            try { TelemetryHelper.TrackCommand(_telemetry, "init", outputFormat, exit, started); }
            catch (Exception) { }
        }

        int Fail(string message, int code = 1)
        {
            CommandError.Write(_renderers, Console.Error, outputFormat, message);
            return code;
        }

        async Task<int> InitializeAsync()
        {
            if (force || reinitialize || sprint is not null || area is not null || profile is not null)
                return Fail("--force, --reinitialize, --sprint, --area and --profile are retired. Init only creates a fresh Core workspace; it never resets, migrates or prefills old state.", 2);
            if (OutputFormats.Normalize(outputFormat) == "ids")
                return Fail("Init has no work-item IDs to emit. Use human, json, json-full, json-compact or minimal.", 2);
            if (!WorktreeAnchorDetector.TryDetect(_paths.StartDir, out var anchor, out var problem)
                || !WorktreeAnchorDetector.TryIsWorktreeRoot(_paths.StartDir, out var isRoot) || !isRoot)
                return Fail("Native Core init requires invocation at a Git worktree root. " + problem);

            var twigDir = Path.Combine(anchor.WorktreeRoot, ".twig");
            if (Directory.Exists(twigDir) || File.Exists(twigDir))
                return Fail("Init refuses existing, partial or legacy .twig state. Use a fresh worktree; manually preserve or discard old state separately. No migration, deletion or uncertain-write settlement was performed.");
            var database = coreStore ?? Path.Combine(twigDir, "core.db");
            if (!Path.IsPathFullyQualified(database))
                return Fail("--core-store must name a new absolute file; its parent directory must already exist.", 2);
            database = Path.GetFullPath(database);
            var parent = Path.GetDirectoryName(database);
            var fileName = Path.GetFileName(database);
            if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || fileName.EndsWith('.') || fileName.EndsWith(' ')
                || Directory.Exists(database)
                || coreStore is not null && !Directory.Exists(parent))
                return Fail("--core-store must name a new file in an existing directory; no directory repair was attempted.", 2);
            if (File.Exists(database)
                || File.Exists(database + "-wal") || File.Exists(database + "-shm"))
                return Fail("Init refuses existing database files or partial SQLite evidence. Select a genuinely new file in a fresh worktree; existing state was preserved.");

            var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), database,
                anchor.WorktreeRoot, _paths.GlobalDisplayPath);
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(database, paths.RepoConfigPath, pathComparison)
                || string.Equals(database, paths.PolicyPath, pathComparison)
                || string.Equals(database, Path.Combine(anchor.WorktreeRoot, ".gitignore"), pathComparison))
                return Fail("--core-store cannot replace a native configuration, policy or ignore file.", 2);
            var hasManifest = File.Exists(paths.RepoConfigPath);
            var config = hasManifest ? await TwigConfiguration.LoadSplitAsync(paths, ct) : new TwigConfiguration();
            var kind = provider is null ? config.ResolveWorkItemProviderKind() : WorkItemProviderSetting.Normalize(provider);
            if (kind is not ("ado" or "github"))
                return Fail("provider-unknown: use --provider ado or --provider github.", 2);
            if (hasManifest && kind != config.ResolveWorkItemProviderKind())
                return Fail("Provider conflicts with the existing twig.json declaration; no configuration was changed.", 2);
            config.Provider = kind;
            if (kind == "github")
            {
                if (org.Length != 0 || project.Length != 0 || team is not null || gitProject is not null || binding is not null)
                    return Fail("GitHub init uses --host, --repository and --account, not ADO coordinates/team/git-project/binding.", 2);
                if (string.IsNullOrWhiteSpace(account) || account != account.Trim()
                    || account.Any(char.IsWhiteSpace) || account.Any(char.IsControl) || account.StartsWith('-'))
                    return Fail("GitHub Core initialization requires --account <gh-account>; the active gh account and ambient tokens are never inferred.", 2);
                if (hasManifest && (host is not null && host != config.GitHub?.Host
                    || repository is not null && repository != config.GitHub?.Repository))
                    return Fail("GitHub coordinates conflict with the existing twig.json declaration.", 2);
                if (!hasManifest) config.GitHub = new GitHubConfig { Host = host, Repository = repository };
                if (!GitHubWorkspaceScope.TryCreate(config.GitHub, out _, out var scopeProblem))
                    return Fail(scopeProblem, 2);
                config.GitHubAccount = account;
                config.GitHubCredentialSource = "gh";
            }
            else
            {
                if (host is not null || repository is not null || account is not null)
                    return Fail("--host, --repository and --account require --provider github.", 2);
                if (string.IsNullOrWhiteSpace(org) || org != org.Trim() || org.Any(char.IsControl)
                    || string.IsNullOrWhiteSpace(project) || project != project.Trim() || project.Any(char.IsControl)
                    || !Uri.TryCreate(AdoRestClient.NormalizeOrgUrl(org).TrimEnd('/') + "/", UriKind.Absolute, out var endpoint)
                    || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                    return Fail("Supply an explicit secure ADO organization and non-empty project: twig init <org> <project>.", 2);
                if (hasManifest && (!string.Equals(org, config.Organization, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(project, config.Project, StringComparison.OrdinalIgnoreCase)
                    || team is not null && team != config.Team || gitProject is not null && gitProject != config.Git.Project))
                    return Fail("ADO coordinates conflict with the existing twig.json declaration.", 2);
                config.Organization = org;
                config.Project = project;
                if (team is not null) config.Team = team;
                if (gitProject is not null) config.Git.Project = gitProject;
                if (binding is not null && string.IsNullOrWhiteSpace(binding))
                    return Fail("--binding requires an exact registered native binding ID.", 2);
            }

            // Resolve the service only after all filesystem/input preflight. Its constructor owns native registry setup.
            var bindings = _bindings();
            if (kind == "ado")
            {
                var bootstrap = await bindings.CreateBootstrapProviderAsync(config, ct, binding);
                if (bootstrap is IDisposable disposable) disposable.Dispose();
            }
            Directory.CreateDirectory(twigDir);
            using var initialization = new FileStream(Path.Combine(twigDir, "core-init.lock"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            var ignorePath = Path.Combine(anchor.WorktreeRoot, ".gitignore");
            var ignore = File.Exists(ignorePath) ? await File.ReadAllTextAsync(ignorePath, ct) : "";
            var lastRule = ignore.Split('\n').Select(line => line.TrimEnd('\r'))
                .LastOrDefault(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'));
            if (lastRule is not (".twig/" or "/.twig/"))
                await File.AppendAllTextAsync(ignorePath,
                    (ignore.Length > 0 && !ignore.EndsWith('\n') ? Environment.NewLine : "") + "/.twig/" + Environment.NewLine, ct);
            if (hasManifest) await config.SaveUserAsync(paths.ConfigPath, ct);
            else await config.SaveSplitAsync(paths, ct);

            if (kind == "ado")
            {
                using var attachment = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System, binding);
                var attached = await attachment.InitializeAsync(ct);
                if (!attached.IsSuccess) return Fail(attached.Error);
                using var registry = new SqliteSystemWorktreeRegistry(bindings.RegistryPath, TimeProvider.System);
                var connection = ConnectionRefResolver.Compute(config);
                var registered = await registry.UpsertConnectionAsync(connection, config.Organization, config.Project, config.Team, ct);
                if (!registered.IsSuccess) return Fail(registered.Error);
                registered = await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor),
                    connection, anchor.WorktreeRoot, ct);
                if (!registered.IsSuccess) return Fail(registered.Error);
            }
            var result = await CoreWorkspaceInitializer.InitializeAsync(config, paths, bindings, database, ct,
                _transport, _githubProcesses);
            if (!result.IsSuccess) return Fail(result.Error.Message);
            _renderers.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(
                [new RenderNode.Record("initialized", new Dictionary<string, RenderCell>
                {
                    ["coreInitialized"] = RenderCell.Boolean(true),
                    ["databasePath"] = RenderCell.String(database),
                    ["message"] = RenderCell.String("Initialized a new natively bound Core workspace; no legacy cache or existing state was adopted."
                        + (coreStore is not null && !database.StartsWith(twigDir + Path.DirectorySeparatorChar,
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                            ? " Custom store files outside .twig are not automatically Git-ignored; protect the database and SQLite sidecars separately." : "")),
                })]));
            return 0;
        }
    }
}
