using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Auth;

/// <summary>Owns the actual locks for one new checkout birth, never a runtime admission.</summary>
internal sealed class FreshWorkspaceInitialization : IDisposable
{
    private readonly TwigPaths _paths;
    private readonly FileStream _initialization;
    private readonly IDisposable _operation;
    private Guid? _createdStore;
    private bool _disposed;

    private FreshWorkspaceInitialization(TwigPaths paths, FileStream initialization, IDisposable operation)
        => (_paths, _initialization, _operation) = (paths, initialization, operation);

    internal static FreshWorkspaceInitialization Begin(TwigPaths paths, NativeSetupEffects effects)
    {
        if (Directory.Exists(paths.TwigDir) || File.Exists(paths.TwigDir))
            throw new InvalidOperationException("Fresh initialization refuses existing or partial checkout state.");
        effects.Before(NativeSetupEffectKind.WorkspaceDirectory);
        Directory.CreateDirectory(paths.TwigDir);
        effects.Confirm(NativeSetupEffectKind.WorkspaceDirectory);
        effects.Before(NativeSetupEffectKind.InitializationLock);
        var initialization = new FileStream(Path.Combine(paths.TwigDir, "core-init.lock"), FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        try
        {
            effects.Confirm(NativeSetupEffectKind.InitializationLock);
            var operation = ConnectionOperationGate.Acquire(paths.RepoRoot, exclusive: true);
            return new(paths, initialization, operation);
        }
        catch
        {
            initialization.Dispose();
            throw;
        }
    }

    internal void RecordCreatedStore(SqliteStore store)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_createdStore is not null || store.InstanceId == Guid.Empty || store.DatabasePath != _paths.DbPath)
            throw new InvalidOperationException("Fresh initialization can retain only its own newly created store.");
        _createdStore = store.InstanceId;
    }

    internal async Task ValidateAsync(TwigPaths paths, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(paths, _paths) || !_initialization.CanWrite
            || File.Exists(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName))
            || Directory.Exists(paths.DbPath)
            || _createdStore is null && (File.Exists(paths.DbPath)
                || File.Exists(paths.DbPath + "-wal") || File.Exists(paths.DbPath + "-shm")))
            throw new InvalidOperationException("Fresh initialization authority is no longer available; existing store evidence was preserved.");
        if (_createdStore is { } instance)
        {
            var actual = await SqliteStore.OpenAsync(paths.DbPath, ct, instance).ConfigureAwait(false);
            if (!actual.IsSuccess)
                throw new InvalidOperationException("Fresh initialization lost its created store instance: " + actual.Error.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _operation.Dispose();
        _initialization.Dispose();
    }
}
