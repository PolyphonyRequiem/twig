using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Providers.Native;

namespace Twig.Infrastructure.Services;

internal sealed record NativeWorkspaceSetupArguments(string WorkspaceRoot, string Organization, string Project,
    string? Team, string? GitProject, string? Provider, string? Host, string? Repository,
    string? Binding, string? StoreFile, string? Account, string? GlobalDisplayPath = null, string? Database = null,
    NativeGitHubAppSetupTarget? GitHubApp = null, Connection? ExpectedNativeAuthority = null);
internal sealed record NativeCreationReceipt(CoreWorkspaceDocument Document, NativeSourceStamp Source, string? NativeDatabase = null);

internal static class NativeWorkspaceSetup
{
    private const string RetryGuidance = "Stop active setup and inspect the preserved checkout-local .twig state. Move it aside only if it is disposable, or retry in a fresh worktree. Keep source, credentials and any remote-write evidence; use a new custom Core store path if any SQLite files exist.";

    internal static bool ValidGitHubAccount(string? account)
        => !string.IsNullOrWhiteSpace(account) && account.Length <= 256 && !account.StartsWith('-')
            && !account.Any(char.IsWhiteSpace) && !account.Any(char.IsControl);

    internal static async Task<Result<NativeCreationReceipt>> InitializeAsync(NativeWorkspaceSetupArguments args,
        Func<IConnectionBindingService> lazyBindings, NativeSetupEffects effects, CancellationToken ct,
        CoreTransportOverride? transport = null, IBoundedProcessRunner? processes = null)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!WorktreeAnchorDetector.TryDetect(args.WorkspaceRoot, out var anchor, out var problem)
                || !WorktreeAnchorDetector.TryIsWorktreeRoot(args.WorkspaceRoot, out var isRoot) || !isRoot)
                return Fail("Native Core init requires invocation at a Git worktree root. " + problem);
            var twigDir = Path.Combine(anchor.WorktreeRoot, ".twig");
            if (File.Exists(twigDir) || Directory.Exists(twigDir)
                && ((File.GetAttributes(twigDir) & FileAttributes.ReparsePoint) != 0
                    || Directory.EnumerateFileSystemEntries(twigDir).Any(path => !IsEmptyCheckoutLock(path))))
                return Fail("Init refuses existing nonempty, linked, partial or legacy .twig state. " + RetryGuidance + " No state was changed.");
            var database = args.StoreFile ?? Path.Combine(twigDir, "core.db");
            if (!Path.IsPathFullyQualified(database))
                return Fail("--core-store must name a new absolute file; its parent directory must already exist.", 2);
            database = Path.GetFullPath(database);
            var parent = Path.GetDirectoryName(database);
            var fileName = Path.GetFileName(database);
            if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || fileName.EndsWith('.') || fileName.EndsWith(' ') || Directory.Exists(database)
                || args.StoreFile is not null && !Directory.Exists(parent))
                return Fail("--core-store must name a new file in an existing directory; no directory repair was attempted.", 2);
            if (File.Exists(database) || File.Exists(database + "-wal") || File.Exists(database + "-shm"))
                return Fail("Init refuses existing database files or partial SQLite evidence. Select a genuinely new file in a fresh worktree; existing state was preserved.");
            var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), database,
                anchor.WorktreeRoot, args.GlobalDisplayPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(database, paths.RepoConfigPath, comparison)
                || string.Equals(database, paths.PolicyPath, comparison)
                || string.Equals(database, Path.Combine(anchor.WorktreeRoot, ".gitignore"), comparison))
                return Fail("--core-store cannot replace a native configuration, policy or ignore file.", 2);
            var hasManifest = File.Exists(paths.RepoConfigPath);
            var config = hasManifest ? await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false) : new TwigConfiguration();
            var kind = args.Provider is null ? config.ResolveWorkItemProviderKind() : WorkItemProviderSetting.Normalize(args.Provider);
            if (kind is not ("ado" or "github" or "twig")) return Fail("provider-unknown: use --provider ado, github or twig.", 2);
            if (hasManifest && kind != config.ResolveWorkItemProviderKind())
                return Fail("Provider conflicts with the existing twig.json declaration; no configuration was changed.", 2);
            config.Provider = kind;
            if (args.GitHubApp is { } app && (kind != "github" || args.Account is not null
                || args.Host != app.Host || args.Repository != app.Repository || !NativeConnectionSetup.ValidGitHubApp(app)))
                return Fail("Choose one named gh account or one explicit github.com App identity and client ID.", 2);
            if (kind != "twig" && args.Database is not null)
                return Fail("--database requires --provider twig. --core-store selects the separate client store.", 2);
            if (kind == "github")
            {
                if (args.Organization.Length != 0 || args.Project.Length != 0 || args.Team is not null
                    || args.GitProject is not null || args.Binding is not null)
                    return Fail("GitHub init uses --host, --repository and --account, not ADO coordinates/team/git-project/binding.", 2);
                if (args.Account is not null && !ValidGitHubAccount(args.Account))
                    return Fail("--account must name an explicit gh account; the active gh account and ambient tokens are never inferred.", 2);
                if (hasManifest && (args.Host is not null && args.Host != config.GitHub?.Host
                    || args.Repository is not null && args.Repository != config.GitHub?.Repository))
                    return Fail("GitHub coordinates conflict with the existing twig.json declaration.", 2);
                if (!hasManifest) config.GitHub = new GitHubConfig { Host = args.Host, Repository = args.Repository };
                if (!GitHubWorkspaceScope.TryCreate(config.GitHub, out _, out var scopeProblem)) return Fail(scopeProblem, 2);
                if (args.Account is not null)
                {
                    config.GitHubAccount = args.Account;
                    config.GitHubCredentialSource = "gh";
                    config.GitHubOAuthIdentity = null;
                    config.GitHubAppClientId = null;
                }
                else if (args.GitHubApp is { } selected)
                {
                    config.GitHubCredentialSource = "github-app";
                    config.GitHubOAuthIdentity = selected.Identity;
                    config.GitHubAppClientId = selected.ClientId;
                    config.GitHubAccount = null;
                }
            }
            else if (kind == "twig")
            {
                if (args.Organization.Length != 0 || args.Project.Length != 0 || args.Team is not null
                    || args.GitProject is not null || args.Binding is not null || args.Host is not null
                    || args.Repository is not null || args.Account is not null)
                    return Fail("Twig-native init uses --database <name>, not ADO/GitHub coordinates or credentials.", 2);
                if (hasManifest && args.Database is not null && args.Database != config.NativeDatabase)
                    return Fail("The native database conflicts with the existing twig.json declaration; no configuration was changed.", 2);
                var name = args.Database ?? config.NativeDatabase;
                if (!NativeDatabaseName.IsValid(name))
                    return Fail("Choose --database <name> (or nativeDatabase in twig.json): 1-64 lowercase letters, digits or internal hyphens. No default database is inferred.", 2);
                config.NativeDatabase = name;
            }
            else
            {
                if (args.Host is not null || args.Repository is not null || args.Account is not null)
                    return Fail("--host, --repository and --account require --provider github.", 2);
                if (hasManifest && args.Organization.Length == 0 && args.Project.Length == 0)
                    args = args with { Organization = config.Organization, Project = config.Project };
                if (NativeSetupEnrollment.AdoProblem(args.Organization, args.Project) is { } adoProblem) return Fail(adoProblem, 2);
                if (hasManifest && (args.Organization != config.Organization || args.Project != config.Project
                    || args.Team is not null && args.Team != config.Team || args.GitProject is not null && args.GitProject != config.Git.Project))
                    return Fail("ADO coordinates conflict with the existing twig.json declaration.", 2);
                config.Organization = args.Organization;
                config.Project = args.Project;
                if (args.Team is not null) config.Team = args.Team;
                if (args.GitProject is not null) config.Git.Project = args.GitProject;
                if (args.Binding is not null && string.IsNullOrWhiteSpace(args.Binding))
                    return Fail("--binding requires an exact registered native binding ID.", 2);
            }

            ct.ThrowIfCancellationRequested();
            effects.Phase = NativeSetupPhase.Initialization;
            effects.Before(NativeSetupEffectKind.NativeHome);
            var bindings = lazyBindings();
            // An explicit device connection survives workspace setup failure. Registering it
            // does not admit the checkout; workspace initialization still requires the lease.
            if (kind == "github" && (args.Account is not null || args.GitHubApp is not null)
                || kind == "twig" && args.Database is not null && args.ExpectedNativeAuthority is null)
            {
                var requirement = ProviderConnectionRequirement.FromConfiguration(config);
                var catalog = new DeviceConnectionCatalog(bindings.RegistryPath);
                effects.Before(NativeSetupEffectKind.ConnectionRegistration);
                if (kind == "github")
                {
                    var credential = args.GitHubApp is { } selection
                        ? new ProviderConnectionCredential("github-app", selection.Identity, selection.ClientId)
                        : new ProviderConnectionCredential("gh", args.Account!);
                    effects.Before(NativeSetupEffectKind.ConnectionBinding);
                    await catalog.RegisterGitHubAsync(requirement, credential, ct).ConfigureAwait(false);
                    effects.Confirm(NativeSetupEffectKind.ConnectionBinding);
                }
                else await catalog.AddAsync(requirement, ct).ConfigureAwait(false);
                effects.Confirm(NativeSetupEffectKind.ConnectionRegistration);
            }
            else if (kind == "ado" && args.Binding is null || kind == "github"
                || kind == "twig" && hasManifest && args.Database is null)
            {
                try
                {
                    var requirement = ProviderConnectionRequirement.FromConfiguration(config);
                    var registered = await new DeviceConnectionCatalog(bindings.RegistryPath)
                        .RequireAsync(requirement, ct, config.Organization, config.Project).ConfigureAwait(false);
                    if (kind == "ado")
                    {
                        args = args with { Binding = registered.BindingId ?? throw new InvalidOperationException("No exact ADO binding was selected.") };
                    }
                    else if (kind == "github")
                    {
                        var credential = registered.Credential!;
                        config.GitHubCredentialSource = credential.Kind;
                        config.GitHubAccount = credential.Kind == "gh" ? credential.Selector : null;
                        config.GitHubOAuthIdentity = credential.Kind == "github-app" ? credential.Selector : null;
                        config.GitHubAppClientId = credential.ClientId;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    return effects.Fail<NativeCreationReceipt>(new(ErrorCode.Unavailable, ex.Message));
                }
            }
            if (kind == "ado")
            {
                effects.Before(NativeSetupEffectKind.Registry);
                var bootstrap = await bindings.CreateBootstrapProviderAsync(config, ct, args.Binding).ConfigureAwait(false);
                if (bootstrap is IDisposable disposable) disposable.Dispose();
                effects.Confirm(NativeSetupEffectKind.Registry);
            }
            effects.Before(NativeSetupEffectKind.WorkspaceDirectory);
            Directory.CreateDirectory(twigDir);
            effects.Confirm(NativeSetupEffectKind.WorkspaceDirectory);
            using var checkout = ConnectionOperationGate.Acquire(anchor.WorktreeRoot, exclusive: true);
            effects.Before(NativeSetupEffectKind.InitializationLock);
            if ((File.GetAttributes(twigDir) & FileAttributes.ReparsePoint) != 0
                || Directory.EnumerateFileSystemEntries(twigDir).Any(entry => !IsEmptyCheckoutLock(entry)))
                return Fail("Init found existing .twig data under the checkout lease. No workspace data was adopted. " + RetryGuidance);
            var lockPath = Path.Combine(twigDir, "core-init.lock");
            using var initialization = new FileStream(lockPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            effects.Confirm(NativeSetupEffectKind.InitializationLock);
            var ignorePath = Path.Combine(anchor.WorktreeRoot, ".gitignore");
            var ignore = File.Exists(ignorePath) ? await File.ReadAllTextAsync(ignorePath, ct).ConfigureAwait(false) : "";
            var lastRule = ignore.Split('\n').Select(line => line.TrimEnd('\r'))
                .LastOrDefault(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'));
            if (lastRule is not (".twig/" or "/.twig/"))
            {
                effects.Before(NativeSetupEffectKind.IgnoreFile);
                await File.AppendAllTextAsync(ignorePath,
                    (ignore.Length > 0 && !ignore.EndsWith('\n') ? Environment.NewLine : "") + "/.twig/" + Environment.NewLine, ct).ConfigureAwait(false);
                effects.Confirm(NativeSetupEffectKind.IgnoreFile);
            }
            effects.Before(NativeSetupEffectKind.Configuration);
            if (!hasManifest)
                await WriteNewAsync(paths.RepoConfigPath, await config.GetRepoBytesAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            if (await config.GetUserBytesAsync(ct).ConfigureAwait(false) is { } userBytes)
                await WriteNewAsync(paths.ConfigPath, userBytes, ct).ConfigureAwait(false);
            else if (Path.Exists(paths.ConfigPath))
                return Fail("A checkout config appeared during init. It was preserved. " + RetryGuidance);
            effects.Confirm(NativeSetupEffectKind.Configuration);
            if (kind == "ado")
            {
                effects.Before(NativeSetupEffectKind.Attachment);
                using var attachment = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System, args.Binding);
                var attached = await attachment.InitializeAsync(ct).ConfigureAwait(false);
                if (!attached.IsSuccess) return effects.Fail<NativeCreationReceipt>(attached.Error, RetryGuidance);
                effects.Confirm(NativeSetupEffectKind.Attachment);
                effects.Before(NativeSetupEffectKind.Registry);
                using var registry = new SqliteSystemWorktreeRegistry(bindings.RegistryPath, TimeProvider.System);
                var connection = ConnectionRefResolver.Compute(config);
                effects.Before(NativeSetupEffectKind.ConnectionRegistration);
                var registered = await registry.UpsertConnectionAsync(connection, config.Organization, config.Project, config.Team, ct).ConfigureAwait(false);
                if (!registered.IsSuccess) return effects.Fail<NativeCreationReceipt>(registered.Error, RetryGuidance);
                effects.Confirm(NativeSetupEffectKind.ConnectionRegistration);
                effects.Before(NativeSetupEffectKind.WorktreeRegistration);
                registered = await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, anchor.WorktreeRoot, ct).ConfigureAwait(false);
                if (!registered.IsSuccess) return effects.Fail<NativeCreationReceipt>(registered.Error, RetryGuidance);
                effects.Confirm(NativeSetupEffectKind.WorktreeRegistration);
                effects.Confirm(NativeSetupEffectKind.Registry);
            }
            // The checkout lease excludes cooperating operations, not external replacements.
            // Do not unlink setup artifacts by path, even if their bytes still match.
            var result = await CoreWorkspaceInitializer.InitializeWithEvidenceAsync(config, paths, bindings,
                database, ct, transport, processes, effects, args.ExpectedNativeAuthority).ConfigureAwait(false);
            return result.IsSuccess ? result : effects.Fail<NativeCreationReceipt>(result.Error, RetryGuidance);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return effects.Cancel<NativeCreationReceipt>(FailureGuidance()); }
        catch (Exception ex) when (NativeSetupEffects.IsExpected(ex)) { return effects.Exception<NativeCreationReceipt>(ex, FailureGuidance()); }

        string? FailureGuidance() => effects.Started(NativeSetupEffectKind.WorkspaceDirectory) ? RetryGuidance : null;

        Result<NativeCreationReceipt> Fail(string message, int exit = 1)
        {
            effects.CommandExitCode = exit;
            return effects.Fail<NativeCreationReceipt>(new(exit == 2 ? ErrorCode.InvalidInput : ErrorCode.Conflict, message));
        }

        static bool IsEmptyCheckoutLock(string path)
            => Path.GetFileName(path) == "binding-operation.lock"
                && (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                && new FileInfo(path).Length == 0;

        static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await file.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
    }
}
