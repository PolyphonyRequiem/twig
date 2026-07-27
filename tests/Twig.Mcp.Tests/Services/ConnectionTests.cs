using Shouldly;
using Twig.Mcp.Services;
using Xunit;

namespace Twig.Mcp.Tests.Services;

public sealed class ConnectionTests
{
    // ── Round-trip ───────────────────────────────────────────────────

    [Fact]
    public void Parse_RoundTrips_ToString()
    {
        var key = Connection.Parse("contoso/MyProject");
        key.ToString().ShouldBe("contoso/MyProject");
    }

    // ── Equality ────────────────────────────────────────────────────

    [Fact]
    public void Equal_Keys_AreEqual()
    {
        var a = new Connection("org", "proj");
        var b = new Connection("org", "proj");
        a.ShouldBe(b);
    }

    [Fact]
    public void Different_Org_NotEqual()
    {
        var a = new Connection("org1", "proj");
        var b = new Connection("org2", "proj");
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Different_Project_NotEqual()
    {
        var a = new Connection("org", "proj1");
        var b = new Connection("org", "proj2");
        a.ShouldNotBe(b);
    }

    // ── Case preservation ───────────────────────────────────────────

    [Fact]
    public void Parse_PreservesCase()
    {
        var key = Connection.Parse("MyOrg/MyProject");
        key.Org.ShouldBe("MyOrg");
        key.Project.ShouldBe("MyProject");
    }

    [Fact]
    public void Different_Case_NotEqual()
    {
        var a = Connection.Parse("Org/Proj");
        var b = Connection.Parse("org/proj");
        a.ShouldNotBe(b);
    }

    // ── Whitespace trimming ─────────────────────────────────────────

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var key = Connection.Parse("  contoso  /  MyProject  ");
        key.Org.ShouldBe("contoso");
        key.Project.ShouldBe("MyProject");
    }

    // ── Invalid input — Parse throws ────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noslash")]
    [InlineData("/project")]
    [InlineData("org/")]
    [InlineData("a/b/c")]
    public void Parse_InvalidInput_ThrowsFormatException(string input)
    {
        Should.Throw<FormatException>(() => Connection.Parse(input));
    }

    // ── TryParse ────────────────────────────────────────────────────

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        Connection.TryParse(null, out var result).ShouldBeFalse();
        result.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("noslash")]
    [InlineData("a/b/c")]
    public void TryParse_BadFormat_ReturnsFalse(string input)
    {
        Connection.TryParse(input, out var result).ShouldBeFalse();
        result.ShouldBeNull();
    }

    [Fact]
    public void TryParse_ValidInput_ReturnsTrueAndKey()
    {
        Connection.TryParse("org/proj", out var result).ShouldBeTrue();
        result.ShouldNotBeNull();
        result!.Org.ShouldBe("org");
        result.Project.ShouldBe("proj");
    }
}
