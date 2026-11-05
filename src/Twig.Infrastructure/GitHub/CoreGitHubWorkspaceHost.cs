using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Policy;
using Twig.Infrastructure.Services;
using Twig.Providers.GitHub;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.GitHub;

internal sealed class CoreGitHubWorkspaceHost(TwigConfiguration configuration, TwigPaths paths,
    IConnectionBindingService nativeBindings, IGitHubTokenSource? selectedCredential = null, CoreTransportOverride? transport = null,
    IBoundedProcessRunner? processes = null)
{
    internal async Task<Result<bool>> InitializeAsync(string databasePath, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(databasePath) || File.Exists(databasePath)
            || File.Exists(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName))
            || !Directory.Exists(Path.GetDirectoryName(databasePath)))
            return Result<bool>.Fail(ErrorCode.InvalidInput, "GitHub Core initialization requires a new explicit absolute store file; existing files were preserved.");
        if (!GitHubWorkspaceScope.TryCreate(configuration.GitHub, out var scope, out var problem))
            return Result<bool>.Fail(ErrorCode.InvalidInput, problem);
        try
        {
            var source = selectedCredential ?? Credential(scope!);
            var credential = await source.GetTokenAsync(ct).ConfigureAwait(false);
            using var reader = GitHubReadOnlyAuthHandler.CreateClient(new CapturedCredential(credential),
                scope!.GraphQlEndpoint, transport?.CreateHandler());
            var observed = await new GitHubRepositoryDiscovery(reader, scope).DiscoverAsync(ct).ConfigureAwait(false);
            // Both native observations use the same captured credential and exact selected endpoint.
            using var actorReader = new GitHubAuthenticatedActorReader(GitHubReadOnlyAuthHandler.CreateClient(
                new CapturedCredential(credential), scope.GraphQlEndpoint, transport?.CreateHandler()), scope);
            var actor = await actorReader.ObserveAsync(ct).ConfigureAwait(false);
            using var registry = new SqliteSystemWorktreeRegistry(nativeBindings.RegistryPath, TimeProvider.System);
            using var attachment = new WorktreeLocalProviderTargetAttachmentStore(paths, registry, TimeProvider.System);
            var selected = await attachment.ReadWithRevisionAsync(ct).ConfigureAwait(false);
            if (!selected.IsSuccess) return Result<bool>.Fail(ErrorCode.StorageUnavailable, selected.Error.Message);
            if (selected.Value.Envelope is null)
            {
                var attached = await new ProviderTargetAttachmentService(registry, attachment)
                    .AttachAsync(observed.Target.ToIdentity(scope.GraphQlEndpoint), observed.Locator, null, selected.Value.Revision, ct).ConfigureAwait(false);
                if (!attached.IsSuccess) return Result<bool>.Fail(ErrorCode.Conflict, attached.Error.Message);
            }
            var targets = new GitHubBoundTargetResolver(attachment, registry, scope);
            using var generation = new FileStream(Path.Combine(paths.TwigDir, ProviderTargetAttachmentDocument.LockFileName),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var origin = await targets.ResolveAsync(ct).ConfigureAwait(false);
            if (origin.Target != observed.Target)
                return Result<bool>.Fail(ErrorCode.Conflict, "The selected immutable GitHub target differs from the native initialization observation.");
            var current = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
            if (current.ResolveWorkItemProviderKind() != "github"
                || !GitHubWorkspaceScope.TryCreate(current.GitHub, out var live, out _) || live != scope
                || CleanCoreGitHubAccess.Identity(origin, current, actor.ActorId) != CleanCoreGitHubAccess.Identity(origin, configuration, actor.ActorId))
                return Result<bool>.Fail(ErrorCode.Conflict, "The selected GitHub configuration changed during initialization.");
            var created = await SqliteStore.CreateAsync(databasePath, ct).ConfigureAwait(false);
            if (!created.IsSuccess) return Result<bool>.Fail(created.Error);
            var connection = new Connection(new("github"), observed.ResourceScope,
                GitHubProvider.Destination(observed.Target.OwnerId, observed.Target.RepositoryId),
                CleanCoreGitHubAccess.Identity(origin, configuration, actor.ActorId));
            return await CoreWorkspaceFactory.RecordNewAsync(paths, created.Value, databasePath, connection, actor.ActorId, ct,
                nativeBindings.RegistryPath).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or HttpRequestException
            or System.Text.Json.JsonException or GitHubCredentialUnavailableException)
        {
            return Result<bool>.Fail(ErrorCode.Unavailable, "GitHub Core initialization could not establish native identity; existing data and any created files were preserved. " + ex.Message);
        }
    }

    internal async Task<Result<CoreWorkspaceHandle>> OpenAsync(CoreWorkspaceDocument document, SqliteStore store, CancellationToken ct, bool scoped = false)
    {
        SqliteSystemWorktreeRegistry? registry = null;
        WorktreeLocalProviderTargetAttachmentStore? attachment = null;
        GitHubProvider? provider = null;
        try
        {
            if (!GitHubWorkspaceScope.TryCreate(configuration.GitHub, out var scope, out var problem))
                return Result<CoreWorkspaceHandle>.Fail(ErrorCode.InvalidInput, problem);
            registry = new(nativeBindings.RegistryPath, TimeProvider.System);
            attachment = new(paths, registry, TimeProvider.System);
            var targets = new GitHubBoundTargetResolver(attachment, registry, scope!);
            var origin = await targets.ResolveAsync(ct).ConfigureAwait(false);
            if (document.Connection.IdentityRef != CleanCoreGitHubAccess.Identity(origin, configuration, document.PrincipalId)
                || document.Connection.Destination != GitHubProvider.Destination(origin.Target.OwnerId, origin.Target.RepositoryId))
                return Result<CoreWorkspaceHandle>.Fail(ErrorCode.Conflict, "The selected immutable GitHub target generation or credential/actor differs from the initialized Core store.");
            var credentials = selectedCredential ?? Credential(scope!);
            var access = new CleanCoreGitHubAccess(configuration, paths, scope!, targets, origin, credentials,
                document.Connection, document.PrincipalId, store, nativeBindings.RegistryPath);
            var benches = UserBenchStore.ForRegistry(nativeBindings.RegistryPath);
            var registered = await NativeCoreExecution.RegisterAsync(nativeBindings.RegistryPath,
                new(document.DatabaseInstance, document.DatabasePath, paths.RepoRoot, document.Connection), ct).ConfigureAwait(false);
            if (!registered.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(registered.Error);
            provider = new(document.Connection, access, store, transport?.CreateHandler());
            var workspace = new CoreWorkspace(document.Connection, provider, store,
                new ConfiguredPolicyProjectionSource(paths.RepoRoot), document.PrincipalId, paths.RepoRoot, document.DatabaseInstance,
                scoped ? null : benches, executionRegistryPath: nativeBindings.RegistryPath);
            var sourceStamp = NativeSourceReader.Stamp(document, paths.RepoRoot, origin.BindingId.ToString(),
                NativeSourceReader.Number(origin.AttachmentRevision), origin.WorktreeFingerprint,
                NativeSourceReader.Number(origin.BindingRevision), document.Connection.IdentityRef);
            var lifetime = new Lifetime(provider, attachment, registry);
            provider = null;
            attachment = null;
            registry = null;
            return Result<CoreWorkspaceHandle>.Ok(new(workspace, lifetime, document, sourceStamp));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return Result<CoreWorkspaceHandle>.Fail(ErrorCode.Unavailable, "The native GitHub Core runtime could not be admitted. " + ex.Message);
        }
        finally
        {
            provider?.Dispose();
            attachment?.Dispose();
            registry?.Dispose();
        }
    }

    private IGitHubTokenSource Credential(GitHubWorkspaceScope scope)
        => SelectCredential(configuration, scope, processes);

    internal static IGitHubTokenSource SelectCredential(TwigConfiguration configuration,
        GitHubWorkspaceScope scope, IBoundedProcessRunner? processes = null) => configuration.GitHubCredentialSource switch
    {
        null or "gh" => new GhCliTokenSource(processes ?? new BoundedProcessRunner(), scope.Host, configuration.GitHubAccount),
        "github-app" when scope.Host == "github.com" => GitHubAppAuthentication.CreateDefault(
            configuration.GitHubAppClientId ?? "", configuration.GitHubOAuthIdentity ?? ""),
        _ => throw new InvalidOperationException("The selected GitHub credential source is unsupported; no alternate source was used."),
    };

    private sealed class CapturedCredential(string token) : IGitHubTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(token);
        }
    }

    private sealed class Lifetime(GitHubProvider provider, WorktreeLocalProviderTargetAttachmentStore attachment,
        SqliteSystemWorktreeRegistry registry) : IDisposable
    {
        public void Dispose()
        {
            provider.Dispose();
            attachment.Dispose();
            registry.Dispose();
        }
    }
}
