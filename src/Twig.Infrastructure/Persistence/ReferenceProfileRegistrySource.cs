using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// The T3 cutover (AB#727): resolves the pinned selected profile from the
/// embedded reference profile rather than failing closed.
/// <para>
/// Before this landed, the only registered <see cref="IProfileRegistrySource"/>
/// was <see cref="UnavailableProfileRegistrySource"/>, so every
/// <c>twig init</c> in a fresh worktree died with
/// <c>selected-profile-unavailable</c> — which made AB#727 itself impossible to
/// work in an isolated worktree, because binding a workspace there required the
/// feature AB#727 delivers.
/// </para>
/// <para>
/// The materialization is taken verbatim from the embedded profile document:
/// <see cref="ReferenceProfile.Identity"/>,
/// <see cref="ReferenceProfile.ProfileVersion"/>, and the concrete allow-set
/// from <see cref="ReferenceProfile.PrimaryScopeAllowTypeNames"/>. Nothing is
/// synthesized — the T1 §6.3 "no synthetic identity, no partial workspace" rule
/// still holds, because a profile that fails to load propagates its own named
/// error instead of a fabricated value.
/// </para>
/// </summary>
internal sealed class ReferenceProfileRegistrySource(IReferenceProfileProvider profileProvider)
    : IProfileRegistrySource
{
    private readonly IReferenceProfileProvider _profileProvider = profileProvider;

    public Result<SelectedProfileMaterialization> Resolve(string processTemplate)
    {
        // The selected profile is pinned by the running binary, not derived from
        // the live process template. The parameter stays on the interface because
        // a future multi-profile registry selects on it; today exactly one profile
        // ships, so honoring it would be inventing a selection rule T1 §6.1 does
        // not define.
        _ = processTemplate;

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
        return Result.Ok(new SelectedProfileMaterialization(
            profile.Identity,
            profile.ProfileVersion,
            profile.PrimaryScopeAllowTypeNames));
    }
}
