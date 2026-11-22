using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;

namespace Twig.Infrastructure.Services;

internal sealed record NativeWorkspaceSetupArguments(string WorkspaceRoot, string Organization, string Project,
    string? Team, string? GitProject, string? Provider, string? Host, string? Repository,
    string? Binding, string? StoreFile, string? Account, string? GlobalDisplayPath = null, string? Database = null,
    NativeGitHubAppSetupTarget? GitHubApp = null, Connection? ExpectedNativeAuthority = null);
internal sealed record NativeCreationReceipt(CoreWorkspaceDocument Document, NativeSourceStamp Source, string? NativeDatabase = null);

internal static class NativeWorkspaceSetup
{
    private const string RetryGuidance = "Stop active setup and inspect the preserved checkout-local .twig state. Move it aside only if it is disposable, or retry in a fresh worktree. Keep source, credentials and any remote-write evidence; use a new custom Core store path if any SQLite files exist.";

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
            var provider = NativeSetupPreflight.ConfigureProvider(args, config, hasManifest);
            if (!provider.IsSuccess) return Fail(provider.Error.Message, 2);
            var kind = provider.Value;

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
