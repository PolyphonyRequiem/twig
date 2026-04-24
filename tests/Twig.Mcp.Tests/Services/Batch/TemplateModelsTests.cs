using Shouldly;
using Twig.Mcp.Services.Batch;
using Xunit;

namespace Twig.Mcp.Tests.Services.Batch;

public class TemplateExpressionTests
{
    [Fact]
    public void Construction_SetsAllProperties()
    {
        var expr = new TemplateExpression(
            StepIndex: 0,
            FieldPath: ["item", "id"],
            FullPlaceholder: "{{steps.0.item.id}}");

        expr.StepIndex.ShouldBe(0);
        expr.FieldPath.ShouldBe(new[] { "item", "id" });
        expr.FullPlaceholder.ShouldBe("{{steps.0.item.id}}");
    }

    [Fact]
    public void Construction_SingleSegmentFieldPath()
    {
        var expr = new TemplateExpression(
            StepIndex: 2,
            FieldPath: ["id"],
            FullPlaceholder: "{{steps.2.id}}");

        expr.FieldPath.ShouldBe(new[] { "id" });
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new TemplateExpression(0, ["item", "id"], "{{steps.0.item.id}}");
        var b = new TemplateExpression(0, ["item", "id"], "{{steps.0.item.id}}");

        a.ShouldBe(b);
        (a == b).ShouldBeTrue();
    }

