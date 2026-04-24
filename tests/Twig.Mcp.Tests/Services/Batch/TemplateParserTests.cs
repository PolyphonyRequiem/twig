using Shouldly;
using Twig.Mcp.Services.Batch;
using Xunit;

namespace Twig.Mcp.Tests.Services.Batch;

public class TemplateParserParseTests
{
    // ── Literal-only inputs ──────────────────────────────────────────────

    [Fact]
    public void Parse_PlainText_ReturnsLiteralOnlyTemplateString()
    {
        var result = TemplateParser.Parse("just plain text");

        result.Raw.ShouldBe("just plain text");
        result.IsFullExpression.ShouldBeFalse();
        result.HasExpressions.ShouldBeFalse();
        result.Segments.Count.ShouldBe(1);
        result.Segments[0].ShouldBeOfType<LiteralSegment>()
            .Text.ShouldBe("just plain text");
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmptySegments()
    {
        var result = TemplateParser.Parse("");

        result.Raw.ShouldBe("");
        result.Segments.ShouldBeEmpty();
        result.IsFullExpression.ShouldBeFalse();
        result.HasExpressions.ShouldBeFalse();
    }

    // ── Single full expression ───────────────────────────────────────────

    [Fact]
    public void Parse_SingleExpression_IsFullExpression()
    {
        var result = TemplateParser.Parse("{{steps.0.id}}");

        result.Raw.ShouldBe("{{steps.0.id}}");
        result.IsFullExpression.ShouldBeTrue();
        result.HasExpressions.ShouldBeTrue();
        result.Segments.Count.ShouldBe(1);

        var expr = result.Segments[0].ShouldBeOfType<ExpressionSegment>().Expr;
        expr.StepIndex.ShouldBe(0);
        expr.FieldPath.ShouldBe(new[] { "id" });
        expr.FullPlaceholder.ShouldBe("{{steps.0.id}}");
    }

    [Fact]
    public void Parse_NestedFieldPath_ParsesAllSegments()
    {
        var result = TemplateParser.Parse("{{steps.2.item.nested.deep}}");

        result.IsFullExpression.ShouldBeTrue();
        var expr = result.Segments[0].ShouldBeOfType<ExpressionSegment>().Expr;
        expr.StepIndex.ShouldBe(2);
        expr.FieldPath.ShouldBe(new[] { "item", "nested", "deep" });
    }

    // ── Expression with surrounding text ─────────────────────────────────

    [Fact]
    public void Parse_ExpressionWithPrefix_ProducesLiteralThenExpression()
    {
        var result = TemplateParser.Parse("prefix-{{steps.0.id}}");

        result.IsFullExpression.ShouldBeFalse();
        result.Segments.Count.ShouldBe(2);
        result.Segments[0].ShouldBeOfType<LiteralSegment>()
            .Text.ShouldBe("prefix-");
        result.Segments[1].ShouldBeOfType<ExpressionSegment>()
            .Expr.StepIndex.ShouldBe(0);
    }

    [Fact]
    public void Parse_ExpressionWithSuffix_ProducesExpressionThenLiteral()
    {
        var result = TemplateParser.Parse("{{steps.1.title}}-suffix");

        result.IsFullExpression.ShouldBeFalse();
        result.Segments.Count.ShouldBe(2);
        result.Segments[0].ShouldBeOfType<ExpressionSegment>()
            .Expr.FieldPath.ShouldBe(new[] { "title" });
        result.Segments[1].ShouldBeOfType<LiteralSegment>()
            .Text.ShouldBe("-suffix");
    }

    [Fact]
    public void Parse_ExpressionSurroundedByText_ThreeSegments()
    {
        var result = TemplateParser.Parse("Item #{{steps.0.id}} created");

        result.IsFullExpression.ShouldBeFalse();
        result.Segments.Count.ShouldBe(3);
        result.Segments[0].ShouldBeOfType<LiteralSegment>()
            .Text.ShouldBe("Item #");
        result.Segments[1].ShouldBeOfType<ExpressionSegment>()
            .Expr.StepIndex.ShouldBe(0);
        result.Segments[2].ShouldBeOfType<LiteralSegment>()
            .Text.ShouldBe(" created");
    }

    // ── Multiple expressions ─────────────────────────────────────────────

    [Fact]
    public void Parse_TwoExpressions_AdjacentWithNoLiteral()
    {
        var result = TemplateParser.Parse("{{steps.0.id}}{{steps.1.title}}");

        result.IsFullExpression.ShouldBeFalse();
        result.Segments.Count.ShouldBe(2);
        result.Segments[0].ShouldBeOfType<ExpressionSegment>()
            .Expr.StepIndex.ShouldBe(0);
        result.Segments[1].ShouldBeOfType<ExpressionSegment>()
            .Expr.StepIndex.ShouldBe(1);
    }

    [Fact]
    public void Parse_TwoExpressions_SeparatedByLiteral()
    {
        var result = TemplateParser.Parse("{{steps.0.id}} - {{steps.1.title}}");

        result.Segments.Count.ShouldBe(3);
        result.Segments[0].ShouldBeOfType<ExpressionSegment>()
            .Expr.FieldPath.ShouldBe(new[] { "id" });
        result.Segments[1].ShouldBeOfType<LiteralSegment>()
            .Text.ShouldBe(" - ");
        result.Segments[2].ShouldBeOfType<ExpressionSegment>()
            .Expr.FieldPath.ShouldBe(new[] { "title" });
    }

    [Fact]
    public void Parse_MultipleExpressions_PreservesRawInput()
    {
        const string input = "A{{steps.0.x}}B{{steps.1.y}}C";
        var result = TemplateParser.Parse(input);

        result.Raw.ShouldBe(input);
        result.Segments.Count.ShouldBe(5);
    }

    // ── Step index edge cases ────────────────────────────────────────────

    [Fact]
    public void Parse_LargeStepIndex_ParsesCorrectly()
    {
        var result = TemplateParser.Parse("{{steps.49.output}}");

        var expr = result.Segments[0].ShouldBeOfType<ExpressionSegment>().Expr;
        expr.StepIndex.ShouldBe(49);
    }

    [Fact]
    public void Parse_StepIndexZero_ParsesCorrectly()
    {
        var result = TemplateParser.Parse("{{steps.0.result}}");

        var expr = result.Segments[0].ShouldBeOfType<ExpressionSegment>().Expr;
        expr.StepIndex.ShouldBe(0);
    }

    // ── Non-matching patterns treated as literals ────────────────────────

    [Fact]
    public void Parse_MalformedSingleBraces_TreatedAsLiteral()
    {
        var result = TemplateParser.Parse("{steps.0.id}");

        result.HasExpressions.ShouldBeFalse();
        result.Segments.Count.ShouldBe(1);
        result.Segments[0].ShouldBeOfType<LiteralSegment>();
    }

    [Fact]
    public void Parse_MissingFieldPath_TreatedAsLiteral()
    {
        // {{steps.0}} has no field path — doesn't match the pattern
        var result = TemplateParser.Parse("{{steps.0}}");

        result.HasExpressions.ShouldBeFalse();
        result.Segments[0].ShouldBeOfType<LiteralSegment>();
    }

    [Fact]
    public void Parse_NonNumericStepIndex_TreatedAsLiteral()
    {
        var result = TemplateParser.Parse("{{steps.abc.id}}");

        result.HasExpressions.ShouldBeFalse();
    }

    [Fact]
    public void Parse_IncompleteBraces_TreatedAsLiteral()
    {
        var result = TemplateParser.Parse("{{steps.0.id}");

        result.HasExpressions.ShouldBeFalse();
    }

    [Fact]
    public void Parse_FieldPathStartingWithDigit_TreatedAsLiteral()
    {
        var result = TemplateParser.Parse("{{steps.0.1invalid}}");

        result.HasExpressions.ShouldBeFalse();
    }

    [Fact]
    public void Parse_SpacesInsideBraces_TreatedAsLiteral()
    {
        var result = TemplateParser.Parse("{{ steps.0.id }}");

        result.HasExpressions.ShouldBeFalse();
    }

    // ── Null input ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_NullInput_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => TemplateParser.Parse(null!));
    }

