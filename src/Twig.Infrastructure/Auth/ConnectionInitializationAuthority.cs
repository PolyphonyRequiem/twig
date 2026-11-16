namespace Twig.Infrastructure.Auth;

/// <summary>Selected metadata credentials under a fresh checkout lease; cannot admit work writes.</summary>
internal sealed class ConnectionInitializationAuthority(
    ResolvedConnectionBinding origin,
    IAuthenticationProvider authentication,
    Func<CancellationToken, Task<ResolvedConnectionBinding>> resolve) : IDisposable
{
    private bool _disposed;
    internal ResolvedConnectionBinding Origin { get; } = origin;

    internal async Task RevalidateAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (await resolve(ct).ConfigureAwait(false) != Origin)
            throw new InvalidOperationException("binding-changed: fresh initialization endpoint, attachment, selection or actor changed.");
    }

    internal async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await RevalidateAsync(ct).ConfigureAwait(false);
        var token = await authentication.GetAccessTokenAsync(ct).ConfigureAwait(false);
        await RevalidateAsync(ct).ConfigureAwait(false);
        return token;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (authentication is IDisposable disposable) disposable.Dispose();
    }
}
