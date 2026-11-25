using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Policy;
using Twig.Providers.Native;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Services;

internal sealed class CoreNativeWorkspaceHost(TwigPaths paths, IConnectionBindingService bindings, string? database)
{
    private string AuthorityPath => Path.Combine(Path.GetDirectoryName(bindings.RegistryPath)!,
        "native-databases", "database-" + database + ".db");

    private Result<bool> ValidateLocation()
    {
        if (!NativeDatabaseName.IsValid(database))
            return Result<bool>.Fail(ErrorCode.InvalidInput,
                "Choose a native database with --database <name> or nativeDatabase in twig.json: 1-64 lowercase letters, digits or internal hyphens.");
        try
        {
            for (var path = AuthorityPath; path is not null; path = Path.GetDirectoryName(path))
                if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return Result<bool>.Fail(ErrorCode.PolicyViolation, "Native database storage cannot traverse a linked file or directory.");
            return Result<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<bool>.Fail(ErrorCode.StorageUnavailable, "The named native database location is unavailable; no alternate source was used.");
        }
    }

    internal async Task<Result<NativeCreationReceipt>> InitializeAsync(string databasePath, CancellationToken ct,
        NativeSetupEffects? effects, Connection? expectedAuthority = null)
    {
        var location = ValidateLocation();
        if (!location.IsSuccess) return Result<NativeCreationReceipt>.Fail(location.Error);
        effects?.Before(NativeSetupEffectKind.NativeAuthority);
        if (!File.Exists(AuthorityPath) && (File.Exists(AuthorityPath + "-wal") || File.Exists(AuthorityPath + "-shm")))
            return Result<NativeCreationReceipt>.Fail(ErrorCode.Conflict,
                "The named native database has partial SQLite evidence; no replacement authority was created.");
        Directory.CreateDirectory(Path.GetDirectoryName(AuthorityPath)!);
        var source = expectedAuthority is not null || File.Exists(AuthorityPath)
            ? await NativeAuthority.OpenAsync(AuthorityPath, ct).ConfigureAwait(false)
            : await NativeAuthority.CreateAsync(AuthorityPath, ct).ConfigureAwait(false);
        if (!source.IsSuccess) return Result<NativeCreationReceipt>.Fail(source.Error);
        if (expectedAuthority is not null && source.Value.Connection != expectedAuthority)
            return Result<NativeCreationReceipt>.Fail(ErrorCode.Conflict, "The existing native database is not the requested source and owner.");
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
            : Result<NativeCreationReceipt>.Ok(new(recorded, Stamp(recorded), database));
    }

    internal async Task<Result<CoreWorkspaceHandle>> OpenAsync(CoreWorkspaceDocument document, SqliteStore store,
        CancellationToken ct, bool scoped = false, Func<IStore,IStore>? mirror = null)
    {
        var location = ValidateLocation();
        if (!location.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(location.Error);
        var source = await NativeAuthority.OpenAsync(AuthorityPath, ct).ConfigureAwait(false);
        if (!source.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(source.Error);
        if (document.Connection != source.Value.Connection || document.PrincipalId != source.Value.Connection.IdentityRef)
            return Result<CoreWorkspaceHandle>.Fail(ErrorCode.Conflict, "The named native database or owner changed; this workspace was not rebound.");
        var registered = await NativeCoreExecution.RegisterAsync(bindings.RegistryPath,
            new(document.DatabaseInstance, document.DatabasePath, paths.RepoRoot, document.Connection), ct).ConfigureAwait(false);
        if (!registered.IsSuccess) return Result<CoreWorkspaceHandle>.Fail(registered.Error);
        var provider = new NativeProvider(source.Value, mirror?.Invoke(store) ?? store);
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
