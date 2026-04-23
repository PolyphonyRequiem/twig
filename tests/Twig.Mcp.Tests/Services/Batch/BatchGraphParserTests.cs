using Shouldly;
using Twig.Mcp.Services.Batch;
using Xunit;

namespace Twig.Mcp.Tests.Services.Batch;

public sealed class BatchGraphParserTests
{
    // ── Happy-path parsing ──────────────────────────────────────────

    [Fact]
    public void Parse_SingleStep_ReturnsGraphWithOneStep()
    {
        var json = """
        {
            "type": "step",
            "tool": "twig_status",
            "args": {}
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalStepCount.ShouldBe(1);
        result.Value.MaxDepth.ShouldBe(0);

        var step = result.Value.Root.ShouldBeOfType<StepNode>();
        step.GlobalIndex.ShouldBe(0);
        step.ToolName.ShouldBe("twig_status");
        step.Arguments.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_StepWithArguments_PreservesArgumentTypes()
    {
        var json = """
        {
            "type": "step",
            "tool": "twig_new",
            "args": {
                "type": "Task",
                "title": "My Task",
                "parentId": 42,
                "skipDuplicateCheck": true
            }
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        var step = result.Value.Root.ShouldBeOfType<StepNode>();
        step.Arguments["type"].ShouldBe("Task");
        step.Arguments["title"].ShouldBe("My Task");
        step.Arguments["parentId"].ShouldBe(42);
        step.Arguments["skipDuplicateCheck"].ShouldBe(true);
    }

    [Fact]
    public void Parse_StepWithNullArgValue_PreservesNull()
    {
        var json = """
        {
            "type": "step",
            "tool": "twig_set",
            "args": { "workspace": null }
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        var step = result.Value.Root.ShouldBeOfType<StepNode>();
        step.Arguments["workspace"].ShouldBeNull();
    }

    [Fact]
    public void Parse_StepWithoutArgs_DefaultsToEmptyDictionary()
    {
        var json = """
        {
            "type": "step",
            "tool": "twig_status"
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        var step = result.Value.Root.ShouldBeOfType<StepNode>();
        step.Arguments.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_Sequence_ParsesChildrenInOrder()
    {
        var json = """
        {
            "type": "sequence",
            "steps": [
                { "type": "step", "tool": "twig_new", "args": { "title": "A" } },
                { "type": "step", "tool": "twig_set", "args": { "idOrPattern": "1" } },
                { "type": "step", "tool": "twig_state", "args": { "stateName": "Doing" } }
            ]
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalStepCount.ShouldBe(3);
        result.Value.MaxDepth.ShouldBe(1);

        var seq = result.Value.Root.ShouldBeOfType<SequenceNode>();
        seq.Children.Count.ShouldBe(3);

        seq.Children[0].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(0);
        seq.Children[1].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(1);
        seq.Children[2].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(2);
    }

    [Fact]
    public void Parse_Parallel_ParsesAllChildren()
    {
        var json = """
        {
            "type": "parallel",
            "steps": [
                { "type": "step", "tool": "twig_update", "args": { "field": "System.Title", "value": "X" } },
                { "type": "step", "tool": "twig_note", "args": { "text": "note" } }
            ]
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalStepCount.ShouldBe(2);
        result.Value.MaxDepth.ShouldBe(1);

        var par = result.Value.Root.ShouldBeOfType<ParallelNode>();
        par.Children.Count.ShouldBe(2);

        par.Children[0].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(0);
        par.Children[1].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(1);
    }

    [Fact]
    public void Parse_NestedSequenceInParallel_AssignsGlobalIndicesDepthFirst()
    {
        var json = """
        {
            "type": "sequence",
            "steps": [
                { "type": "step", "tool": "twig_new", "args": {} },
                {
                    "type": "parallel",
                    "steps": [
                        { "type": "step", "tool": "twig_update", "args": {} },
                        { "type": "step", "tool": "twig_note", "args": {} }
                    ]
                },
                { "type": "step", "tool": "twig_state", "args": {} }
            ]
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalStepCount.ShouldBe(4);
        result.Value.MaxDepth.ShouldBe(2);

        var seq = result.Value.Root.ShouldBeOfType<SequenceNode>();
        seq.Children[0].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(0);

        var par = seq.Children[1].ShouldBeOfType<ParallelNode>();
        par.Children[0].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(1);
        par.Children[1].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(2);

        seq.Children[2].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(3);
    }

    [Fact]
    public void Parse_EmptySequence_ReturnsGraphWithNoSteps()
    {
        var json = """
        {
            "type": "sequence",
            "steps": []
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalStepCount.ShouldBe(0);
        var seq = result.Value.Root.ShouldBeOfType<SequenceNode>();
        seq.Children.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_EmptyParallel_ReturnsGraphWithNoSteps()
    {
        var json = """
        {
            "type": "parallel",
            "steps": []
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalStepCount.ShouldBe(0);
        var par = result.Value.Root.ShouldBeOfType<ParallelNode>();
        par.Children.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_ThreeLevelNesting_Succeeds()
    {
        // sequence > parallel > sequence > step = depth 3 for the step
        var json = """
        {
            "type": "sequence",
            "steps": [
                {
                    "type": "parallel",
                    "steps": [
                        {
                            "type": "sequence",
                            "steps": [
                                { "type": "step", "tool": "twig_note", "args": {} }
                            ]
                        }
                    ]
                }
            ]
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalStepCount.ShouldBe(1);
        result.Value.MaxDepth.ShouldBe(3);
    }

    // ── Argument type preservation ──────────────────────────────────

    [Fact]
    public void Parse_StepWithIntegerArg_PreservesIntType()
    {
        var json = """
        {
            "type": "step",
            "tool": "twig_show",
            "args": { "id": 123 }
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        var step = result.Value.Root.ShouldBeOfType<StepNode>();
        step.Arguments["id"].ShouldBeOfType<int>().ShouldBe(123);
    }

    [Fact]
    public void Parse_StepWithBooleanArgs_PreservesBoolType()
    {
        var json = """
        {
            "type": "step",
            "tool": "twig_new",
            "args": { "skipDuplicateCheck": false }
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        var step = result.Value.Root.ShouldBeOfType<StepNode>();
        step.Arguments["skipDuplicateCheck"].ShouldBeOfType<bool>().ShouldBeFalse();
    }

    // ── Validation: Empty/invalid JSON ──────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_EmptyOrWhitespace_ReturnsFail(string? json)
    {
        var result = BatchGraphParser.Parse(json!);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty");
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsFail()
    {
        var result = BatchGraphParser.Parse("{not valid json}");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Invalid JSON");
    }

    [Fact]
    public void Parse_JsonArray_ReturnsFail()
    {
        var result = BatchGraphParser.Parse("[1, 2, 3]");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("JSON object");
    }

    [Fact]
    public void Parse_JsonString_ReturnsFail()
    {
        var result = BatchGraphParser.Parse("\"hello\"");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("JSON object");
    }

    // ── Validation: Missing required properties ─────────────────────

    [Fact]
    public void Parse_MissingTypeProperty_ReturnsFail()
    {
        var json = """
        {
            "tool": "twig_status"
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("type");
    }

    [Fact]
    public void Parse_TypePropertyNotString_ReturnsFail()
    {
        var json = """
        {
            "type": 42
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("type");
    }

    [Fact]
    public void Parse_UnknownNodeType_ReturnsFail()
    {
        var json = """
        {
            "type": "unknown"
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Unknown node type");
        result.Error.ShouldContain("unknown");
    }

    [Fact]
    public void Parse_StepMissingTool_ReturnsFail()
    {
        var json = """
        {
            "type": "step",
            "args": {}
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("tool");
    }

    [Fact]
    public void Parse_StepEmptyToolName_ReturnsFail()
    {
        var json = """
        {
            "type": "step",
            "tool": "  ",
            "args": {}
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("empty");
    }

    [Fact]
    public void Parse_StepToolNotString_ReturnsFail()
    {
        var json = """
        {
            "type": "step",
            "tool": 123
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("tool");
    }

    [Fact]
    public void Parse_SequenceMissingSteps_ReturnsFail()
    {
        var json = """
        {
            "type": "sequence"
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("steps");
    }

    [Fact]
    public void Parse_ParallelMissingSteps_ReturnsFail()
    {
        var json = """
        {
            "type": "parallel"
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("steps");
    }

    [Fact]
    public void Parse_SequenceStepsNotArray_ReturnsFail()
    {
        var json = """
        {
            "type": "sequence",
            "steps": "not-an-array"
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("steps");
    }

    [Fact]
    public void Parse_StepArgsNotObject_ReturnsFail()
    {
        var json = """
        {
            "type": "step",
            "tool": "twig_status",
            "args": "not-an-object"
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("args");
        result.Error.ShouldContain("not a JSON object");
    }

    // ── Validation: Depth limit ─────────────────────────────────────

    [Fact]
    public void Parse_ExceedsMaxDepth_ReturnsFail()
    {
        // depth 0: sequence, depth 1: parallel, depth 2: sequence, depth 3: parallel, depth 4: step
        var json = """
        {
            "type": "sequence",
            "steps": [
                {
                    "type": "parallel",
                    "steps": [
                        {
                            "type": "sequence",
                            "steps": [
                                {
                                    "type": "parallel",
                                    "steps": [
                                        { "type": "step", "tool": "twig_note", "args": {} }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("nesting depth");
        result.Error.ShouldContain("3");
    }

    // ── Validation: Operation count limit ───────────────────────────

    [Fact]
    public void Parse_ExactlyMaxOperations_Succeeds()
    {
        var steps = string.Join(",\n",
            Enumerable.Range(0, BatchConstants.MaxOperations)
                .Select(i => "{ \"type\": \"step\", \"tool\": \"twig_note\", \"args\": { \"text\": \"step" + i + "\" } }"));

        var json = "{ \"type\": \"sequence\", \"steps\": [" + steps + "] }";

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalStepCount.ShouldBe(BatchConstants.MaxOperations);
    }

    [Fact]
    public void Parse_ExceedsMaxOperations_ReturnsFail()
    {
        var steps = string.Join(",\n",
            Enumerable.Range(0, BatchConstants.MaxOperations + 1)
                .Select(i => "{ \"type\": \"step\", \"tool\": \"twig_note\", \"args\": { \"text\": \"step" + i + "\" } }"));

        var json = "{ \"type\": \"sequence\", \"steps\": [" + steps + "] }";

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("50");
    }

    // ── Validation: Recursive batch ban ─────────────────────────────

    [Fact]
    public void Parse_RecursiveBatchCall_ReturnsFail()
    {
        var json = """
        {
            "type": "step",
            "tool": "twig_batch",
            "args": { "graph": "{}" }
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Recursive batch");
        result.Error.ShouldContain("twig_batch");
    }

    [Fact]
    public void Parse_RecursiveBatchCall_CaseInsensitive_ReturnsFail()
    {
        var json = """
        {
            "type": "step",
            "tool": "TWIG_BATCH",
            "args": {}
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Recursive batch");
    }

    [Fact]
    public void Parse_RecursiveBatchNestedInSequence_ReturnsFail()
    {
        var json = """
        {
            "type": "sequence",
            "steps": [
                { "type": "step", "tool": "twig_note", "args": {} },
                { "type": "step", "tool": "twig_batch", "args": {} }
            ]
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Recursive batch");
    }

    // ── Validation: Child node in container is not an object ────────

    [Fact]
    public void Parse_SequenceChildNotObject_ReturnsFail()
    {
        var json = """
        {
            "type": "sequence",
            "steps": [ "not-an-object" ]
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("JSON object");
    }

    // ── Complex real-world graph ────────────────────────────────────

    [Fact]
    public void Parse_RealWorldGraph_CreatesCorrectStructure()
    {
        var json = """
        {
            "type": "sequence",
            "steps": [
                {
                    "type": "step",
                    "tool": "twig_new",
                    "args": { "type": "Task", "title": "My Task", "parentId": 42 }
                },
                {
                    "type": "step",
                    "tool": "twig_set",
                    "args": { "idOrPattern": "{{steps.0.id}}" }
                },
                {
                    "type": "parallel",
                    "steps": [
                        {
                            "type": "step",
                            "tool": "twig_update",
                            "args": { "field": "System.Description", "value": "desc", "format": "markdown" }
                        },
                        {
                            "type": "step",
                            "tool": "twig_note",
                            "args": { "text": "Created via batch" }
                        }
                    ]
                }
            ]
        }
        """;

        var result = BatchGraphParser.Parse(json);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TotalStepCount.ShouldBe(4);
        result.Value.MaxDepth.ShouldBe(2);

        var seq = result.Value.Root.ShouldBeOfType<SequenceNode>();
        seq.Children.Count.ShouldBe(3);

        // Step 0: twig_new
        var step0 = seq.Children[0].ShouldBeOfType<StepNode>();
        step0.GlobalIndex.ShouldBe(0);
        step0.ToolName.ShouldBe("twig_new");
        step0.Arguments["parentId"].ShouldBe(42);

        // Step 1: twig_set with template placeholder (treated as literal string here)
        var step1 = seq.Children[1].ShouldBeOfType<StepNode>();
        step1.GlobalIndex.ShouldBe(1);
        step1.ToolName.ShouldBe("twig_set");
        step1.Arguments["idOrPattern"].ShouldBe("{{steps.0.id}}");

        // Parallel block with steps 2 and 3
        var par = seq.Children[2].ShouldBeOfType<ParallelNode>();
        par.Children[0].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(2);
        par.Children[1].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(3);
    }

    // ── Constants verification ──────────────────────────────────────

    [Fact]
    public void BatchConstants_HasExpectedValues()
    {
        BatchConstants.MaxDepth.ShouldBe(3);
        BatchConstants.MaxOperations.ShouldBe(50);
        BatchConstants.DefaultTimeoutSeconds.ShouldBe(120);
        BatchConstants.BatchToolName.ShouldBe("twig_batch");
    }
}