    // ── Field path with underscores ──────────────────────────────────────

    [Fact]
    public void Parse_FieldPathWithUnderscores_ParsesCorrectly()
    {
        var result = TemplateParser.Parse("{{steps.0.my_field}}");

        var expr = result.Segments[0].ShouldBeOfType<ExpressionSegment>().Expr;
        expr.FieldPath.ShouldBe(new[] { "my_field" });
    }

    [Fact]
    public void Parse_FieldPathStartingWithUnderscore_ParsesCorrectly()
    {
        var result = TemplateParser.Parse("{{steps.0._private}}");

        var expr = result.Segments[0].ShouldBeOfType<ExpressionSegment>().Expr;
        expr.FieldPath.ShouldBe(new[] { "_private" });
    }

    // ── Step index overflow / boundary tests ─────────────────────────────

    [Fact]
    public void Parse_StepIndexAtInt32MaxValue_ParsesCorrectly()
    {
        var result = TemplateParser.Parse("{{steps.2147483647.id}}");

        result.HasExpressions.ShouldBeTrue();
        result.IsFullExpression.ShouldBeTrue();
        var expr = result.Segments[0].ShouldBeOfType<ExpressionSegment>().Expr;
        expr.StepIndex.ShouldBe(int.MaxValue);
        expr.FieldPath.ShouldBe(new[] { "id" });
    }

