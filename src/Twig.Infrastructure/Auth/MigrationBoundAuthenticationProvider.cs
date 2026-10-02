using Twig.Domain.Interfaces;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;

namespace Twig.Infrastructure.Auth;

/// <summary>Freezes storage authority; a supported pre-cutover provider must close, never adopt a new generation.</summary>
internal sealed class MigrationBoundAuthenticationProvider : IAuthenticationProvider, IBoundAuthenticationMetadata, IDisposable
{
    private readonly IAuthenticationProvider _inner;
    private readonly ResolvedConnectionBinding _binding;
    private readonly string _registryPath;
    private readonly LegacyHostCapability? _legacyCapability;
    private bool _disposed;

    internal MigrationBoundAuthenticationProvider(IAuthenticationProvider inner, ResolvedConnectionBinding binding, string registryPath)
    {
        _inner = inner;
        _binding = binding;
        _registryPath = registryPath;
        if (binding.StorageGeneration is null)
            _legacyCapability = new LegacyHostCapability(Path.Combine(binding.WorktreeRoot, ".twig", "cache"));
        try { Validate(); }
        catch { _legacyCapability?.Dispose(); throw; }
    }

    private void Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var current = MirrorAdmission.ReadNativeState(_registryPath, _binding.Operation.WorktreeFingerprint);
        if (current is null && _binding.StorageGeneration is null) return;
        if (current is null || current.State != "active" || current.Generation != _binding.StorageGeneration
            || current.BindingId != _binding.Binding.BindingId)
            throw new InvalidOperationException("binding-changed: migration storage authority changed or activation is incomplete. Explicitly close and reconnect this CLI/TUI/MCP/Herdr host; no legacy work HTTP is admitted.");
        var paths = TwigPaths.BuildPaths(Path.Combine(_binding.WorktreeRoot, ".twig"), new TwigConfiguration(), _binding.WorktreeRoot);
        var admission = MirrorAdmission.Acquire(paths, _registryPath)
            ?? throw new InvalidOperationException("migration-incomplete: native storage capability is missing.");
        admission.Validate();
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        Validate();
        var token = await _inner.GetAccessTokenAsync(ct).ConfigureAwait(false);
        Validate();
        return token;
    }

    public async Task<AuthenticationIdentity> GetBoundIdentityAsync(CancellationToken ct = default)
    {
        Validate();
        return await ((IBoundAuthenticationMetadata)_inner).GetBoundIdentityAsync(ct).ConfigureAwait(false);
    }

    public void InvalidateToken() { Validate(); _inner.InvalidateToken(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _legacyCapability?.Dispose();
        if (_inner is IDisposable disposable) disposable.Dispose();
    }
}
