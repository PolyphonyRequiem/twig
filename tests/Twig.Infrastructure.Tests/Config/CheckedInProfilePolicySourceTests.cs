using Shouldly;
using Twig.Domain.Services.Attachment;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Tests the T1 §4.1 checked-in profile policy source: materialized policy
/// block with an explicit selected-profile identity/version binding plus the
/// concrete primary-scope allow-set. Missing binding or missing allow-set
/// fails closed — the "block absent" case is a migration event, not the
/// steady-state default.
/// </summary>
public sealed class CheckedInProfilePolicySourceTests
{
    [Fact]
    public void GetAllowSet_fails_closed_when_no_policy_block_is_configured()
    {
        var config = new TwigConfiguration { Organization = "o", Project = "p" };
        config.Policy.ShouldBeNull();
        var result = new CheckedInProfilePolicySource(config).GetAllowSet();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(AttachmentStorageFailure.EligibilityUnavailable);
    }

    [Fact]
    public void GetAllowSet_fails_closed_when_selected_profile_binding_is_missing()
    {
        // Policy block present but no selectedProfile — an out-of-contract
        // manifest. Eligibility must refuse rather than silently accept the
        // allow-set.
        var config = new TwigConfiguration
        {
            Organization = "o",
            Project = "p",
            Policy = new PolicyConfig { PrimaryScopeTypes = new List<string> { "Task" } },
        };
        var result = new CheckedInProfilePolicySource(config).GetAllowSet();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(AttachmentStorageFailure.EligibilityUnavailable);
    }

    [Fact]
    public void GetAllowSet_fails_closed_when_selected_profile_identity_or_version_is_empty()
    {
        var config = new TwigConfiguration
        {
            Organization = "o",
            Project = "p",
            Policy = new PolicyConfig
            {
                SelectedProfile = new SelectedProfileBinding { Identity = "", Version = "1" },
                PrimaryScopeTypes = new List<string> { "Task" },
            },
        };
        new CheckedInProfilePolicySource(config).GetAllowSet().IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void GetAllowSet_returns_configured_types_when_binding_and_types_present()
    {
        var config = new TwigConfiguration
        {
            Organization = "o",
            Project = "p",
            Policy = new PolicyConfig
            {
                SelectedProfile = new SelectedProfileBinding { Identity = "twig/default", Version = "1" },
                PrimaryScopeTypes = new List<string> { "Task", "Bug" },
            },
        };
        var result = new CheckedInProfilePolicySource(config).GetAllowSet();
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new[] { "Task", "Bug" });
    }

    [Fact]
    public void GetAllowSet_returns_empty_set_when_binding_present_and_types_declared_none()
    {
        var config = new TwigConfiguration
        {
            Organization = "o",
            Project = "p",
            Policy = new PolicyConfig
            {
                SelectedProfile = new SelectedProfileBinding { Identity = "twig/default", Version = "1" },
                PrimaryScopeTypes = new List<string>(),
            },
        };
        var result = new CheckedInProfilePolicySource(config).GetAllowSet();
        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(0);
    }
}
