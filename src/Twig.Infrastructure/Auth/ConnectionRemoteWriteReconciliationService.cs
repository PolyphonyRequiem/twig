using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;

namespace Twig.Infrastructure.Auth;

internal sealed record ConnectionRemoteWriteInspection(string State, string IntentId, string Digest, bool CanReconcile,
    string WorktreeRoot, string BindingId, string IdentityId, ConnectionRemoteWriteRequest Request,
    IReadOnlyList<ConnectionRemoteWriteObservation> Observations, ConnectionRemoteWriteReceipt? Receipt,
    IReadOnlyList<string> Blockers, IReadOnlyList<string> NextSteps);

internal interface IConnectionRemoteWriteReconciliationService
{
    Task<IReadOnlyList<ConnectionRemoteWriteInspection>> InspectAsync(
        TwigConfiguration configuration, TwigPaths paths, CancellationToken ct = default);
}

/// <summary>Reads original native transport evidence without settling or replaying an attempt.</summary>
internal sealed class ConnectionRemoteWriteReconciliationService : IConnectionRemoteWriteReconciliationService, IDisposable
{
    private readonly IConnectionBindingService _bindings;
    private readonly SqliteSystemWorktreeRegistry _registry;

    internal ConnectionRemoteWriteReconciliationService(string userHome, IConnectionBindingService bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
        if (!Path.IsPathFullyQualified(userHome)) throw new ArgumentException("userHome must be absolute.", nameof(userHome));
        var registryPath = Path.Combine(Path.GetFullPath(userHome), "system.db");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!comparer.Equals(registryPath, Path.GetFullPath(bindings.RegistryPath)))
            throw new ArgumentException("Native inspection must use the binding service's registry.", nameof(bindings));
        _bindings = bindings;
        _registry = new SqliteSystemWorktreeRegistry(registryPath, TimeProvider.System);
    }

    public async Task<IReadOnlyList<ConnectionRemoteWriteInspection>> InspectAsync(
        TwigConfiguration configuration, TwigPaths paths, CancellationToken ct = default)
    {
        var current = await _bindings.ResolveAsync(configuration, paths, ct).ConfigureAwait(false);
        var histories = await _registry.ReadConnectionRemoteWritesAsync(
            current.Operation.WorktreeFingerprint, ct: ct).ConfigureAwait(false);
        if (!histories.IsSuccess) throw new InvalidOperationException(histories.Error);
        return histories.Value.Select(history => new ConnectionRemoteWriteInspection(
            history.Receipt is null ? "unknown" : "settled", history.Intent.IntentId,
            history.Intent.RequestDigest, false, history.Intent.Origin.WorktreeRoot,
            history.Intent.Origin.Binding.BindingId, history.Intent.Origin.Identity.IdentityId,
            history.Intent.Request, history.Observations, history.Receipt,
            ConnectionRemoteWriteAdmission.SameAuthority(current, history.Intent.Origin) ? []
                : ["remote-write-origin-changed: original binding/principal differs from the recorded selection."],
            ["Correlated native transport evidence only. Core proposal status is execution authority; inspection does not settle, replay or clear fences."]))
            .ToArray();
    }

    public void Dispose() => _registry.Dispose();
}
