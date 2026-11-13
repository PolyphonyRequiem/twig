using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Policy;
using Twig.Providers.Native;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Services;

internal sealed class CoreNativeWorkspaceHost(TwigPaths paths, IConnectionBindingService bindings)
{
    private string AuthorityPath => Path.Combine(Path.GetDirectoryName(bindings.RegistryPath)!, "native-authority.db");

    internal async Task<Result<NativeCreationReceipt>> InitializeAsync(string databasePath, CancellationToken ct,
        NativeSetupEffects? effects)
    {
        effects?.Before(NativeSetupEffectKind.NativeAuthority);
        var source = File.Exists(AuthorityPath)
            ? await NativeAuthority.OpenAsync(AuthorityPath, ct).ConfigureAwait(false)
            : await NativeAuthority.CreateAsync(AuthorityPath, ct).ConfigureAwait(false);
        if (!source.IsSuccess) return Result<NativeCreationReceipt>.Fail(source.Error);
        effects?.Confirm(NativeSetupEffectKind.NativeAuthority);
        effects?.Before(NativeSetupEffectKind.CoreStore);
        var created = await SqliteStore.CreateAsync(databasePath, ct).ConfigureAwait(false);
        if (!created.IsSuccess) return Result<NativeCreationReceipt>.Fail(created.Error);
        effects?.Confirm(NativeSetupEffectKind.CoreStore);
        CoreWorkspaceDocument? recorded = null;
        var recording = await CoreWorkspaceFactory.RecordNewAsync(paths, created.Value, databasePath,
            source.Value.Connection, source.Value.Connection.IdentityRef, ct, bindings.RegistryPath,
            effects, document => recorded = document).ConfigureAwait(false);
        if (!recording.IsSuccess) return Result<NativeCreationReceipt>.Fail(recording.Error);
        return recorded is null
            ? Result<NativeCreationReceipt>.Fail(ErrorCode.Unavailable, "Native workspace recording did not retain its exact evidence.")
            : Result<NativeCreationReceipt>.Ok(new(recorded, Stamp(recorded)));
    }

    internal async Task<Result<CoreWorkspaceHandle>> OpenAsync(CoreWorkspaceDocument document, SqliteStore store,
        CancellationToken ct, bool scoped = false)
    {
        var source = await NativeAuthority.OpenAsync(AuthorityPath, ct).ConfigureAwait(false);
        if (!source.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(source.Error);
        if (document.Connection != source.Value.Connection || document.PrincipalId != source.Value.Connection.IdentityRef)
            return Result<CoreWorkspaceHandle>.Fail(ErrorCode.Conflict, "The native authority or owner changed; this workspace was not rebound.");
        var registered = await NativeCoreExecution.RegisterAsync(bindings.RegistryPath,
            new(document.DatabaseInstance, document.DatabasePath, paths.RepoRoot, document.Connection), ct).ConfigureAwait(false);
        if (!registered.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(registered.Error);
        var provider = new NativeProvider(source.Value, store);
        var workspace = new CoreWorkspace(document.Connection, provider, store,
            new ConfiguredPolicyProjectionSource(paths.RepoRoot), document.PrincipalId, paths.RepoRoot,
            document.DatabaseInstance, scoped ? null : UserBenchStore.ForRegistry(bindings.RegistryPath),
            executionRegistryPath: bindings.RegistryPath);
        return Result<CoreWorkspaceHandle>.Ok(new(workspace, provider, document, Stamp(document)));
    }

    private NativeSourceStamp Stamp(CoreWorkspaceDocument document)
        => NativeSourceReader.Stamp(document, paths.RepoRoot, AuthorityPath, document.Connection.ResourceScope,
            document.Connection.Destination, document.PrincipalId);
}
