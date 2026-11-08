using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Auth;

/// <summary>Freezes checkout authority; an old provider refuses changes until explicit reconnect.</summary>
internal sealed class MigrationBoundAuthenticationProvider : IAuthenticationProvider, IBoundAuthenticationMetadata, IConnectionOperationGuard, IConnectionRemoteWriteGuard, IDisposable
{
    private readonly IAuthenticationProvider _inner;
    private readonly ResolvedConnectionBinding _binding;
    private readonly string _registryPath;
    private readonly IConnectionBindingService _bindings;
    private readonly AsyncLocal<OperationScope?> _operationScope = new();
    private bool _disposed;

    internal MigrationBoundAuthenticationProvider(IAuthenticationProvider inner, ResolvedConnectionBinding binding,
        string registryPath, IConnectionBindingService bindings)
    {
        _inner = inner;
        _binding = binding;
        _registryPath = registryPath;
        _bindings = bindings;
        ValidateStorage();
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
        var prior = CurrentScope();
        if (prior is not null && prior.TryRetain())
            return Task.FromResult<IDisposable>(new OperationLease(this, prior, null, owner: false));
        var scope = new OperationScope(ConnectionOperationGate.Acquire(_binding.WorktreeRoot), prior);
        _operationScope.Value = scope;
        return ValidateOperationAsync(scope, prior, ct);
    }

    public Task<IDisposable?> TryAcquireFreshOperationAsync(IConnectionBindingService resolver, TwigPaths paths,
        Action<ResolvedConnectionBinding> validateCurrent, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(validateCurrent);
        if (!ReferenceEquals(resolver, _bindings)) throw Changed();
        var expected = TwigPaths.BuildPaths(Path.Combine(_binding.WorktreeRoot, ".twig"), new TwigConfiguration(), _binding.WorktreeRoot);
        if (!SameContext(paths, expected)) return Task.FromResult<IDisposable?>(null);
        var prior = CurrentScope();
        // Retain only the physical lease; the child must obtain its own fresh authority.
        var physical = prior is not null && prior.TryRetain()
            ? new OperationLease(this, prior, null, owner: false)
            : ConnectionOperationGate.Acquire(_binding.WorktreeRoot);
        var scope = new OperationScope(physical, prior);
        _operationScope.Value = scope;
        return OptionalLease(ValidateOperationAsync(scope, prior, ct, validateCurrent, expected));
    }

    private static async Task<IDisposable?> OptionalLease(Task<IDisposable> lease)
        => await lease.ConfigureAwait(false);

    private static bool SameContext(TwigPaths current, TwigPaths expected)
        => SamePath(current.StartDir, expected.StartDir) && SamePath(current.RepoRoot, expected.RepoRoot)
            && SamePath(current.TwigDir, expected.TwigDir) && SamePath(current.DbPath, expected.DbPath)
            && SamePath(current.ConfigPath, expected.ConfigPath) && SamePath(current.RepoConfigPath, expected.RepoConfigPath)
            && SamePath(current.GlobalDisplayPath, expected.GlobalDisplayPath);

    private static bool SamePath(string left, string right)
        => NativePathComparer.Instance.Equals(Path.GetFullPath(left), Path.GetFullPath(right));

    private OperationScope? CurrentScope()
    {
        var scope = _operationScope.Value;
        while (scope is not null && !scope.IsActive) scope = scope.Prior;
        _operationScope.Value = scope;
        return scope;
    }

