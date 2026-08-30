using Shouldly;
using Twig.Domain.Enums;
using Twig.Infrastructure.Services.ReferenceProfile;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.ReferenceProfile;

/// <summary>
/// Contract: primary-scope attachment eligibility and sprint-commitment
/// acceptance MUST be derived through the profile seam, never a literal type
/// name in code. #734 acceptance criterion (3).
/// </summary>
public sealed class ReferenceProfileScopeAcceptanceTests
{
    private static Twig.Domain.ValueObjects.ReferenceProfile Profile()
    {
        var provider = new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching());
        return provider.Load().Value;
    }

    /// <summary>
    /// Concrete implementation of the primary-scope acceptance check every
    /// caller should route through: takes a candidate type name, returns true
    /// iff the profile's role allow-set contains a role whose declared type
    /// name matches. The predicate is inlined here (not imported from a shim)
    /// so the test asserts what a caller MUST do — nothing else.
    /// </summary>
    private static bool AcceptsAsPrimaryScope(
        Twig.Domain.ValueObjects.ReferenceProfile profile, string typeName) =>
        profile.PrimaryScopeAllowTypeNames.Contains(typeName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The sprint-commitment acceptance check: only the profile's
    /// <see cref="Role.Task"/> binding is accepted directly on the sprint
    /// backlog, matching the T1 §Locked vocabulary property
    /// "sprint-entry-only-for-`task`".
    /// </summary>
    private static bool AcceptsAsDirectSprintCommitment(
        Twig.Domain.ValueObjects.ReferenceProfile profile, string typeName) =>
        string.Equals(profile.SprintTierTypeName, typeName, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Every_profile_eligible_type_name_is_accepted_as_primary_scope()
    {
        var profile = Profile();
        foreach (var role in profile.PrimaryScope.EligibleRoles)
        {
            var declared = profile.TypeByRole(role);
            AcceptsAsPrimaryScope(profile, declared.TypeName).ShouldBeTrue(
                $"role {role} bound to {declared.TypeName} is in the profile allow-set");
        }
    }

    [Fact]
    public void Undeclared_type_names_are_rejected_as_primary_scope()
    {
        var profile = Profile();
        AcceptsAsPrimaryScope(profile, "TypeThatIsNotInAnyProfile").ShouldBeFalse();
    }

    [Fact]
    public void Only_the_sprint_tier_binding_is_accepted_as_a_direct_sprint_commitment()
    {
        var profile = Profile();
        var taskBinding = profile.TypeByRole(Role.Task).TypeName;

        AcceptsAsDirectSprintCommitment(profile, taskBinding).ShouldBeTrue();

        foreach (var t in profile.Types)
        {
            if (t.Role == Role.Task) continue;
            AcceptsAsDirectSprintCommitment(profile, t.TypeName).ShouldBeFalse(
                $"only role Task is accepted as a direct sprint commitment; {t.Role} MUST be rejected");
        }
    }

    [Fact]
    public void Sprint_commitment_rejects_undeclared_type_names()
    {
        AcceptsAsDirectSprintCommitment(Profile(), "TypeNobodyDeclared").ShouldBeFalse();
    }
}
