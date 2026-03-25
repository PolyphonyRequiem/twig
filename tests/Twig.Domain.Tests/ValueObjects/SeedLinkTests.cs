using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

/// <summary>
/// Unit tests for <see cref="SeedLink"/> value equality and <see cref="SeedLinkTypes"/> mapping.
/// </summary>
public class SeedLinkTests
{
    // ═══════════════════════════════════════════════════════════════
    //  SeedLink value equality
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SeedLink_ValueEquality_SameValues_AreEqual()
    {
        var ts = DateTimeOffset.UtcNow;
        var a = new SeedLink(-1, -2, SeedLinkTypes.Related, ts);
        var b = new SeedLink(-1, -2, SeedLinkTypes.Related, ts);
        a.ShouldBe(b);
    }

    [Fact]
    public void SeedLink_ValueEquality_DifferentSource_NotEqual()
    {
        var ts = DateTimeOffset.UtcNow;
        var a = new SeedLink(-1, -2, SeedLinkTypes.Related, ts);
        var b = new SeedLink(-3, -2, SeedLinkTypes.Related, ts);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void SeedLink_ValueEquality_DifferentTarget_NotEqual()
    {
        var ts = DateTimeOffset.UtcNow;
        var a = new SeedLink(-1, -2, SeedLinkTypes.Related, ts);
        var b = new SeedLink(-1, -3, SeedLinkTypes.Related, ts);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void SeedLink_ValueEquality_DifferentLinkType_NotEqual()
    {
        var ts = DateTimeOffset.UtcNow;
        var a = new SeedLink(-1, -2, SeedLinkTypes.Blocks, ts);
        var b = new SeedLink(-1, -2, SeedLinkTypes.Related, ts);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void SeedLink_ValueEquality_DifferentCreatedAt_NotEqual()
    {
        var a = new SeedLink(-1, -2, SeedLinkTypes.Related, DateTimeOffset.UtcNow);
        var b = new SeedLink(-1, -2, SeedLinkTypes.Related, DateTimeOffset.UtcNow.AddSeconds(1));
        a.ShouldNotBe(b);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SeedLinkTypes.All
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void All_ContainsSixTypes()
    {
        SeedLinkTypes.All.Count.ShouldBe(8);
    }

    [Theory]
    [InlineData(SeedLinkTypes.ParentChild)]
    [InlineData(SeedLinkTypes.Blocks)]
    [InlineData(SeedLinkTypes.BlockedBy)]
    [InlineData(SeedLinkTypes.DependsOn)]
    [InlineData(SeedLinkTypes.DependedOnBy)]
    [InlineData(SeedLinkTypes.Related)]
    [InlineData(SeedLinkTypes.Successor)]
    [InlineData(SeedLinkTypes.Predecessor)]
    public void All_ContainsType(string linkType)
    {
        SeedLinkTypes.All.ShouldContain(linkType);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SeedLinkTypes.GetReverse()
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(SeedLinkTypes.Blocks, SeedLinkTypes.BlockedBy)]
    [InlineData(SeedLinkTypes.BlockedBy, SeedLinkTypes.Blocks)]
    [InlineData(SeedLinkTypes.DependsOn, SeedLinkTypes.DependedOnBy)]
    [InlineData(SeedLinkTypes.DependedOnBy, SeedLinkTypes.DependsOn)]
    [InlineData(SeedLinkTypes.Successor, SeedLinkTypes.Predecessor)]
    [InlineData(SeedLinkTypes.Predecessor, SeedLinkTypes.Successor)]
    public void GetReverse_DirectionalType_ReturnsInverse(string input, string expected)
    {
        SeedLinkTypes.GetReverse(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(SeedLinkTypes.ParentChild)]
    [InlineData(SeedLinkTypes.Related)]
    public void GetReverse_SymmetricType_ReturnsNull(string input)
    {
        SeedLinkTypes.GetReverse(input).ShouldBeNull();
    }

    [Fact]
    public void GetReverse_UnknownType_ReturnsNull()
    {
        SeedLinkTypes.GetReverse("unknown-type").ShouldBeNull();
    }
}
