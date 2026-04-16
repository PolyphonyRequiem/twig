using Shouldly;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

/// <summary>
/// Tests for <see cref="CompanionTools"/>, <see cref="UpdateResult"/>, and <see cref="CompanionUpdateResult"/>.
/// </summary>
public sealed class CompanionToolTests
{
    // ═══════════════════════════════════════════════════════════════
    //  CompanionTools.All
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void All_ContainsExpectedCompanions()
    {
        CompanionTools.All.ShouldContain("twig-mcp");
        CompanionTools.All.ShouldContain("twig-tui");
    }

    // ═══════════════════════════════════════════════════════════════
    //  CompanionTools.GetExeName
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GetExeName_ReturnsCorrectPlatformName()
    {
        var result = CompanionTools.GetExeName("twig-mcp");

        if (OperatingSystem.IsWindows())
            result.ShouldBe("twig-mcp.exe");
        else
            result.ShouldBe("twig-mcp");
    }

    // ═══════════════════════════════════════════════════════════════
    //  UpdateResult / CompanionUpdateResult record construction
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void UpdateResult_RoundTrips_Properties()
    {
        var companions = new List<CompanionUpdateResult>
        {
            new("twig-mcp", true, "/usr/local/bin/twig-mcp"),
            new("twig-tui", false, null),
        };

        var result = new UpdateResult("/usr/local/bin/twig", companions);

        result.MainBinaryPath.ShouldBe("/usr/local/bin/twig");
        result.Companions.Count.ShouldBe(2);
        result.Companions[0].Found.ShouldBeTrue();
        result.Companions[0].InstalledPath.ShouldBe("/usr/local/bin/twig-mcp");
        result.Companions[1].Found.ShouldBeFalse();
        result.Companions[1].InstalledPath.ShouldBeNull();
    }
}
