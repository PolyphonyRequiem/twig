using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Services.ReferenceProfile;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.ReferenceProfile;

/// <summary>
/// Contract tests for the T3 (AB#734) profile-lookup seam. Each declaration in
/// T1 (AB#732) §8.1 gets an assertion here — omission blocks #735.
/// </summary>
/// <remarks>
/// These tests bind to the EXTERNAL contract only. Internal helpers of the
/// provider are deliberately unnamed. If a future implementation swaps
/// <see cref="EmbeddedReferenceProfileProvider"/> for a different one, these
/// tests must all still pass without changes.
/// </remarks>
public sealed class ReferenceProfileProviderContractTests
{
    private static Twig.Domain.ValueObjects.ReferenceProfile LoadProfile()
    {
        var provider = new EmbeddedReferenceProfileProvider();
        var loaded = provider.Load();
        loaded.IsSuccess.ShouldBeTrue(loaded.Error);
        return loaded.Value;
    }

    // ---- T1 §8.1 declaration inventory --------------------------------------

    [Fact]
    public void Identity_matches_the_shipped_profile()
    {
        LoadProfile().Identity.ShouldBe("twig.reference-profile.hyperbright");
    }

    [Fact]
    public void ProfileVersion_matches_the_shipped_profile()
    {
        LoadProfile().ProfileVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public void BaseProcess_carries_parent_ref_and_tailoring_version()
    {
        var bp = LoadProfile().BaseProcess;
        bp.ParentRef.ShouldNotBeNullOrWhiteSpace();
        bp.TailoringVersion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Hierarchy_is_the_locked_vocabulary()
    {
        var h = LoadProfile().Hierarchy;
        h.Apex.ShouldBe([Role.Initiative]);
        h.Requirement.ShouldBe([Role.Investigation, Role.Feature, Role.Bug]);
        h.Leaf.ShouldBe([Role.Task]);
    }

    [Fact]
    public void TypeByRole_returns_the_declared_binding_for_every_role()
    {
        var profile = LoadProfile();
        foreach (var role in Enum.GetValues<Role>())
        {
            var binding = profile.TypeByRole(role);
            binding.Role.ShouldBe(role);
            binding.TypeName.ShouldNotBeNullOrWhiteSpace();
            binding.BacklogRole.ShouldNotBeNullOrWhiteSpace();
            binding.BacklogBehaviorRef.ShouldNotBeNullOrWhiteSpace();
            binding.States.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void RoleByTypeName_reverse_indexes_every_declared_type()
    {
        var profile = LoadProfile();
        foreach (var t in profile.Types)
            profile.RoleByTypeName(t.TypeName).ShouldBe(t.Role);
    }

    [Fact]
    public void RoleByTypeName_is_null_for_undeclared_types()
    {
        LoadProfile().RoleByTypeName("Some.Type.The.Profile.Does.Not.Declare").ShouldBeNull();
    }

    [Fact]
    public void RoleByTypeName_is_case_insensitive()
    {
        var profile = LoadProfile();
        var declared = profile.Types[0];
        profile.RoleByTypeName(declared.TypeName.ToUpperInvariant()).ShouldBe(declared.Role);
    }

    [Fact]
    public void LinkKinds_carries_all_four_vocabulary_edges()
    {
        var kinds = LoadProfile().LinkKinds;
        kinds.Count.ShouldBe(4);
        kinds.Select(k => k.Kind).ShouldBe(
            [LinkKind.ParentChild, LinkKind.PredecessorSuccessor, LinkKind.Related, LinkKind.Artifact]);
    }

    [Fact]
    public void PrimaryScope_declares_the_scope_kind_and_role_allow_set()
    {
        var ps = LoadProfile().PrimaryScope;
        ps.Kind.ShouldBe("ado-workitem");
        ps.EligibleRoles.ShouldNotBeEmpty();
        foreach (var r in ps.EligibleRoles)
            Enum.IsDefined(r).ShouldBeTrue();
    }

    [Fact]
    public void PrimaryScopeAllowTypeNames_is_the_type_name_join_of_the_role_allow_set()
    {
        var profile = LoadProfile();
        var expected = profile.PrimaryScope.EligibleRoles
            .Select(r => profile.TypeByRole(r).TypeName)
            .ToArray();
        profile.PrimaryScopeAllowTypeNames.ShouldBe(expected);
    }

    [Fact]
    public void EmbeddedFingerprint_is_lowercase_hex()
    {
        var fp = LoadProfile().EmbeddedFingerprint;
        fp.Length.ShouldBe(64);
        fp.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Load_is_cached_and_returns_the_same_instance()
    {
        var provider = new EmbeddedReferenceProfileProvider();
        var first = provider.Load().Value;
        var second = provider.Load().Value;
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    // ---- Load-time named error identifiers (T1 §7.1) ------------------------

    [Fact]
    public void Missing_embedded_resource_returns_named_error()
    {
        // Assembly with NO embedded resource by that name.
        var provider = new EmbeddedReferenceProfileProvider(typeof(int).Assembly);
        var loaded = provider.Load();
        loaded.IsSuccess.ShouldBeFalse();
        loaded.Error.ShouldBe(ReferenceProfileErrors.ProfileBlobNotFound);
    }
}
