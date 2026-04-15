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
    public void All_ContainsTwigMcp()
    {
        CompanionTools.All.ShouldContain("twig-mcp");
    }

    [Fact]
    public void All_ContainsTwigTui()
    {
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
    }

    [Fact]
    public void CompanionUpdateResult_InstalledPath_IsNullable()
    {
        var notFound = new CompanionUpdateResult("twig-tui", Found: false, InstalledPath: null);

        notFound.Found.ShouldBeFalse();
        notFound.InstalledPath.ShouldBeNull();
    }

    [Fact]
    public void CompanionUpdateResult_Found_WithPath()
    {
        var found = new CompanionUpdateResult("twig-mcp", Found: true, InstalledPath: "/bin/twig-mcp");

        found.Found.ShouldBeTrue();
        found.InstalledPath.ShouldBe("/bin/twig-mcp");
    }
}
