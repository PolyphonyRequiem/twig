using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Default composition of AB#736 §6.3 (local layout), §9.5 (system-store
/// registration), and §4.1 policy materialization into a single seam. Every
/// underlying primitive is idempotent, so a partial failure is safe to
/// re-run; the composed run stops at the first §8 failure so the operator
/// sees the specific repair hint rather than a compounded error.
/// </summary>
internal sealed class ManagedWorktreeInitializer : IManagedWorktreeInitializer
{
    private readonly IPrimaryScopeAttachmentStore _store;
    private readonly ISystemWorktreeRegistry _registry;
    private readonly IWorktreeFingerprintProvider _fingerprint;
    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;

    public ManagedWorktreeInitializer(
        IPrimaryScopeAttachmentStore store,
        ISystemWorktreeRegistry registry,
        IWorktreeFingerprintProvider fingerprint,
        TwigConfiguration config,
        TwigPaths paths)
    {
        _store = store;
        _registry = registry;
        _fingerprint = fingerprint;
        _config = config;
        _paths = paths;
    }

    public async Task<Result> InitializeAsync(
        string organization,
        string project,
        string? team,
        string profileIdentity,
        string profileVersion,
        CancellationToken ct = default)
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
        if (!upsertWt.IsSuccess)
            return upsertWt;

        // Materialize the AB#736 §4.1 policy block. Writes are idempotent —
        // an existing binding is preserved, and only fields the current run
        // knows about are backfilled. This is the "no permanently
        // unavailable default" contract: an untouched managed worktree
        // never fails eligibility for a missing block.
        try
        {
            _config.Policy ??= new PolicyConfig();
            _config.Policy.SelectedProfile ??= new SelectedProfileBinding();
            if (string.IsNullOrWhiteSpace(_config.Policy.SelectedProfile.Identity))
                _config.Policy.SelectedProfile.Identity = string.IsNullOrWhiteSpace(profileIdentity) ? "twig/default" : profileIdentity;
            if (string.IsNullOrWhiteSpace(_config.Policy.SelectedProfile.Version))
                _config.Policy.SelectedProfile.Version = string.IsNullOrWhiteSpace(profileVersion) ? "1" : profileVersion;
            _config.Policy.PrimaryScopeTypes ??= new List<string>();
            await _config.SaveSplitAsync(_paths, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}");
        }
        return Result.Ok();
    }
}
