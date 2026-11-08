using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;

namespace Twig.Infrastructure.Services;

internal sealed record NativeWorkspaceSetupArguments(string WorkspaceRoot, string Organization, string Project,
    string? Team, string? GitProject, string? Provider, string? Host, string? Repository,
    string? Binding, string? StoreFile, string? Account, string? GlobalDisplayPath = null);
internal sealed record NativeCreationReceipt(CoreWorkspaceDocument Document, NativeSourceStamp Source);

internal static class NativeWorkspaceSetup
{
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
            if (Directory.Exists(twigDir) || File.Exists(twigDir))
                return Fail("Init refuses existing, partial or legacy .twig state. Use a fresh worktree; manually preserve or discard old state separately. No migration, deletion or uncertain-write settlement was performed.");
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
            if (kind is not ("ado" or "github")) return Fail("provider-unknown: use --provider ado or --provider github.", 2);
            if (hasManifest && kind != config.ResolveWorkItemProviderKind())
                return Fail("Provider conflicts with the existing twig.json declaration; no configuration was changed.", 2);
            config.Provider = kind;
            if (kind == "github")
            {
                if (args.Organization.Length != 0 || args.Project.Length != 0 || args.Team is not null
                    || args.GitProject is not null || args.Binding is not null)
                    return Fail("GitHub init uses --host, --repository and --account, not ADO coordinates/team/git-project/binding.", 2);
                if (string.IsNullOrWhiteSpace(args.Account) || args.Account != args.Account.Trim()
                    || args.Account.Any(char.IsWhiteSpace) || args.Account.Any(char.IsControl) || args.Account.StartsWith('-'))
                    return Fail("GitHub Core initialization requires --account <gh-account>; the active gh account and ambient tokens are never inferred.", 2);
                if (hasManifest && (args.Host is not null && args.Host != config.GitHub?.Host
                    || args.Repository is not null && args.Repository != config.GitHub?.Repository))
                    return Fail("GitHub coordinates conflict with the existing twig.json declaration.", 2);
                if (!hasManifest) config.GitHub = new GitHubConfig { Host = args.Host, Repository = args.Repository };
                if (!GitHubWorkspaceScope.TryCreate(config.GitHub, out _, out var scopeProblem)) return Fail(scopeProblem, 2);
                config.GitHubAccount = args.Account;
                config.GitHubCredentialSource = "gh";
            }
            else
            {
                if (args.Host is not null || args.Repository is not null || args.Account is not null)
                    return Fail("--host, --repository and --account require --provider github.", 2);
                if (NativeSetupEnrollment.AdoProblem(args.Organization, args.Project) is { } adoProblem) return Fail(adoProblem, 2);
                if (hasManifest && (!string.Equals(args.Organization, config.Organization, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(args.Project, config.Project, StringComparison.OrdinalIgnoreCase)
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
            effects.Before(NativeSetupEffectKind.InitializationLock);
            using var initialization = new FileStream(Path.Combine(twigDir, "core-init.lock"), FileMode.CreateNew,
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
            if (hasManifest) await config.SaveUserAsync(paths.ConfigPath, ct).ConfigureAwait(false);
            else await config.SaveSplitAsync(paths, ct).ConfigureAwait(false);
            effects.Confirm(NativeSetupEffectKind.Configuration);
            if (kind == "ado")
            {
                effects.Before(NativeSetupEffectKind.Attachment);
                using var attachment = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System, args.Binding);
                var attached = await attachment.InitializeAsync(ct).ConfigureAwait(false);
                if (!attached.IsSuccess) return effects.Fail<NativeCreationReceipt>(attached.Error);
                effects.Confirm(NativeSetupEffectKind.Attachment);
                effects.Before(NativeSetupEffectKind.Registry);
                using var registry = new SqliteSystemWorktreeRegistry(bindings.RegistryPath, TimeProvider.System);
                var connection = ConnectionRefResolver.Compute(config);
                effects.Before(NativeSetupEffectKind.ConnectionRegistration);
                var registered = await registry.UpsertConnectionAsync(connection, config.Organization, config.Project, config.Team, ct).ConfigureAwait(false);
                if (!registered.IsSuccess) return effects.Fail<NativeCreationReceipt>(registered.Error);
                effects.Confirm(NativeSetupEffectKind.ConnectionRegistration);
                effects.Before(NativeSetupEffectKind.WorktreeRegistration);
                registered = await registry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor), connection, anchor.WorktreeRoot, ct).ConfigureAwait(false);
                if (!registered.IsSuccess) return effects.Fail<NativeCreationReceipt>(registered.Error);
                effects.Confirm(NativeSetupEffectKind.WorktreeRegistration);
                effects.Confirm(NativeSetupEffectKind.Registry);
            }
            var result = await CoreWorkspaceInitializer.InitializeWithEvidenceAsync(config, paths, bindings,
                database, ct, transport, processes, effects).ConfigureAwait(false);
            return result.IsSuccess ? result : effects.Fail<NativeCreationReceipt>(result.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return effects.Cancel<NativeCreationReceipt>(); }
        catch (Exception ex) when (NativeSetupEffects.IsExpected(ex)) { return effects.Exception<NativeCreationReceipt>(ex); }

        Result<NativeCreationReceipt> Fail(string message, int exit = 1)
        {
            effects.CommandExitCode = exit;
            return effects.Fail<NativeCreationReceipt>(new(exit == 2 ? ErrorCode.InvalidInput : ErrorCode.Conflict, message));
        }
    }
}
