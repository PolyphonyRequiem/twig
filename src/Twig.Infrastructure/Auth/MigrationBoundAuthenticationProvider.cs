using Twig.Domain.Interfaces;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;

namespace Twig.Infrastructure.Auth;

/// <summary>Freezes checkout authority; an old provider refuses changes until explicit reconnect.</summary>
internal sealed class MigrationBoundAuthenticationProvider : IAuthenticationProvider, IBoundAuthenticationMetadata, IConnectionOperationGuard, IConnectionRemoteWriteGuard, IDisposable
{
    private readonly IAuthenticationProvider _inner;
    private readonly ResolvedConnectionBinding _binding;
    private readonly string _registryPath;
    private readonly IConnectionBindingService _bindings;
    private readonly LegacyHostCapability? _legacyCapability;
    private readonly AsyncLocal<OperationScope?> _operationScope = new();
    private bool _disposed;

    internal MigrationBoundAuthenticationProvider(IAuthenticationProvider inner, ResolvedConnectionBinding binding,
        string registryPath, IConnectionBindingService bindings)
    {
        _inner = inner;
        _binding = binding;
        _registryPath = registryPath;
        _bindings = bindings;
        if (binding.StorageGeneration is null)
            _legacyCapability = new LegacyHostCapability(Path.Combine(binding.WorktreeRoot, ".twig", "cache"));
        try { ValidateStorage(); }
        catch { _legacyCapability?.Dispose(); throw; }
    }

    private void ValidateStorage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MirrorAdmission.ValidateNoUnfinishedTransition(_registryPath, _binding.Operation.WorktreeFingerprint);
        var current = MirrorAdmission.ReadNativeState(_registryPath, _binding.Operation.WorktreeFingerprint);
        if (current is null && _binding.StorageGeneration is null) return;
        if (current is null || current.State != "active" || current.Generation != _binding.StorageGeneration
            || current.BindingId != _binding.Binding.BindingId)
            throw Changed();
        var paths = TwigPaths.BuildPaths(Path.Combine(_binding.WorktreeRoot, ".twig"), new TwigConfiguration(), _binding.WorktreeRoot);
        _ = MirrorAdmission.Acquire(paths, _registryPath)
            ?? throw new InvalidOperationException("migration-incomplete: native storage capability is missing.");
    }

    public Task<IDisposable> AcquireOperationAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var prior = _operationScope.Value;
        if (prior is not null && prior.TryRetain())
            return Task.FromResult<IDisposable>(new OperationLease(this, prior, null, owner: false));
        var scope = new OperationScope(ConnectionOperationGate.Acquire(_binding.WorktreeRoot));
        _operationScope.Value = scope;
        return ValidateOperationAsync(scope, prior, ct);
    }

    private async Task<IDisposable> ValidateOperationAsync(OperationScope scope, OperationScope? prior, CancellationToken ct)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var paths = TwigPaths.BuildPaths(Path.Combine(_binding.WorktreeRoot, ".twig"), new TwigConfiguration(), _binding.WorktreeRoot);
            var configuration = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
            var current = await _bindings.ResolveAsync(configuration, paths, ct).ConfigureAwait(false);
            if (current.Binding != _binding.Binding
                || (current.SelectionSource == "connection-default-binding" && _binding.SelectionSource == current.SelectionSource
                    && current.SelectionRevision != _binding.SelectionRevision) || current.StorageGeneration != _binding.StorageGeneration
                || current.Operation.Organization != _binding.Operation.Organization || current.Operation.Project != _binding.Operation.Project
                || current.Operation.Team != _binding.Operation.Team || current.Operation.WorktreeFingerprint != _binding.Operation.WorktreeFingerprint
                || !SameAuthority(current.Identity, _binding.Identity))
                throw Changed();
            scope.MarkValidated();
            return new OperationLease(this, scope, prior, owner: true);
        }
        catch
        {
            scope.Release(owner: true);
            _operationScope.Value = prior;
            throw;
        }
    }

    private sealed class OperationScope(IDisposable physicalLease)
    {
        private bool _active = true;
        private bool _validated;
        private int _references = 1;

        internal void MarkValidated()
        {
            lock (this) _validated = true;
        }

        internal bool TryRetain()
        {
            lock (this)
            {
                if (!_active || !_validated) return false;
                _references++;
                return true;
            }
        }

        internal void Release(bool owner)
        {
            bool dispose;
            lock (this)
            {
                if (owner) _active = false;
                dispose = --_references == 0;
            }
            if (dispose) physicalLease.Dispose();
        }
    }

    private sealed class OperationLease(MigrationBoundAuthenticationProvider provider, OperationScope scope, OperationScope? prior, bool owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            scope.Release(owner);
            if (owner) provider._operationScope.Value = prior;
        }
    }

    private static bool SameAuthority(AuthenticationIdentity current, AuthenticationIdentity frozen)
        => current.IdentityId == frozen.IdentityId && current.Method == frozen.Method
            && current.CredentialRef == frozen.CredentialRef && current.TenantId == frozen.TenantId
            && current.ObjectId == frozen.ObjectId && current.Issuer == frozen.Issuer
            && current.AuthorityHost == frozen.AuthorityHost && current.AdoAuthority == frozen.AdoAuthority
            && current.AdoPrincipalId == frozen.AdoPrincipalId;

    private static InvalidOperationException Changed()
        => new("binding-changed: checkout selection, endpoint, principal or admitted cache generation changed. Explicitly close and reconnect this CLI/TUI/MCP/Herdr host; this provider will not adopt another identity.");

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        using var lease = await AcquireOperationAsync(ct).ConfigureAwait(false);
        var token = await _inner.GetAccessTokenAsync(ct).ConfigureAwait(false);
        return token;
    }

    public async Task<AuthenticationIdentity> GetBoundIdentityAsync(CancellationToken ct = default)
    {
        using var lease = await AcquireOperationAsync(ct).ConfigureAwait(false);
        return await ((IBoundAuthenticationMetadata)_inner).GetBoundIdentityAsync(ct).ConfigureAwait(false);
    }

    public async Task<IConnectionRemoteWriteAdmission> BeginRemoteWriteAsync(ConnectionRemoteWriteRequest request, CancellationToken ct = default)
    {
        using var lease = await AcquireOperationAsync(ct).ConfigureAwait(false);
        return await ConnectionRemoteWriteAdmission.BeginAsync(_registryPath, _binding, request, ct).ConfigureAwait(false);
    }

    public void InvalidateToken()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var prior = _operationScope.Value;
        if (prior is not null && prior.TryRetain())
        {
            using var nested = new OperationLease(this, prior, null, owner: false);
            _inner.InvalidateToken();
            return;
        }
        using var lease = ConnectionOperationGate.Acquire(_binding.WorktreeRoot);
        ValidateStorage();
        _inner.InvalidateToken();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _legacyCapability?.Dispose();
        if (_inner is IDisposable disposable) disposable.Dispose();
    }
}