    [Fact]
    public void Inequality_DifferentStepIndex()
    {
        var a = new TemplateExpression(0, ["id"], "{{steps.0.id}}");
        var b = new TemplateExpression(1, ["id"], "{{steps.1.id}}");

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentFieldPath()
    {
        var a = new TemplateExpression(0, ["item", "id"], "{{steps.0.item.id}}");
        var b = new TemplateExpression(0, ["item", "title"], "{{steps.0.item.title}}");

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentFullPlaceholder()
    {
        var a = new TemplateExpression(0, ["id"], "{{steps.0.id}}");
        var b = new TemplateExpression(0, ["id"], "{{ steps.0.id }}");

        a.ShouldNotBe(b);
    }

    [Fact]
    public void GetHashCode_EqualInstances_SameHash()
    {
        var a = new TemplateExpression(0, ["item", "id"], "{{steps.0.item.id}}");
        var b = new TemplateExpression(0, ["item", "id"], "{{steps.0.item.id}}");

        a.GetHashCode().ShouldBe(b.GetHashCode());
    }
}

public class TemplateSegmentTests
{
    [Fact]
    public void LiteralSegment_StoresText()
    {
        var seg = new LiteralSegment("hello world");
        seg.Text.ShouldBe("hello world");
    }

    [Fact]
    public void ExpressionSegment_StoresExpression()
    {
        var expr = new TemplateExpression(0, ["id"], "{{steps.0.id}}");
        var seg = new ExpressionSegment(expr);
        seg.Expr.ShouldBe(expr);
    }

    [Fact]
    public void LiteralSegment_Equality()
    {
        var a = new LiteralSegment("text");
        var b = new LiteralSegment("text");
        a.ShouldBe(b);
    }

    [Fact]
    public void ExpressionSegment_Equality()
    {
        var expr = new TemplateExpression(0, ["id"], "{{steps.0.id}}");
        var a = new ExpressionSegment(expr);
        var b = new ExpressionSegment(expr);
        a.ShouldBe(b);
    }

    [Fact]
    public void MixedSegments_AreNotEqual()
    {
        TemplateSegment literal = new LiteralSegment("text");
        TemplateSegment expression = new ExpressionSegment(
            new TemplateExpression(0, ["id"], "{{steps.0.id}}"));

        literal.ShouldNotBe(expression);
    }
}

public class TemplateStringTests
{
    private static readonly IReadOnlyList<TemplateSegment> NoSegments =
        Array.Empty<TemplateSegment>();

    [Fact]
    public void Construction_LiteralOnly_NoExpressions()
    {
        var segments = new TemplateSegment[] { new LiteralSegment("plain text") };
        var ts = new TemplateString(segments, Raw: "plain text", IsFullExpression: false);

        ts.Raw.ShouldBe("plain text");
        ts.Segments.Count.ShouldBe(1);
        ts.IsFullExpression.ShouldBeFalse();
        ts.HasExpressions.ShouldBeFalse();
    }

    [Fact]
    public void Construction_EmptySegments_NoExpressions()
    {
        var ts = new TemplateString(NoSegments, Raw: "", IsFullExpression: false);

        ts.Segments.ShouldBeEmpty();
        ts.HasExpressions.ShouldBeFalse();
    }

    [Fact]
    public void Construction_SingleExpression_HasExpressionsIsTrue()
    {
        var expr = new TemplateExpression(0, ["id"], "{{steps.0.id}}");
        var segments = new TemplateSegment[] { new ExpressionSegment(expr) };
        var ts = new TemplateString(segments, Raw: "{{steps.0.id}}", IsFullExpression: true);

        ts.HasExpressions.ShouldBeTrue();
        ts.Segments.Count.ShouldBe(1);
        ts.IsFullExpression.ShouldBeTrue();
    }

    [Fact]
    public void Construction_MixedContent_IsFullExpressionFalse()
    {
        var expr = new TemplateExpression(0, ["id"], "{{steps.0.id}}");
        var segments = new TemplateSegment[]
        {
            new LiteralSegment("prefix "),
            new ExpressionSegment(expr),
            new LiteralSegment(" suffix")
        };
        var ts = new TemplateString(
            segments,
            Raw: "prefix {{steps.0.id}} suffix",
            IsFullExpression: false);

        ts.HasExpressions.ShouldBeTrue();
        ts.IsFullExpression.ShouldBeFalse();
        ts.Raw.ShouldBe("prefix {{steps.0.id}} suffix");
        ts.Segments.Count.ShouldBe(3);
    }

    [Fact]
    public void Construction_MultipleExpressions()
    {
        var expr1 = new TemplateExpression(0, ["id"], "{{steps.0.id}}");
        var expr2 = new TemplateExpression(1, ["title"], "{{steps.1.title}}");
        var segments = new TemplateSegment[]
        {
            new ExpressionSegment(expr1),
            new LiteralSegment(" - "),
            new ExpressionSegment(expr2)
        };
        var ts = new TemplateString(
            segments,
            Raw: "{{steps.0.id}} - {{steps.1.title}}",
            IsFullExpression: false);

        ts.Segments.Count.ShouldBe(3);
        ts.HasExpressions.ShouldBeTrue();
        ts.IsFullExpression.ShouldBeFalse();
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var expr = new TemplateExpression(0, ["id"], "{{steps.0.id}}");
        var segments = new TemplateSegment[] { new ExpressionSegment(expr) };
        var a = new TemplateString(segments, "{{steps.0.id}}", true);
        var b = new TemplateString(segments, "{{steps.0.id}}", true);

        a.ShouldBe(b);
        (a == b).ShouldBeTrue();
    }

    [Fact]
    public void Inequality_DifferentRaw()
    {
        var a = new TemplateString(NoSegments, "text a", false);
        var b = new TemplateString(NoSegments, "text b", false);

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentIsFullExpression()
    {
        var expr = new TemplateExpression(0, ["id"], "{{steps.0.id}}");
        var segments = new TemplateSegment[] { new ExpressionSegment(expr) };
        var a = new TemplateString(segments, "{{steps.0.id}}", true);
        var b = new TemplateString(segments, "{{steps.0.id}}", false);

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Inequality_DifferentSegments()
    {
        var exprA = new TemplateExpression(0, ["id"], "{{steps.0.id}}");
        var exprB = new TemplateExpression(1, ["id"], "{{steps.1.id}}");
        var a = new TemplateString(
            new TemplateSegment[] { new ExpressionSegment(exprA) },
            "{{steps.0.id}}", true);
        var b = new TemplateString(
            new TemplateSegment[] { new ExpressionSegment(exprB) },
            "{{steps.0.id}}", true);

        a.ShouldNotBe(b);
    }

    [Fact]
    public void HasExpressions_EmptySegments_ReturnsFalse()
    {
        var ts = new TemplateString(NoSegments, "literal", false);
        ts.HasExpressions.ShouldBeFalse();
    }

    [Fact]
    public void HasExpressions_OnlyLiterals_ReturnsFalse()
    {
        var segments = new TemplateSegment[] { new LiteralSegment("just text") };
        var ts = new TemplateString(segments, "just text", false);
        ts.HasExpressions.ShouldBeFalse();
    }

    [Fact]
    public void HasExpressions_WithExpression_ReturnsTrue()
    {
        var expr = new TemplateExpression(0, ["id"], "{{steps.0.id}}");
        var segments = new TemplateSegment[] { new ExpressionSegment(expr) };
        var ts = new TemplateString(segments, "{{steps.0.id}}", true);
        ts.HasExpressions.ShouldBeTrue();
    }

    [Fact]
    public void GetHashCode_EqualInstances_SameHash()
    {
        var expr = new TemplateExpression(0, ["id"], "{{steps.0.id}}");
        var segments = new TemplateSegment[] { new ExpressionSegment(expr) };
        var a = new TemplateString(segments, "{{steps.0.id}}", true);
        var b = new TemplateString(segments, "{{steps.0.id}}", true);

        a.GetHashCode().ShouldBe(b.GetHashCode());
    }
}
