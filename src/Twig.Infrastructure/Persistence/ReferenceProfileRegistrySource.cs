using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Resolves an explicitly selected opaque identity from the released embedded
/// profile. Process-template metadata is not a profile selection rule.
/// Materialization values come from the artifact, never synthetic defaults.
/// </summary>
internal sealed class ReferenceProfileRegistrySource(IReferenceProfileProvider profileProvider)
    : IProfileRegistrySource
{
    private readonly IReferenceProfileProvider _profileProvider = profileProvider;

    public Result<SelectedProfileMaterialization> Resolve(string profileIdentity)
    {
        var loaded = _profileProvider.Load();
        if (!loaded.IsSuccess)
        {
            // Propagate the profile's own named identifier (ReferenceProfileErrors)
            // rather than flattening every cause to selected-profile-unavailable —
            // `twig init` reports it verbatim, and the specific identifier is what
            // makes the failure actionable.
            return Result.Fail<SelectedProfileMaterialization>(loaded.Error);
        }

        var profile = loaded.Value;
        if (!string.Equals(profileIdentity, profile.Identity, StringComparison.Ordinal))
            return Result.Fail<SelectedProfileMaterialization>(Twig.Domain.ValueObjects.ReferenceProfileErrors.ProfileIdentityUnknown);

        return Result.Ok(new SelectedProfileMaterialization(
            profile.Identity,
            profile.ProfileVersion,
            profile.PrimaryScopeAllowTypeNames));
    }
}
