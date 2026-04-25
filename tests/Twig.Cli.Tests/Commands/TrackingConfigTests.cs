using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class TrackingConfigTests
{
    [Fact]
    public void Default_CleanupPolicy_IsNone()
    {
        var config = new TwigConfiguration();
        config.Tracking.CleanupPolicy.ShouldBe("none");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("on-complete")]
    [InlineData("on-complete-and-past")]
    public void SetValue_TrackingCleanupPolicy_AcceptsValidValues(string policy)
    {
        var config = new TwigConfiguration();
        config.SetValue("tracking.cleanuppolicy", policy).ShouldBeTrue();
        config.Tracking.CleanupPolicy.ShouldBe(policy);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("always")]
    [InlineData("")]
    public void SetValue_TrackingCleanupPolicy_RejectsInvalidValues(string policy)
    {
        var config = new TwigConfiguration();
        config.SetValue("tracking.cleanuppolicy", policy).ShouldBeFalse();
        config.Tracking.CleanupPolicy.ShouldBe("none");
    }

    [Fact]
    public void SetValue_TrackingCleanupPolicy_IsCaseInsensitive()
    {
        var config = new TwigConfiguration();
        config.SetValue("tracking.cleanuppolicy", "On-Complete").ShouldBeTrue();
        config.Tracking.CleanupPolicy.ShouldBe("on-complete");
    }

    [Fact]
    public void TrackingConfig_RoundTrips_ThroughJson()
    {
        var config = new TwigConfiguration();
        config.Tracking.CleanupPolicy = "on-complete";

        var json = System.Text.Json.JsonSerializer.Serialize(
            config, Twig.Infrastructure.Serialization.TwigJsonContext.Default.TwigConfiguration);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize(
            json, Twig.Infrastructure.Serialization.TwigJsonContext.Default.TwigConfiguration);

        deserialized.ShouldNotBeNull();
        deserialized.Tracking.CleanupPolicy.ShouldBe("on-complete");
    }
}
