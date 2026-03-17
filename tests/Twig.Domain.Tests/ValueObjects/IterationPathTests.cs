using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class IterationPathTests
{
    [Theory]
    [InlineData("MyProject")]
    [InlineData(@"MyProject\Sprint 1")]
    [InlineData(@"MyProject\Release 1\Sprint 1")]
    public void Parse_ValidPath_ReturnsSuccess(string path)
    {
        var result = IterationPath.Parse(path);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(path);
    }

    [Fact]
    public void Parse_Null_ReturnsFail()
    {
        var result = IterationPath.Parse(null);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("null");
    }

    [Fact]
    public void Parse_Empty_ReturnsFail()
    {
        var result = IterationPath.Parse("");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty");
    }

    [Fact]
    public void Parse_Whitespace_ReturnsFail()
    {
        var result = IterationPath.Parse("   ");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty");
    }

    [Fact]
    public void Parse_EmptySegment_DoubleBackslash_ReturnsFail()
    {
        var result = IterationPath.Parse(@"MyProject\\Sprint 1");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty segment");
    }

    [Fact]
    public void Parse_LeadingBackslash_ReturnsFail()
    {
        var result = IterationPath.Parse(@"\MyProject\Sprint 1");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty segment");
    }

    [Fact]
    public void Parse_TrailingBackslash_ReturnsFail()
    {
        var result = IterationPath.Parse(@"MyProject\Sprint 1\");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty segment");
    }

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var result = IterationPath.Parse("  MyProject  ");
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("MyProject");
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        var result = IterationPath.Parse(@"MyProject\Sprint 1");
        result.Value.ToString().ShouldBe(@"MyProject\Sprint 1");
    }

    [Fact]
    public void Equality_SameValue()
    {
        var a = IterationPath.Parse("MyProject").Value;
        var b = IterationPath.Parse("MyProject").Value;
        a.ShouldBe(b);
    }

    [Fact]
    public void Inequality_DifferentValue()
    {
        var a = IterationPath.Parse("ProjectA").Value;
        var b = IterationPath.Parse("ProjectB").Value;
        a.ShouldNotBe(b);
    }
}
