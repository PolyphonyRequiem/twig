using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Default composition of AB#736 §6.3 (local layout), §9.5 (system-store
/// registration), and §4.1 policy materialization. Every underlying
/// primitive is idempotent; the composed run stops at the first §8 failure.
/// <para>
/// An absent selection leaves the checkout unprofiled. Existing declarations
/// are validated before writes and never repaired, replaced, or upgraded.
/// Policy materialization records do not select a profile.
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
        string? profileIdentity = null,
        CancellationToken ct = default)
    {
        var selection = ResolveMaterializedPolicy(profileIdentity);
        if (!selection.IsSuccess)
            return Result.Fail(selection.Error);

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

        // Only an explicit, newly selected profile needs materialization. A
        // present pin and its existing policy records are left untouched.
        if (_config.Profile is not null || selection.Value is not { } materialized)
            return Result.Ok();

        var loaded = _profileProvider.Load();
        if (!loaded.IsSuccess)
            return Result.Fail(loaded.Error);

        var originalProfile = _config.Profile;
        var originalPolicy = _config.Policy;
        var originalBinding = originalPolicy?.SelectedProfile;
        var originalBindingValues = originalBinding is null
            ? (Identity: string.Empty, Version: string.Empty)
            : (originalBinding.Identity, originalBinding.Version);
        var originalScopeTypes = originalPolicy?.PrimaryScopeTypes;
        var persisted = false;

        try
        {
            _config.Policy ??= new PolicyConfig();
            _config.Policy.SelectedProfile ??= new SelectedProfileBinding();
            if (string.IsNullOrWhiteSpace(_config.Policy.SelectedProfile.Identity))
                _config.Policy.SelectedProfile.Identity = materialized.Identity;
            if (string.IsNullOrWhiteSpace(_config.Policy.SelectedProfile.Version))
                _config.Policy.SelectedProfile.Version = materialized.Version;
            _config.Policy.PrimaryScopeTypes ??= new List<string>(materialized.PrimaryScopeTypes);

            _config.Profile = new ProfilePinConfig
            {
                Identity = loaded.Value.Identity,
                ProfileVersion = loaded.Value.ProfileVersion,
                BaseProcessVersion = loaded.Value.BaseProcess.TailoringVersion,
            };
            await _config.SaveSplitAsync(_paths, ct).ConfigureAwait(false);
            persisted = true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return Result.Fail($"{AttachmentStorageFailure.AtomicWriteFailed}: {ex.Message}");
        }
        finally
        {
            // A failed save must not turn a later same-instance retry into an
            // apparent already-pinned success. Restore aliases as well as values.
            if (!persisted)
            {
                _config.Profile = originalProfile;
                _config.Policy = originalPolicy;
                if (originalPolicy is not null)
                {
                    originalPolicy.SelectedProfile = originalBinding;
                    originalPolicy.PrimaryScopeTypes = originalScopeTypes;
                }
                if (originalBinding is not null)
                {
                    originalBinding.Identity = originalBindingValues.Identity;
                    originalBinding.Version = originalBindingValues.Version;
                }
            }
        }
        return Result.Ok();
    }

    // InitCommand uses the same admission before archiving or deleting any
    // previous layout; direct callers are admitted again by InitializeAsync.
    internal Result ValidateProfileSelection(string? profileIdentity)
    {
        var selection = ResolveMaterializedPolicy(profileIdentity);
        return selection.IsSuccess ? Result.Ok() : Result.Fail(selection.Error);
    }

    private Result<SelectedProfileMaterialization?> ResolveMaterializedPolicy(string? profileIdentity)
    {
        if (_config.Profile is not null)
        {
            var pin = _profileProvider.ValidatePin();
            if (!pin.IsSuccess)
                return Result.Fail<SelectedProfileMaterialization?>(pin.Error);
        }

        // Absence is not consent. In particular, policy.selectedProfile is a
        // materialization record, not an alternative runtime authority.
        if (profileIdentity is null)
            return Result.Ok<SelectedProfileMaterialization?>(null);

        if (_config.Profile is { } existingPin
            && !string.Equals(profileIdentity, existingPin.Identity, StringComparison.Ordinal))
            return Result.Fail<SelectedProfileMaterialization?>(ReferenceProfileErrors.ProfileIdentityUnknown);

        var resolved = _profileRegistry.Resolve(profileIdentity);
        if (!resolved.IsSuccess)
            return Result.Fail<SelectedProfileMaterialization?>(resolved.Error);

        var loaded = _profileProvider.Load();
        if (!loaded.IsSuccess)
            return Result.Fail<SelectedProfileMaterialization?>(loaded.Error);

        var materialized = resolved.Value;
        if (!string.Equals(profileIdentity, materialized.Identity, StringComparison.Ordinal)
            || !string.Equals(materialized.Identity, loaded.Value.Identity, StringComparison.Ordinal))
            return Result.Fail<SelectedProfileMaterialization?>(ReferenceProfileErrors.ProfileIdentityUnknown);
        if (!string.Equals(materialized.Version, loaded.Value.ProfileVersion, StringComparison.Ordinal))
            return Result.Fail<SelectedProfileMaterialization?>(ReferenceProfileErrors.ProfileVersionMismatch);

        var binding = _config.Policy?.SelectedProfile;
        if (!string.IsNullOrWhiteSpace(binding?.Identity)
            && !string.Equals(binding.Identity, materialized.Identity, StringComparison.Ordinal))
            return Result.Fail<SelectedProfileMaterialization?>(ReferenceProfileErrors.ProfileIdentityUnknown);
        if (!string.IsNullOrWhiteSpace(binding?.Version)
            && !string.Equals(binding.Version, materialized.Version, StringComparison.Ordinal))
            return Result.Fail<SelectedProfileMaterialization?>(ReferenceProfileErrors.ProfileVersionMismatch);

        return Result.Ok<SelectedProfileMaterialization?>(materialized);
    }
}

/// <summary>
/// Failure-path registry source for callers selecting a profile when the
/// registry is unavailable. Unselected initialization does not consult it.
/// </summary>
internal sealed class UnavailableProfileRegistrySource : IProfileRegistrySource
{
    public Result<SelectedProfileMaterialization> Resolve(string profileIdentity)
    {
        _ = profileIdentity;
        return Result.Fail<SelectedProfileMaterialization>(AttachmentStorageFailure.SelectedProfileUnavailable);
    }
}
