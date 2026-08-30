using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Default composition of AB#736 §6.3 (local layout), §9.5 (system-store
/// registration), and §4.1 policy materialization. Every underlying
/// primitive is idempotent; the composed run stops at the first §8 failure.
/// <para>
/// The caller supplies the materialized profile — no synthetic
/// identity/version is invented here. When the checked-in
/// <see cref="TwigConfiguration.Policy"/> is already fully populated the
/// initializer preserves it byte-for-byte; only genuinely missing fields
/// are filled from the supplied materialization.
/// </para>
/// </summary>
internal sealed class ManagedWorktreeInitializer : IManagedWorktreeInitializer
{
    private readonly IPrimaryScopeAttachmentStore _store;
    private readonly ISystemWorktreeRegistry _registry;
    private readonly IWorktreeFingerprintProvider _fingerprint;
    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;
    private readonly IProfileRegistrySource _profileRegistry;
    private readonly IReferenceProfileProvider _profileProvider;

    public ManagedWorktreeInitializer(
        IPrimaryScopeAttachmentStore store,
        ISystemWorktreeRegistry registry,
        IWorktreeFingerprintProvider fingerprint,
        TwigConfiguration config,
        TwigPaths paths,
        IProfileRegistrySource profileRegistry,
        IReferenceProfileProvider profileProvider)
    {
        _store = store;
        _registry = registry;
        _fingerprint = fingerprint;
        _config = config;
        _paths = paths;
        _profileRegistry = profileRegistry;
        _profileProvider = profileProvider;
    }

    public async Task<Result> InitializeAsync(
        string organization,
        string project,
        string? team,
        string profileIdentity,
        string profileVersion,
        CancellationToken ct = default)
    {
        // Resolve the materialized policy BEFORE touching the filesystem so a
        // #727-unavailable failure aborts init cleanly with the named error.
        var policyResult = ResolveMaterializedPolicy(profileIdentity);
        if (!policyResult.IsSuccess)
            return Result.Fail(policyResult.Error);
        var materialized = policyResult.Value;

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

        try
        {
            // Preserve any existing configured policy. Only fill fields that
            // are genuinely missing — never overwrite a value the operator
            // has already checked in. profileVersion is intentionally
            // ignored when the block already binds a version.
            _ = profileVersion;
            _config.Policy ??= new PolicyConfig();
            _config.Policy.SelectedProfile ??= new SelectedProfileBinding();
            if (string.IsNullOrWhiteSpace(_config.Policy.SelectedProfile.Identity))
                _config.Policy.SelectedProfile.Identity = materialized.Identity;
            if (string.IsNullOrWhiteSpace(_config.Policy.SelectedProfile.Version))
                _config.Policy.SelectedProfile.Version = materialized.Version;
            if (_config.Policy.PrimaryScopeTypes is null)
                _config.Policy.PrimaryScopeTypes = new List<string>(materialized.PrimaryScopeTypes);

            // T1 §5.1 pin (AB#735). Materialized from the EMBEDDED profile,
            // which is the only correct source: the pin's whole job is to
            // record which released profile this repository is bound to, and
            // the running binary is what ships it.
            //
            // Written whole or not at all. A two-of-three pin is the subset
            // match T1 §8.2 rejects, and the pin reader treats any blank field
            // as no pin at all — so an INCOMPLETE block is replaced rather than
            // preserved. Preserving it would leave the repository permanently on
            // twig-json-profile-block-missing with `twig init` as the documented
            // recovery, and re-running init would do nothing: a repair that
            // cannot repair.
            //
            // A COMPLETE block is never touched, so an operator who deliberately
            // pinned an older release keeps it and gets a named mismatch rather
            // than a silent upgrade.
            if (IsIncompletePin(_config.Profile))
            {
                var embedded = _profileProvider.Load();
                if (!embedded.IsSuccess)
                    return Result.Fail(embedded.Error);

                _config.Profile = new ProfilePinConfig
                {
                    Identity = embedded.Value.Identity,
                    ProfileVersion = embedded.Value.ProfileVersion,
                    BaseProcessVersion = embedded.Value.BaseProcess.TailoringVersion,
                };
            }
            await _config.SaveSplitAsync(_paths, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}");
        }
        return Result.Ok();
    }

    /// <summary>
    /// Whether the checked-in <c>profile</c> block is absent or missing any of
    /// its three fields.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>TwigJsonReferenceProfilePinSource</c>'s reader exactly: it
    /// reports a partially-filled block as no pin, so init must treat the same
    /// shape as needing materialization. Two predicates that disagree would
    /// produce a repository init reports as fixed and the pin reader still calls
    /// missing.
    /// </remarks>
    private static bool IsIncompletePin(ProfilePinConfig? pin) =>
        pin is null
        || string.IsNullOrWhiteSpace(pin.Identity)
        || string.IsNullOrWhiteSpace(pin.ProfileVersion)
        || string.IsNullOrWhiteSpace(pin.BaseProcessVersion);

    /// <summary>Resolve the materialized policy: (1) an existing checked-in
    /// <see cref="PolicyConfig"/> whose <see cref="SelectedProfileBinding"/>
    /// and <see cref="PolicyConfig.PrimaryScopeTypes"/> are complete is
    /// authoritative; (2) otherwise, delegate to the
    /// <see cref="IProfileRegistrySource"/> — the future AB#727 seam. A
    /// failure surfaces <c>selected-profile-unavailable</c>.</summary>
    private Result<SelectedProfileMaterialization> ResolveMaterializedPolicy(string processTemplate)
    {
        // (1) Preserve an existing complete policy verbatim.
        var existing = _config.Policy;
        if (existing?.SelectedProfile is { Identity: { Length: > 0 } id, Version: { Length: > 0 } ver }
            && existing.PrimaryScopeTypes is { } types)
        {
            return Result.Ok(new SelectedProfileMaterialization(id, ver, types));
        }
        // (2) Delegate to the profile registry. Default implementation returns
        // `selected-profile-unavailable` until AB#727 lands.
        return _profileRegistry.Resolve(processTemplate);
    }
}

/// <summary>
/// The AB#736 §4.1 profile registry seam AB#727 will land. Until it does,
/// this default implementation returns
/// <c>selected-profile-unavailable</c> so init fails closed rather than
/// materializing synthetic identity/version values.
/// </summary>
internal sealed class UnavailableProfileRegistrySource : IProfileRegistrySource
{
    public Result<SelectedProfileMaterialization> Resolve(string processTemplate)
    {
        _ = processTemplate;
        return Result.Fail<SelectedProfileMaterialization>(AttachmentStorageFailure.SelectedProfileUnavailable);
    }
}
