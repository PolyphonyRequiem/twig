using Shouldly;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

/// <summary>
/// Tests for <see cref="CompanionTools"/>, <see cref="UpdateResult"/>, and <see cref="CompanionUpdateResult"/>.
/// </summary>
public sealed class CompanionToolTests
{
    [Fact]
    public void All_ContainsExpectedCompanions()
    {
        CompanionTools.All.ShouldContain("twig-mcp");
        CompanionTools.All.ShouldContain("twig-tui");
    }

    [Fact]
    public void GetExeName_ReturnsCorrectPlatformName()
    {
        var result = CompanionTools.GetExeName("twig-mcp");

        if (OperatingSystem.IsWindows())
            result.ShouldBe("twig-mcp.exe");
        else
            result.ShouldBe("twig-mcp");
    }

}
