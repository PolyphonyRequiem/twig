using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class IterationExpressionTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Relative expressions — @current
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_AtCurrent_ReturnsRelativeWithOffsetZero()
    {
        var result = IterationExpression.Parse("@current");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Kind.ShouldBe(ExpressionKind.Relative);
        result.Value.Offset.ShouldBe(0);
        result.Value.IsRelative.ShouldBeTrue();
        result.Value.Raw.ShouldBe("@current");
    }

    [Fact]
    public void Parse_AtCurrentCaseInsensitive_ReturnsRelative()
    {
        var result = IterationExpression.Parse("@CURRENT");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Kind.ShouldBe(ExpressionKind.Relative);
        result.Value.Offset.ShouldBe(0);
    }

    [Fact]
    public void Parse_AtCurrentMixedCase_ReturnsRelative()
    {
        var result = IterationExpression.Parse("@Current");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Kind.ShouldBe(ExpressionKind.Relative);
        result.Value.Offset.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Relative expressions — @current-N
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("@current-1", -1)]
    [InlineData("@current-2", -2)]
    [InlineData("@current-10", -10)]
    public void Parse_AtCurrentMinusN_ReturnsNegativeOffset(string expression, int expectedOffset)
    {
        var result = IterationExpression.Parse(expression);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Kind.ShouldBe(ExpressionKind.Relative);
        result.Value.Offset.ShouldBe(expectedOffset);
        result.Value.IsRelative.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Relative expressions — @current+N
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("@current+1", 1)]
    [InlineData("@current+2", 2)]
    [InlineData("@current+5", 5)]
    public void Parse_AtCurrentPlusN_ReturnsPositiveOffset(string expression, int expectedOffset)
    {
        var result = IterationExpression.Parse(expression);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Kind.ShouldBe(ExpressionKind.Relative);
        result.Value.Offset.ShouldBe(expectedOffset);
        result.Value.IsRelative.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Absolute expressions
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(@"Project\Sprint 5")]
    [InlineData(@"MyProject\Iteration\Sprint 1")]
    [InlineData("Sprint 5")]
    public void Parse_AbsoluteIterationPath_ReturnsAbsolute(string expression)
    {
        var result = IterationExpression.Parse(expression);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Kind.ShouldBe(ExpressionKind.Absolute);
        result.Value.Offset.ShouldBe(0);
        result.Value.IsRelative.ShouldBeFalse();
        result.Value.Raw.ShouldBe(expression);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Invalid expressions — empty/whitespace
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Parse_EmptyOrWhitespace_ReturnsFailure(string? expression)
    {
        var result = IterationExpression.Parse(expression);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Invalid relative expressions
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_AtUnknown_ReturnsFailure()
    {
        var result = IterationExpression.Parse("@previous");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Unknown relative expression");
    }

    [Fact]
    public void Parse_AtCurrentWithTrailingText_ReturnsFailure()
    {
        var result = IterationExpression.Parse("@currentfoo");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Invalid relative expression");
    }

    [Fact]
    public void Parse_AtCurrentMinusNoNumber_ReturnsFailure()
    {
        var result = IterationExpression.Parse("@current-");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Expected a number");
    }

    [Fact]
    public void Parse_AtCurrentPlusNoNumber_ReturnsFailure()
    {
        var result = IterationExpression.Parse("@current+");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Expected a number");
    }

    [Fact]
    public void Parse_AtCurrentMinusZero_ReturnsFailure()
    {
        var result = IterationExpression.Parse("@current-0");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("positive integer");
    }

    [Fact]
    public void Parse_AtCurrentPlusZero_ReturnsFailure()
    {
        var result = IterationExpression.Parse("@current+0");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("positive integer");
    }

    [Fact]
    public void Parse_AtCurrentMinusNegative_ReturnsFailure()
    {
        var result = IterationExpression.Parse("@current--1");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("positive integer");
    }

    [Fact]
    public void Parse_AtCurrentMinusNonNumeric_ReturnsFailure()
    {
        var result = IterationExpression.Parse("@current-abc");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("positive integer");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Whitespace trimming
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_LeadingTrailingWhitespace_IsTrimmed()
    {
        var result = IterationExpression.Parse("  @current-1  ");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Raw.ShouldBe("@current-1");
        result.Value.Offset.ShouldBe(-1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ToString
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ToString_ReturnsRaw()
    {
        var expr = IterationExpression.Parse("@current-1").Value;
        expr.ToString().ShouldBe("@current-1");
    }

    [Fact]
    public void ToString_AbsoluteReturnsRaw()
    {
        var expr = IterationExpression.Parse(@"Project\Sprint 5").Value;
        expr.ToString().ShouldBe(@"Project\Sprint 5");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Equality (record struct)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Equality_SameExpression_AreEqual()
    {
        var a = IterationExpression.Parse("@current-1").Value;
        var b = IterationExpression.Parse("@current-1").Value;
        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentExpression_AreNotEqual()
    {
        var a = IterationExpression.Parse("@current-1").Value;
        var b = IterationExpression.Parse("@current+1").Value;
        a.ShouldNotBe(b);
    }
}
