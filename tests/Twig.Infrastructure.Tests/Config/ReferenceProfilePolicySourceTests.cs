using Shouldly;
using Twig.Domain.Services.Attachment;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services.ReferenceProfile;
using Twig.Infrastructure.Tests.Services.ReferenceProfile;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// The T1 §8.1 cutover (AB#735): <see cref="IPrimaryScopePolicySource"/> is a
/// thin adapter over the reference profile's own allow-set. These replace the
/// checked-in-policy tests — that source read <c>twig.json</c>'s
/// <c>policy.primaryScopeTypes</c> as authoritative, and the whole point of the
/// cutover is that it no longer is.
/// </summary>
public sealed class ReferenceProfilePolicySourceTests
{
    [Fact]
    public void GetAllowSet_returns_the_profiles_own_type_names()
    {
        var profile = Twig.TestKit.ReferenceProfileBuilder.Build();
        var source = new ReferenceProfilePolicySource(
            Twig.TestKit.ReferenceProfileBuilder.Provider(profile));

        var result = source.GetAllowSet();

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value.ShouldBe(profile.PrimaryScopeAllowTypeNames);
    }

    /// <summary>
    /// The behaviour the cutover exists to produce: the allow-set follows the
    /// PROFILE's binding, so a fork that rebinds the leaf role is honoured
    /// without any code change — and, conversely, a repository cannot widen the
    /// set by editing a manifest.
    /// </summary>
    [Fact]
    public void GetAllowSet_follows_the_profile_binding_rather_than_a_literal()
    {
        var rebound = Twig.TestKit.ReferenceProfileBuilder.Build(taskTypeName: "Chore");
        var source = new ReferenceProfilePolicySource(
            Twig.TestKit.ReferenceProfileBuilder.Provider(rebound));

        var result = source.GetAllowSet();

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value.ShouldContain("Chore");
        result.Value.ShouldNotContain("Task");
    }

    [Fact]
    public void GetAllowSet_fails_closed_and_names_the_profile_error_when_the_profile_is_unusable()
    {
        var source = new ReferenceProfilePolicySource(
            Twig.TestKit.ReferenceProfileBuilder.FailingProvider(
                ReferenceProfileErrors.ProfileBlobNotFound));

        var result = source.GetAllowSet();

        result.IsSuccess.ShouldBeFalse();
        // Not flattened to eligibility-unavailable: a corrupt install and an
        // out-of-date pin need different repairs, and only the specific
        // identifier says which.
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileBlobNotFound);
    }

    /// <summary>
    /// T1 §6.1 as a production gate. Answering the eligibility question from a
    /// profile release the repository is not pinned to would look entirely
    /// healthy while consulting the wrong document.
    /// </summary>
    [Fact]
    public void GetAllowSet_refuses_when_the_repository_pin_does_not_match_the_binary()
    {
        var source = new ReferenceProfilePolicySource(
            Twig.TestKit.ReferenceProfileBuilder.PinFailingProvider(
                ReferenceProfileErrors.ProfileVersionMismatch));

        var result = source.GetAllowSet();

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.ProfileVersionMismatch);
    }

    /// <summary>
    /// End-to-end against the SHIPPED artifact and this repository's real pin
    /// values — the wiring assertion the stub-based cases above cannot make.
    /// </summary>
    [Fact]
    public void Shipped_profile_and_matching_pin_produce_the_declared_allow_set()
    {
        var provider = new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching());
        var result = new ReferenceProfilePolicySource(provider).GetAllowSet();

        result.IsSuccess.ShouldBeTrue(result.Error);
        result.Value.ShouldBe(new[] { "Initiative", "Investigation", "Feature", "Bug", "Task" });
    }

    [Fact]
    public void Missing_twig_json_profile_block_is_named_rather_than_defaulted()
    {
        var provider = new EmbeddedReferenceProfileProvider(ProfilePinSources.Missing());
        var result = new ReferenceProfilePolicySource(provider).GetAllowSet();

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.TwigJsonProfileBlockMissing);
    }
}

/// <summary>Tests the default #727-unavailable profile registry source.</summary>
public sealed class UnavailableProfileRegistrySourceTests
{
    [Fact]
    public void Resolve_always_fails_with_selected_profile_unavailable()
    {
        var source = new UnavailableProfileRegistrySource();
        var result = source.Resolve("anyProcess");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(AttachmentStorageFailure.SelectedProfileUnavailable);
    }
}
