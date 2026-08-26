using Shouldly;
using Twig.Domain.Services.Attachment;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

public sealed class CheckedInProfilePolicySourceTests
{
    [Fact]
    public void GetAllowSet_fails_closed_when_no_policy_block_is_configured()
    {
        var config = new TwigConfiguration { Organization = "o", Project = "p" };
        var result = new CheckedInProfilePolicySource(config).GetAllowSet();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(AttachmentStorageFailure.EligibilityUnavailable);
    }

    [Fact]
    public void GetAllowSet_fails_closed_when_selected_profile_binding_is_missing()
    {
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
    public void GetAllowSet_returns_configured_types_when_binding_and_types_present()
    {
        var config = new TwigConfiguration
        {
            Organization = "o",
            Project = "p",
            Policy = new PolicyConfig
            {
                SelectedProfile = new SelectedProfileBinding { Identity = "Agile", Version = "1" },
                PrimaryScopeTypes = new List<string> { "Task", "Bug" },
            },
        };
        var result = new CheckedInProfilePolicySource(config).GetAllowSet();
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new[] { "Task", "Bug" });
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
