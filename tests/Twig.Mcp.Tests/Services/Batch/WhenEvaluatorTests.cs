using Shouldly;
using Twig.Mcp.Services.Batch;
using Xunit;
using static Twig.Mcp.Tests.Services.Batch.BatchTestHelpers;

namespace Twig.Mcp.Tests.Services.Batch;

public sealed class WhenEvaluatorTests
{
    // ── Equality operator ───────────────────────────────────────────

    [Fact]
    public void Evaluate_EqualityTrue_ReturnsTrue()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"state":"Active"}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.state}} == 'Active'", steps);

        result.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_EqualityFalse_ReturnsFalse()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"state":"Done"}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.state}} == 'Active'", steps);

        result.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_EqualityCaseInsensitive_ReturnsTrue()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"state":"active"}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.state}} == 'Active'", steps);

        result.ShouldBeTrue();
    }

    // ── Inequality operator ─────────────────────────────────────────

    [Fact]
    public void Evaluate_InequalityTrue_ReturnsTrue()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"state":"Active"}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.state}} != 'Done'", steps);

        result.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_InequalityFalse_ReturnsFalse()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"state":"Done"}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.state}} != 'Done'", steps);

        result.ShouldBeFalse();
    }

    // ── Boolean shorthand ───────────────────────────────────────────

    [Fact]
    public void Evaluate_BooleanTrue_ReturnsTrue()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"isActive":true}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.isActive}}", steps);

        result.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_BooleanFalse_ReturnsFalse()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"isActive":false}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.isActive}}", steps);

        result.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_NonEmptyString_ReturnsTrue()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"name":"hello"}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.name}}", steps);

        result.ShouldBeTrue();
    }

    // ── Literal expression (no templates) ───────────────────────────

    [Fact]
    public void Evaluate_LiteralTrue_ReturnsTrue()
    {
        var result = WhenEvaluator.Evaluate("true", []);

        result.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_LiteralFalse_ReturnsFalse()
    {
        var result = WhenEvaluator.Evaluate("false", []);

        result.ShouldBeFalse();
    }

    // ── Unquoted comparison values ──────────────────────────────────

    [Fact]
    public void Evaluate_UnquotedLiteral_ComparesCaseInsensitive()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"state":"Done"}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.state}} == Done", steps);

        result.ShouldBeTrue();
    }

    // ── Nested field paths ──────────────────────────────────────────

    [Fact]
    public void Evaluate_NestedFieldPath_ResolvesCorrectly()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"data":{"item":{"state":"Active"}}}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.data.item.state}} != 'Done'", steps);

        result.ShouldBeTrue();
    }

    // ── Multiple template references ────────────────────────────────

    [Fact]
    public void Evaluate_TwoTemplatesBothSides_ComparesResolved()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"expected":"Active"}"""),
            Succeeded(1, """{"actual":"Active"}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.expected}} == {{steps.1.actual}}", steps);

        result.ShouldBeTrue();
    }

    // ── Template resolution failure ─────────────────────────────────

    [Fact]
    public void Evaluate_TemplateRefToFailedStep_Throws()
    {
        var steps = new StepResult?[]
        {
            Failed(0)
        };

        Should.Throw<TemplateResolutionException>(
            () => WhenEvaluator.Evaluate("{{steps.0.state}} == 'Active'", steps));
    }

    [Fact]
    public void Evaluate_TemplateRefToMissingStep_Throws()
    {
        var steps = Array.Empty<StepResult?>();

        Should.Throw<TemplateResolutionException>(
            () => WhenEvaluator.Evaluate("{{steps.5.state}} == 'Active'", steps));
    }

    // ── Numeric comparison ──────────────────────────────────────────

    [Fact]
    public void Evaluate_NumericEquality_MatchesAsString()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"count":42}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.count}} == 42", steps);

        result.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_NumericInequality_ReturnsFalse()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"count":42}""")
        };

        var result = WhenEvaluator.Evaluate("{{steps.0.count}} != 42", steps);

        result.ShouldBeFalse();
    }

    // ── Operator inside quotes should not be treated as operator ─────

    [Fact]
    public void Evaluate_OperatorInsideQuotes_NotSplitOnIt()
    {
        // The == inside the single-quoted right-hand side should not be treated as the comparison operator.
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"msg":"hello"}""")
        };

        // hello == 'a == b' should find the first non-quoted == and compare "hello" vs "a == b"
        var result = WhenEvaluator.Evaluate("{{steps.0.msg}} == 'a == b'", steps);

        // "hello" != "a == b", so this should be false
        result.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_QuotedRightSideWithOperator_MatchesCorrectly()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"val":"x != y"}""")
        };

        // "x != y" == 'x != y' — the != inside quotes is ignored, outer == is the real operator
        var result = WhenEvaluator.Evaluate("{{steps.0.val}} == 'x != y'", steps);

        result.ShouldBeTrue();
    }

    // ── Whitespace handling ─────────────────────────────────────────

    [Fact]
    public void Evaluate_ExtraWhitespace_IsTrimmed()
    {
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"state":"Active"}""")
        };

        var result = WhenEvaluator.Evaluate("  {{steps.0.state}}  ==  'Active'  ", steps);

        result.ShouldBeTrue();
    }
}
