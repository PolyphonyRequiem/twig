using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Default composition of AB#736 §6.3 (local layout) and §9.5 (system-store
/// registration) into a single seam. Both underlying primitives are
/// idempotent, so a partial failure is safe to re-run; the composed run
/// stops at the first §8 failure so the operator sees the specific repair
/// hint rather than a compounded error.
/// </summary>
internal sealed class ManagedWorktreeInitializer : IManagedWorktreeInitializer
{
    private readonly IPrimaryScopeAttachmentStore _store;
    private readonly ISystemWorktreeRegistry _registry;
    private readonly IWorktreeFingerprintProvider _fingerprint;

    public ManagedWorktreeInitializer(
        IPrimaryScopeAttachmentStore store,
        ISystemWorktreeRegistry registry,
        IWorktreeFingerprintProvider fingerprint)
    {
        _store = store;
        _registry = registry;
        _fingerprint = fingerprint;
    }

    public async Task<Result> InitializeAsync(string organization, string project, string? team, CancellationToken ct = default)
    {
        var layout = await _store.InitializeAsync(ct).ConfigureAwait(false);
        if (!layout.IsSuccess)
            return layout;

        var fingerprint = _fingerprint.CurrentFingerprint;
        if (string.IsNullOrEmpty(fingerprint.CanonicalJson))
            return Result.Fail(AttachmentStorageFailure.NotAGitWorktree);

        var upsertConn = await _registry.UpsertConnectionAsync(fingerprint.ConnectionRef, organization, project, team, ct).ConfigureAwait(false);
        if (!upsertConn.IsSuccess)
            return upsertConn;

        var upsertWt = await _registry.UpsertWorktreeAsync(fingerprint.CanonicalJson, fingerprint.ConnectionRef, fingerprint.WorktreeRoot, ct).ConfigureAwait(false);
        return upsertWt;
    }
}
