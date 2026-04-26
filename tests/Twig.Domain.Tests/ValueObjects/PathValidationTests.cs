using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class PathValidationTests
{
    // ── ValidateBackslashPath ───────────────────────────────────────────

    [Theory]
    [InlineData("MyProject")]
    [InlineData(@"MyProject\Team A")]
    [InlineData(@"MyProject\Team A\SubTeam")]
    public void ValidateBackslashPath_ValidPath_ReturnsSuccess(string path)
    {
        // Exercise the shared validation indirectly via AreaPath.Parse, which delegates to PathValidation.
        var result = AreaPath.Parse(path);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(path);
    }

    [Fact]
    public void ValidateBackslashPath_Null_ReturnsFail()
    {
        var result = AreaPath.Parse(null);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("null");
    }

    [Fact]
    public void ValidateBackslashPath_Empty_ReturnsFail()
    {
        var result = IterationPath.Parse("");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty");
    }

    [Fact]
    public void ValidateBackslashPath_Whitespace_ReturnsFail()
    {
        var result = IterationPath.Parse("   ");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty");
    }

    [Fact]
    public void ValidateBackslashPath_DoubleBackslash_ReturnsFail()
    {
        var result = AreaPath.Parse(@"Project\\Team");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty segment");
    }

    [Fact]
    public void ValidateBackslashPath_LeadingBackslash_ReturnsFail()
    {
        var result = IterationPath.Parse(@"\Project\Team");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty segment");
    }

    [Fact]
    public void ValidateBackslashPath_TrailingBackslash_ReturnsFail()
    {
        var result = AreaPath.Parse(@"Project\Team\");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty segment");
    }

    [Fact]
    public void ValidateBackslashPath_TrimsWhitespace()
    {
        var result = IterationPath.Parse("  MyProject  ");
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe("MyProject");
    }

    [Fact]
    public void ValidateBackslashPath_ErrorMessageIncludesPathKind()
    {
        var areaResult = AreaPath.Parse(null);
        areaResult.Error.ShouldContain("Area path");

        var iterResult = IterationPath.Parse(null);
        iterResult.Error.ShouldContain("Iteration path");
    }

    // ── IsUnder (shared logic) ──────────────────────────────────────────

    [Fact]
    public void IsUnder_BothTypesShareSameSemantics()
    {
        // Verify AreaPath and IterationPath IsUnder produce the same results for the same inputs.
        var areaChild = AreaPath.Parse(@"Project\Team\Sub").Value;
        var areaAncestor = AreaPath.Parse(@"Project\Team").Value;

        var iterChild = IterationPath.Parse(@"Project\Team\Sub").Value;
        var iterAncestor = IterationPath.Parse(@"Project\Team").Value;

        areaChild.IsUnder(areaAncestor).ShouldBe(iterChild.IsUnder(iterAncestor));
    }
}
