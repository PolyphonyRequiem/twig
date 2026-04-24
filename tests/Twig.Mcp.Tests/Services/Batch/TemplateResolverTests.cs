using Shouldly;
using Twig.Mcp.Services.Batch;
using Xunit;
using static Twig.Mcp.Tests.Services.Batch.BatchTestHelpers;

namespace Twig.Mcp.Tests.Services.Batch;

public class TemplateResolverTests
{
    // ── Helper factories ─────────────────────────────────────────────────

    private static Dictionary<string, object?> Args(params (string key, object? value)[] pairs)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in pairs)
            dict[key] = value;
        return dict;
    }

    // ── Pass-through (no templates) ──────────────────────────────────────

    [Fact]
    public void Resolve_NoTemplates_ReturnsSameValues()
    {
        var args = Args(("title", "My Task"), ("count", 42));
        var steps = new StepResult?[0];

        var result = TemplateResolver.Resolve(args, steps);

        result["title"].ShouldBe("My Task");
        result["count"].ShouldBe(42);
    }

    [Fact]
    public void Resolve_NullValues_PassedThrough()
    {
        var args = Args(("key", null));
        var steps = new StepResult?[0];

        var result = TemplateResolver.Resolve(args, steps);

        result["key"].ShouldBeNull();
    }

    [Fact]
    public void Resolve_NonStringValues_PassedThrough()
    {
        var args = Args(("flag", true), ("count", 7), ("data", null));
        var steps = new StepResult?[0];

        var result = TemplateResolver.Resolve(args, steps);

        result["flag"].ShouldBe(true);
        result["count"].ShouldBe(7);
        result["data"].ShouldBeNull();
    }

    // ── Full-expression type preservation ────────────────────────────────

    [Fact]
    public void Resolve_FullExpression_IntegerPreserved()
    {
        var args = Args(("parentId", "{{steps.0.id}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"id": 42, "title": "Test"}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["parentId"].ShouldBe(42);
        result["parentId"].ShouldBeOfType<int>();
    }

    [Fact]
    public void Resolve_FullExpression_StringPreserved()
    {
        var args = Args(("idOrPattern", "{{steps.0.title}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"id": 42, "title": "My Task"}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["idOrPattern"].ShouldBe("My Task");
    }

    [Fact]
    public void Resolve_FullExpression_BooleanPreserved()
    {
        var args = Args(("flag", "{{steps.0.isDirty}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"isDirty": true}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["flag"].ShouldBe(true);
        result["flag"].ShouldBeOfType<bool>();
    }

    [Fact]
    public void Resolve_FullExpression_NullPreserved()
    {
        var args = Args(("value", "{{steps.0.optional}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"optional": null}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["value"].ShouldBeNull();
    }

    [Fact]
    public void Resolve_FullExpression_LargeNumber_ReturnsLong()
    {
        var args = Args(("bigId", "{{steps.0.bigId}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"bigId": 3000000000}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["bigId"].ShouldBe(3000000000L);
        result["bigId"].ShouldBeOfType<long>();
    }

    [Fact]
    public void Resolve_FullExpression_DecimalNumber_ReturnsDouble()
    {
        var args = Args(("score", "{{steps.0.score}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"score": 3.14}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["score"].ShouldBe(3.14);
        result["score"].ShouldBeOfType<double>();
    }

    // ── Nested property paths ────────────────────────────────────────────

    [Fact]
    public void Resolve_NestedPath_NavigatesJsonHierarchy()
    {
        var args = Args(("value", "{{steps.0.item.id}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"item": {"id": 99, "title": "Nested"}}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["value"].ShouldBe(99);
    }

    [Fact]
    public void Resolve_DeeplyNestedPath_NavigatesMultipleLevels()
    {
        var args = Args(("value", "{{steps.0.a.b.c}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"a": {"b": {"c": "deep"}}}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["value"].ShouldBe("deep");
    }

    // ── Partial expression (string interpolation) ────────────────────────

    [Fact]
    public void Resolve_PartialExpression_InterpolatesIntoString()
    {
        var args = Args(("text", "Created: {{steps.0.title}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"id": 42, "title": "My Task"}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["text"].ShouldBe("Created: My Task");
        result["text"].ShouldBeOfType<string>();
    }

    [Fact]
    public void Resolve_PartialExpression_MultipleExpressions()
    {
        var args = Args(("text", "ID={{steps.0.id}}, Title={{steps.0.title}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"id": 42, "title": "My Task"}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["text"].ShouldBe("ID=42, Title=My Task");
    }

    [Fact]
    public void Resolve_PartialExpression_MultipleSteps()
    {
        var args = Args(("text", "{{steps.0.title}} -> {{steps.1.title}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"title": "Step A"}"""),
            Succeeded(1, """{"title": "Step B"}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["text"].ShouldBe("Step A -> Step B");
    }

    [Fact]
    public void Resolve_PartialExpression_NumberCoercedToString()
    {
        var args = Args(("text", "ID: {{steps.0.id}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"id": 42}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["text"].ShouldBe("ID: 42");
        result["text"].ShouldBeOfType<string>();
    }

    [Fact]
    public void Resolve_PartialExpression_BoolCoercedToString()
    {
        var args = Args(("text", "dirty={{steps.0.isDirty}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"isDirty": false}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["text"].ShouldBe("dirty=false");
    }

    [Fact]
    public void Resolve_PartialExpression_NullCoercedToEmpty()
    {
        var args = Args(("text", "val={{steps.0.val}}end"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"val": null}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["text"].ShouldBe("val=end");
    }

    // ── Mixed arguments (some templated, some not) ───────────────────────

    [Fact]
    public void Resolve_MixedArguments_OnlyTemplatedOnesResolved()
    {
        var args = Args(
            ("idOrPattern", "{{steps.0.id}}"),
            ("workspace", "myOrg/myProject"),
            ("count", 5));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"id": 42}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["idOrPattern"].ShouldBe(42);
        result["workspace"].ShouldBe("myOrg/myProject");
        result["count"].ShouldBe(5);
    }

    // ── Error: step not completed ────────────────────────────────────────

    [Fact]
    public void Resolve_StepNotCompleted_ThrowsWithMessage()
    {
        var args = Args(("id", "{{steps.0.id}}"));
        var steps = new StepResult?[] { null };

        var ex = Should.Throw<TemplateResolutionException>(
            () => TemplateResolver.Resolve(args, steps));

        ex.Placeholder.ShouldBe("{{steps.0.id}}");
        ex.Message.ShouldContain("not completed yet");
    }

    [Fact]
    public void Resolve_StepFailed_ThrowsWithStatus()
    {
        var args = Args(("id", "{{steps.0.id}}"));
        var steps = new StepResult?[] { Failed(0) };

        var ex = Should.Throw<TemplateResolutionException>(
            () => TemplateResolver.Resolve(args, steps));

        ex.Placeholder.ShouldBe("{{steps.0.id}}");
        ex.Message.ShouldContain("not complete successfully");
        ex.Message.ShouldContain("Failed");
    }

    [Fact]
    public void Resolve_StepSkipped_ThrowsWithStatus()
    {
        var args = Args(("id", "{{steps.0.id}}"));
        var steps = new StepResult?[] { Skipped(0) };

        var ex = Should.Throw<TemplateResolutionException>(
            () => TemplateResolver.Resolve(args, steps));

        ex.Message.ShouldContain("Skipped");
    }

    // ── Error: step index out of range ───────────────────────────────────

    [Fact]
    public void Resolve_StepIndexOutOfRange_ThrowsWithRange()
    {
        var args = Args(("id", "{{steps.5.id}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"id": 1}""")
        };

        var ex = Should.Throw<TemplateResolutionException>(
            () => TemplateResolver.Resolve(args, steps));

        ex.Message.ShouldContain("out of range");
        ex.Message.ShouldContain("0..0");
    }

    // ── Error: no output ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_StepSucceededButNoOutput_Throws()
    {
        var args = Args(("id", "{{steps.0.id}}"));
        var steps = new StepResult?[]
        {
            new StepResult(0, "test_tool", StepStatus.Succeeded, null, null, 10)
        };

        var ex = Should.Throw<TemplateResolutionException>(
            () => TemplateResolver.Resolve(args, steps));

        ex.Message.ShouldContain("no output");
    }

    // ── Error: property not found ────────────────────────────────────────

    [Fact]
    public void Resolve_PropertyNotFound_ThrowsWithAvailableProperties()
    {
        var args = Args(("value", "{{steps.0.missing}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"id": 42, "title": "Test"}""")
        };

        var ex = Should.Throw<TemplateResolutionException>(
            () => TemplateResolver.Resolve(args, steps));

        ex.Message.ShouldContain("missing");
        ex.Message.ShouldContain("not found");
        ex.Message.ShouldContain("id");
        ex.Message.ShouldContain("title");
    }

    [Fact]
    public void Resolve_NestedPropertyNotFound_Throws()
    {
        var args = Args(("value", "{{steps.0.item.missing}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"item": {"id": 1}}""")
        };

        var ex = Should.Throw<TemplateResolutionException>(
            () => TemplateResolver.Resolve(args, steps));

        ex.Message.ShouldContain("missing");
        ex.Message.ShouldContain("not found");
    }

    // ── Error: non-object intermediate ───────────────────────────────────

    [Fact]
    public void Resolve_IntermediateIsNotObject_Throws()
    {
        var args = Args(("value", "{{steps.0.id.nested}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"id": 42}""")
        };

        var ex = Should.Throw<TemplateResolutionException>(
            () => TemplateResolver.Resolve(args, steps));

        ex.Message.ShouldContain("not an object");
        ex.Message.ShouldContain("number");
    }

    // ── Error: array/object at leaf ──────────────────────────────────────

    [Fact]
    public void Resolve_FullExpression_ObjectAtLeaf_Throws()
    {
        var args = Args(("value", "{{steps.0.item}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"item": {"id": 1, "title": "x"}}""")
        };

        var ex = Should.Throw<TemplateResolutionException>(
            () => TemplateResolver.Resolve(args, steps));

        ex.Message.ShouldContain("object");
        ex.Message.ShouldContain("scalar");
    }

    [Fact]
    public void Resolve_FullExpression_ArrayAtLeaf_Throws()
    {
        var args = Args(("value", "{{steps.0.items}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"items": [1, 2, 3]}""")
        };

        var ex = Should.Throw<TemplateResolutionException>(
            () => TemplateResolver.Resolve(args, steps));

        ex.Message.ShouldContain("array");
        ex.Message.ShouldContain("scalar");
    }

    [Fact]
    public void Resolve_PartialExpression_ObjectAtLeaf_Throws()
    {
        var args = Args(("text", "prefix-{{steps.0.item}}-suffix"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"item": {"id": 1}}""")
        };

        var ex = Should.Throw<TemplateResolutionException>(
            () => TemplateResolver.Resolve(args, steps));

        ex.Message.ShouldContain("object");
    }

    // ── ResolveTemplateString (internal, verified via Resolve) ───────────

    [Fact]
    public void Resolve_EmptyStepsArray_WithNoTemplates_Works()
    {
        var args = Args(("title", "plain text"));
        var steps = new StepResult?[0];

        var result = TemplateResolver.Resolve(args, steps);

        result["title"].ShouldBe("plain text");
    }

    [Fact]
    public void Resolve_ArgumentsAreNewDictionary_NotMutatingOriginal()
    {
        var args = Args(("id", "{{steps.0.id}}"), ("static", "hello"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"id": 42}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result.ShouldNotBeSameAs(args);
        args["id"].ShouldBe("{{steps.0.id}}"); // Original unchanged
    }

    // ── Null arguments check ─────────────────────────────────────────────

    [Fact]
    public void Resolve_NullArguments_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => TemplateResolver.Resolve(null!, new StepResult?[0]));
    }

    [Fact]
    public void Resolve_NullCompletedSteps_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => TemplateResolver.Resolve(Args(), null!));
    }

    // ── Edge case: empty arguments ───────────────────────────────────────

    [Fact]
    public void Resolve_EmptyArguments_ReturnsEmptyDictionary()
    {
        var result = TemplateResolver.Resolve(Args(), new StepResult?[0]);

        result.ShouldBeEmpty();
    }

    // ── Edge case: string with no expressions (literal braces) ───────────

    [Fact]
    public void Resolve_StringWithUnmatchedBraces_TreatedAsLiteral()
    {
        var args = Args(("text", "some {{ incomplete"));
        var steps = new StepResult?[0];

        var result = TemplateResolver.Resolve(args, steps);

        result["text"].ShouldBe("some {{ incomplete");
    }

    // ── Edge case: referencing multiple previous steps ────────────────────

    [Fact]
    public void Resolve_MultiplePriorSteps_ResolvesEach()
    {
        var args = Args(("text", "{{steps.0.id}} and {{steps.2.title}}"));
        var steps = new StepResult?[]
        {
            Succeeded(0, """{"id": 1}"""),
            Succeeded(1, """{"id": 2}"""),
            Succeeded(2, """{"title": "Third"}""")
        };

        var result = TemplateResolver.Resolve(args, steps);

        result["text"].ShouldBe("1 and Third");
    }
}
