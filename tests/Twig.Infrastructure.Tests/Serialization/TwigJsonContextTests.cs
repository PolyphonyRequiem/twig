using Shouldly;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Serialization;

/// <summary>
/// Tests for <see cref="TwigJsonContext"/> source-gen registrations.
/// </summary>
public class TwigJsonContextTests
{
    [Fact]
    public void SeedPublishRules_IsRegisteredInTwigJsonContext()
    {
        // Verify that the source-gen context includes SeedPublishRules for AOT compatibility
        TwigJsonContext.Default.SeedPublishRules.ShouldNotBeNull();
    }
}
