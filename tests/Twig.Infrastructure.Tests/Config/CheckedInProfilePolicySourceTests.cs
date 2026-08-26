using Shouldly;
using Twig.Domain.Services.Attachment;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

/// <summary>
/// Tests the T1 §4.1 checked-in profile policy source consumed by AB#738's
/// eligibility gate. Failure paths MUST fail closed with
/// <c>eligibility-unavailable</c> — permit-by-default is the exact defect
/// this policy source removes.
/// </summary>
public sealed class CheckedInProfilePolicySourceTests
{
    [Fact]
    public void GetAllowSet_fails_closed_when_no_policy_block_is_configured()
    {
        var config = new TwigConfiguration { Organization = "o", Project = "p" };
        // Explicitly no Policy on the twig.json manifest — the legacy shape.
        config.Policy.ShouldBeNull();

        var source = new CheckedInProfilePolicySource(config);
        var result = source.GetAllowSet();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(AttachmentStorageFailure.EligibilityUnavailable);
    }

    [Fact]
    public void GetAllowSet_returns_configured_types_when_policy_block_present()
    {
        var config = new TwigConfiguration
        {
            Organization = "o",
            Project = "p",
            Policy = new PolicyConfig { PrimaryScopeTypes = new List<string> { "Task", "Bug" } },
        };

        var source = new CheckedInProfilePolicySource(config);
        var result = source.GetAllowSet();
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new[] { "Task", "Bug" });
    }

    [Fact]
    public void GetAllowSet_returns_empty_set_when_policy_block_declares_none()
    {
        // A repo may deliberately disable primary-scope attachment by writing
        // an empty allow-set — that is a valid "no type is eligible" statement,
        // distinct from the fail-closed "unavailable" of a missing block.
        var config = new TwigConfiguration
        {
            Organization = "o",
            Project = "p",
            Policy = new PolicyConfig { PrimaryScopeTypes = new List<string>() },
        };

        var source = new CheckedInProfilePolicySource(config);
        var result = source.GetAllowSet();
        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(0);
    }
}
