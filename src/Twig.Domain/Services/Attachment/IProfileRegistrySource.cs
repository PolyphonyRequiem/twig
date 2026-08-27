using Twig.Domain.Common;

namespace Twig.Domain.Services.Attachment;

/// <summary>
/// The AB#736 §4.1 selected-profile source AB#727 will fulfill. Until #727
/// lands, the default implementation returns
/// <c>selected-profile-unavailable</c> so <c>twig init</c> fails closed
/// rather than materializing a synthetic identity/version.
/// </summary>
internal interface IProfileRegistrySource
{
    /// <summary>Resolve the pinned selected profile for a checkout whose
    /// process description advertises the given <paramref name="processTemplate"/>.
    /// A successful result carries a real identity + version + concrete
    /// primary-scope allow-set materialized from the profile registry;
    /// a failure result surfaces
    /// <see cref="AttachmentStorageFailure.EligibilityUnavailable"/> or a
    /// dedicated <c>selected-profile-unavailable</c> identifier so the init
    /// verb reports the migration event verbatim.</summary>
    Result<SelectedProfileMaterialization> Resolve(string processTemplate);
}

/// <summary>The materialized policy of the selected pinned profile: the T1
/// §4.1 shape the checked-in twig.json embeds and AB#738's eligibility gate
/// consumes.</summary>
internal readonly record struct SelectedProfileMaterialization(
    string Identity,
    string Version,
    IReadOnlyList<string> PrimaryScopeTypes);
