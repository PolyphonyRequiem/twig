using Twig.Domain.Common;

namespace Twig.Domain.Services.Attachment;

/// <summary>
/// Resolves explicitly selected opaque released-profile identities. Process
/// templates and policy materialization records never supply implicit consent.
/// </summary>
internal interface IProfileRegistrySource
{
    /// <summary>Resolve the exact <paramref name="profileIdentity"/> from the
    /// released profile registry. Unknown identities fail closed with
    /// <c>profile-identity-unknown</c>; artifact failures retain their named errors.
    /// A successful result contains artifact identity, version and allow-set.
    /// This does not validate compatibility with an arbitrary live process.</summary>
    Result<SelectedProfileMaterialization> Resolve(string profileIdentity);
}

/// <summary>Materialization records for a selected released profile. Runtime
/// eligibility remains governed by the exact three-field profile pin.</summary>
internal readonly record struct SelectedProfileMaterialization(
    string Identity,
    string Version,
    IReadOnlyList<string> PrimaryScopeTypes);