    private async Task<IDisposable> ValidateOperationAsync(OperationScope scope, OperationScope? prior, CancellationToken ct,
        Action<ResolvedConnectionBinding>? validateCurrent = null, TwigPaths? validationPaths = null)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var paths = validationPaths ?? TwigPaths.BuildPaths(Path.Combine(_binding.WorktreeRoot, ".twig"), new TwigConfiguration(), _binding.WorktreeRoot);
            var configuration = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
            var current = await _bindings.ResolveAsync(configuration, paths, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            ValidateCurrent(current);
            validateCurrent?.Invoke(current);
            scope.MarkValidated(current, paths);
            return new OperationLease(this, scope, prior, owner: true);
        }
        catch
        {
            scope.Release(owner: true);
            _operationScope.Value = prior;
            throw;
        }
    }

    private void ValidateCurrent(ResolvedConnectionBinding current)
    {
        if (current.Binding != _binding.Binding
            || (current.SelectionSource == "connection-default-binding" && _binding.SelectionSource == current.SelectionSource
                && current.SelectionRevision != _binding.SelectionRevision) || current.StorageGeneration != _binding.StorageGeneration
            || current.Operation.Organization != _binding.Operation.Organization || current.Operation.Project != _binding.Operation.Project
            || current.Operation.Team != _binding.Operation.Team || current.Operation.WorktreeFingerprint != _binding.Operation.WorktreeFingerprint
            || !SameAuthority(current.Identity, _binding.Identity))
            throw Changed();
    }

    private sealed class OperationScope(IDisposable physicalLease, OperationScope? prior)
    {
        private bool _active = true;
        private ResolvedConnectionBinding? _origin;
        private TwigPaths? _paths;
        private int _references = 1;
        internal OperationScope? Prior { get; } = prior;
        internal bool IsActive { get { lock (this) return _active; } }

        internal void MarkValidated(ResolvedConnectionBinding origin, TwigPaths paths)
        {
            lock (this) { _paths = paths; _origin = origin; }
        }

        internal ResolvedConnectionBinding? GetOrigin(TwigConfiguration configuration, TwigPaths paths)
        {
            lock (this)
            {
                if (!_active || _origin is null || _paths is null) return null;
                var comparer = NativePathComparer.Instance;
                if (!comparer.Equals(Path.GetFullPath(paths.StartDir), Path.GetFullPath(_paths.StartDir))
                    || !comparer.Equals(Path.GetFullPath(paths.TwigDir), Path.GetFullPath(_paths.TwigDir))
                    || !comparer.Equals(Path.GetFullPath(paths.DbPath), Path.GetFullPath(_paths.DbPath))
                    || !comparer.Equals(Path.GetFullPath(paths.ConfigPath), Path.GetFullPath(_paths.ConfigPath))
                    || !comparer.Equals(Path.GetFullPath(paths.GlobalDisplayPath), Path.GetFullPath(_paths.GlobalDisplayPath))) return null;
                if (_origin.Operation.Organization != configuration.Organization
                    || _origin.Operation.Project != configuration.Project
                    || _origin.Operation.Team != configuration.Team
                    || _origin.Operation.DisplayIcons != configuration.Display.Icons
                    || !comparer.Equals(_origin.Operation.PortableConfigurationSource,
                        configuration.IsLegacyMode ? paths.ConfigPath : paths.RepoConfigPath)
                    || (configuration.Display.IconsOverride is not null) != comparer.Equals(
                        _origin.Operation.DisplayIconsSource, paths.ConfigPath)) return null;
                return _origin;
            }
        }

        internal ResolvedConnectionBinding? GetAuthority()
        {
            lock (this) return _active ? _origin : null;
        }

        internal bool TryRetain()
        {
            lock (this)
            {
                if (!_active || _origin is null) return false;
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

    private sealed class OperationLease(MigrationBoundAuthenticationProvider provider, OperationScope scope, OperationScope? prior, bool owner) : IAdmittedConnectionOperation
    {
        private int _disposed;

        public ResolvedConnectionBinding? GetOrigin(IConnectionBindingService resolver, TwigConfiguration configuration, TwigPaths paths)
        {
            if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(resolver, provider._bindings)) return null;
            return scope.GetOrigin(configuration, paths);
        }

        public ResolvedConnectionBinding? GetAuthority(IConnectionBindingService resolver)
            => Volatile.Read(ref _disposed) == 0 && ReferenceEquals(resolver, provider._bindings)
                ? scope.GetAuthority() : null;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            scope.Release(owner);
            if (owner && ReferenceEquals(provider._operationScope.Value, scope))
                provider._operationScope.Value = prior;
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
        var prior = CurrentScope();
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
        if (_inner is IDisposable disposable) disposable.Dispose();
    }
}
