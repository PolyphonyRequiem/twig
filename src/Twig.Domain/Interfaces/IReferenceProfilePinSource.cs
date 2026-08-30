using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Supplies the repository's checked-in reference-profile pin (T1 AB#732 §5.1)
/// to the profile seam, without the seam knowing how <c>twig.json</c> is
/// located, loaded, or partitioned.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IReferenceProfileProvider"/> because the two answer
/// questions with different failure modes and different repair paths: the
/// embedded profile is wrong only if the binary is corrupt ("reinstall twig"),
/// whereas the pin is wrong whenever a repository and a binary disagree about
/// which profile release is in force ("bump the pin, or install the matching
/// twig"). Folding the pin into the provider's own construction would make a
/// config edit indistinguishable from a tampered release.
/// </para>
/// <para>
/// 🔴 A <c>null</c> return means the <c>profile</c> block is ABSENT, which is a
/// named failure (<c>twig-json-profile-block-missing</c>) and never a
/// permissive default. An absent pin cannot be treated as "matches whatever is
/// installed" — that is precisely the silent coupling the pin exists to
/// prevent.
/// </para>
/// </remarks>
public interface IReferenceProfilePinSource
{
    /// <summary>
    /// The checked-in pin, or <c>null</c> when the repository declares no
    /// <c>profile</c> block at all.
    /// </summary>
    ReferenceProfilePin? GetPin();
}
