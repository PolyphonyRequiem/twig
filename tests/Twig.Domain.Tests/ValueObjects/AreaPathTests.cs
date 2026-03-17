using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class AreaPathTests
{
    [Theory]
    [InlineData("MyProject")]
    [InlineData(@"MyProject\Team A")]
    [InlineData(@"MyProject\Team A\SubTeam")]
    public void Parse_ValidPath_ReturnsSuccess(string path)
    {
        var result = AreaPath.Parse(path);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(path);
    }

    [Fact]
    public void Parse_Null_ReturnsFail()
    {
        var result = AreaPath.Parse(null);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("null");
    }

    [Fact]
    public void Parse_Empty_ReturnsFail()
    {
        var result = AreaPath.Parse("");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty");
    }

    [Fact]
    public void Parse_Whitespace_ReturnsFail()
    {
        var result = AreaPath.Parse("   ");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty");
    }

    [Fact]
    public void Parse_EmptySegment_DoubleBackslash_ReturnsFail()
    {
        var result = AreaPath.Parse(@"MyProject\\Team A");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty segment");
    }

    [Fact]
    public void Parse_LeadingBackslash_ReturnsFail()
    {
        var result = AreaPath.Parse(@"\MyProject\Team A");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty segment");
    }

    [Fact]
    public void Parse_TrailingBackslash_ReturnsFail()
    {
        var result = AreaPath.Parse(@"MyProject\Team A\");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty segment");
    }

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var result = AreaPath.Parse("  MyProject  ");
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("MyProject");
    }

    [Fact]
    public void Segments_SingleSegment()
    {
        var result = AreaPath.Parse("MyProject");
        result.Value.Segments.Count.ShouldBe(1);
        result.Value.Segments[0].ShouldBe("MyProject");
    }

    [Fact]
    public void Segments_MultipleSegments()
    {
        var result = AreaPath.Parse(@"MyProject\Team A\SubTeam");
        result.Value.Segments.Count.ShouldBe(3);
        result.Value.Segments[0].ShouldBe("MyProject");
        result.Value.Segments[1].ShouldBe("Team A");
        result.Value.Segments[2].ShouldBe("SubTeam");
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        var result = AreaPath.Parse(@"MyProject\Team A");
        result.Value.ToString().ShouldBe(@"MyProject\Team A");
    }

    [Fact]
    public void Equality_SameValue()
    {
        var a = AreaPath.Parse("MyProject").Value;
        var b = AreaPath.Parse("MyProject").Value;
        a.ShouldBe(b);
    }

    [Fact]
    public void Inequality_DifferentValue()
    {
        var a = AreaPath.Parse("ProjectA").Value;
        var b = AreaPath.Parse("ProjectB").Value;
        a.ShouldNotBe(b);
    }
}