    [Fact]
    public void Parse_StepIndexExceedsInt32_TreatedAsLiteral()
    {
        // 2147483648 is Int32.MaxValue + 1 — 10 digits, matches regex but overflows int.Parse
        var result = TemplateParser.Parse("{{steps.2147483648.id}}");

        result.HasExpressions.ShouldBeFalse();
        result.Segments.Count.ShouldBe(1);
        result.Segments[0].ShouldBeOfType<LiteralSegment>()
            .Text.ShouldBe("{{steps.2147483648.id}}");
    }

    [Fact]
    public void Parse_StepIndexOverflow_ElevenDigits_TreatedAsLiteral()
    {
        // 11 digits — rejected by regex \d{1,10}, never matches
        var result = TemplateParser.Parse("{{steps.99999999999.id}}");

        result.HasExpressions.ShouldBeFalse();
        result.Segments.Count.ShouldBe(1);
        result.Segments[0].ShouldBeOfType<LiteralSegment>();
    }
}

public class TemplateParserExtractExpressionsTests
{
    [Fact]
    public void ExtractExpressions_NoExpressions_ReturnsEmpty()
    {
        var result = TemplateParser.ExtractExpressions("plain text");
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractExpressions_SingleExpression_ReturnsOne()
    {
        var result = TemplateParser.ExtractExpressions("use {{steps.0.id}} here");

        result.Count.ShouldBe(1);
        result[0].StepIndex.ShouldBe(0);
        result[0].FieldPath.ShouldBe(new[] { "id" });
        result[0].FullPlaceholder.ShouldBe("{{steps.0.id}}");
    }

    [Fact]
    public void ExtractExpressions_MultipleExpressions_ReturnsAll()
    {
        var result = TemplateParser.ExtractExpressions(
            "{{steps.0.id}} and {{steps.1.title}} and {{steps.2.output.nested}}");

        result.Count.ShouldBe(3);
        result[0].StepIndex.ShouldBe(0);
        result[1].StepIndex.ShouldBe(1);
        result[2].StepIndex.ShouldBe(2);
        result[2].FieldPath.ShouldBe(new[] { "output", "nested" });
    }

    [Fact]
    public void ExtractExpressions_NullInput_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => TemplateParser.ExtractExpressions(null!));
    }

    [Fact]
    public void ExtractExpressions_EmptyString_ReturnsEmpty()
    {
        var result = TemplateParser.ExtractExpressions("");
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractExpressions_DuplicateExpressions_ReturnsAll()
    {
        var result = TemplateParser.ExtractExpressions(
            "{{steps.0.id}} and {{steps.0.id}}");

        result.Count.ShouldBe(2);
        result[0].ShouldBe(result[1]);
    }
}
