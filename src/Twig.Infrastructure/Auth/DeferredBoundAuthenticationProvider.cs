using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Lazy wrapper around an <see cref="IAuthenticationProvider"/> whose construction
/// requires central binding admission. Resolution is deferred until the first
/// <see cref="GetAccessTokenAsync"/> call so that command surfaces that complete
/// before any HTTP (help, local manifest validation, offline init precondition
/// checks) never force an account/binding admission against a tenant they do not
/// yet need to speak to.
/// <para>
/// Once the inner provider has been admitted it is frozen for the lifetime of the
/// wrapper — a second resolution with a different identity cannot silently rotate
/// in behind an in-flight work HTTP call. Concurrent first callers share one
/// resolution <see cref="Task"/>; a faulted admission is cleared under the lock
/// so the next <see cref="GetAccessTokenAsync"/> re-runs the same guarded factory
/// (the only way the chosen identity can change is a user-visible admission
/// failure, never a silent swap).
/// </para>
/// </summary>
internal sealed class DeferredBoundAuthenticationProvider : IAuthenticationProvider, IBoundAuthenticationMetadata
{
    private readonly Func<CancellationToken, Task<IAuthenticationProvider>> _factory;
    private readonly object _sync = new();
    private Task<IAuthenticationProvider>? _resolution;

    public DeferredBoundAuthenticationProvider(Func<CancellationToken, Task<IAuthenticationProvider>> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // The admission factory is awaited through a per-caller WaitAsync so a
        // single caller's cancellation does not poison the shared resolution Task
        // for sibling waiters. No sync-over-async anywhere on this hot path.
        var provider = await GetProviderAsync(ct).ConfigureAwait(false);
        return await provider.GetAccessTokenAsync(ct).ConfigureAwait(false);
    }

    public async Task<AuthenticationIdentity> GetBoundIdentityAsync(CancellationToken ct = default)
    {
        var provider = await GetProviderAsync(ct).ConfigureAwait(false);
        if (provider is not IBoundAuthenticationMetadata metadata)
            throw new InvalidOperationException("Admitted authentication provider has no frozen principal evidence; repair the selected binding.");
        return await metadata.GetBoundIdentityAsync(ct).ConfigureAwait(false);
    }

    public void InvalidateToken()
    {
        // Cleanup must either name the selected binding or report admission
        // failure; returning success here would conceal a failed auth clear.
        var provider = GetProviderAsync(CancellationToken.None).GetAwaiter().GetResult();
        provider.InvalidateToken();
    }

    private Task<IAuthenticationProvider> GetProviderAsync(CancellationToken ct)
    {
        Task<IAuthenticationProvider> task;
        lock (_sync)
        {
            if (_resolution is null || _resolution.IsFaulted || _resolution.IsCanceled)
                _resolution = StartResolutionAsync();
            task = _resolution;
        }
        return task.WaitAsync(ct);
    }

    private Task<IAuthenticationProvider> StartResolutionAsync()
    {
        // Factory is invoked with CancellationToken.None so a cancellation from
        // one caller does not cancel the admission other concurrent callers are
        // awaiting; per-caller cancellation is honored by WaitAsync above.
        // Any synchronous throw from the factory is projected into a faulted
        // Task so the lock-held region never observes a mid-construction state.
        try
        {
            return _factory(CancellationToken.None);
        }
        catch (Exception ex)
        {
            return Task.FromException<IAuthenticationProvider>(ex);
        }
    }
}
