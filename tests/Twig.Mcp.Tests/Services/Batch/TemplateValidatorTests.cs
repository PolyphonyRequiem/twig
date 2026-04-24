using Shouldly;
using Twig.Mcp.Services.Batch;
using Xunit;

namespace Twig.Mcp.Tests.Services.Batch;

public class TemplateValidatorTests
{
    // ── Helper factories ─────────────────────────────────────────────────

    private static StepNode Step(int index, params (string key, string value)[] args)
    {
        var arguments = new Dictionary<string, object?>();
        foreach (var (key, value) in args)
            arguments[key] = value;
        return new StepNode(index, "test_tool", arguments);
    }

    private static StepNode StepNoArgs(int index) =>
        new(index, "test_tool", new Dictionary<string, object?>());

    private static StepNode StepNonStringArg(int index, string key, object? value)
    {
        var arguments = new Dictionary<string, object?> { [key] = value };
        return new StepNode(index, "test_tool", arguments);
    }

    // ── No errors ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_NoTemplateExpressions_ReturnsEmpty()
    {
        var graph = new BatchGraph(
            new SequenceNode([
                Step(0, ("title", "plain text")),
                Step(1, ("value", "no templates here"))
            ]),
            TotalStepCount: 2);

        var errors = TemplateValidator.Validate(graph);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ValidBackwardReference_ReturnsEmpty()
    {
        var graph = new BatchGraph(
            new SequenceNode([
                Step(0, ("title", "Create item")),
                Step(1, ("id", "{{steps.0.id}}"))
            ]),
            TotalStepCount: 2);

        var errors = TemplateValidator.Validate(graph);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ValidChainedBackwardReferences_ReturnsEmpty()
    {
        var graph = new BatchGraph(
            new SequenceNode([
                Step(0, ("title", "Create")),
                Step(1, ("id", "{{steps.0.id}}")),
                Step(2, ("id", "{{steps.1.output}}")),
                Step(3, ("ref", "{{steps.0.id}}-{{steps.2.name}}"))
            ]),
            TotalStepCount: 4);

        var errors = TemplateValidator.Validate(graph);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_StepWithNoArguments_ReturnsEmpty()
    {
        var graph = new BatchGraph(
            new SequenceNode([StepNoArgs(0), StepNoArgs(1)]),
            TotalStepCount: 2);

        var errors = TemplateValidator.Validate(graph);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_NonStringArguments_SkippedNoErrors()
    {
        var graph = new BatchGraph(
            new SequenceNode([
                StepNoArgs(0),
                StepNonStringArg(1, "count", 42),
                StepNonStringArg(2, "flag", true),
                StepNonStringArg(3, "nothing", null)
            ]),
            TotalStepCount: 4);

        var errors = TemplateValidator.Validate(graph);

        errors.ShouldBeEmpty();
    }

    // ── Forward reference errors ─────────────────────────────────────────

    [Fact]
    public void Validate_ForwardReference_ReturnsError()
    {
        var graph = new BatchGraph(
            new SequenceNode([
                Step(0, ("ref", "{{steps.1.id}}")),
                StepNoArgs(1)
            ]),
            TotalStepCount: 2);

        var errors = TemplateValidator.Validate(graph);

        errors.Count.ShouldBe(1);
        errors[0].StepIndex.ShouldBe(0);
        errors[0].Expression.StepIndex.ShouldBe(1);
        errors[0].Expression.FullPlaceholder.ShouldBe("{{steps.1.id}}");
        errors[0].Reason.ShouldContain("Forward reference");
        errors[0].Reason.ShouldContain("step 0");
        errors[0].Reason.ShouldContain("step 1");
    }

    [Fact]
    public void Validate_SelfReference_ReturnsForwardReferenceError()
    {
        var graph = new BatchGraph(
            new SequenceNode([
                Step(0, ("ref", "{{steps.0.id}}"))
            ]),
            TotalStepCount: 1);

        var errors = TemplateValidator.Validate(graph);

        errors.Count.ShouldBe(1);
        errors[0].StepIndex.ShouldBe(0);
        errors[0].Expression.StepIndex.ShouldBe(0);
        errors[0].Reason.ShouldContain("Forward reference");
    }

    [Fact]
    public void Validate_ForwardReferenceSkipsMultipleSteps_ReturnsError()
    {
        var graph = new BatchGraph(
            new SequenceNode([
                Step(0, ("ref", "{{steps.3.output}}")),
                StepNoArgs(1),
                StepNoArgs(2),
                StepNoArgs(3)
            ]),
            TotalStepCount: 4);

        var errors = TemplateValidator.Validate(graph);

        errors.Count.ShouldBe(1);
        errors[0].StepIndex.ShouldBe(0);
        errors[0].Expression.StepIndex.ShouldBe(3);
    }

    [Fact]
    public void Validate_MultipleForwardRefsInSameStep_ReturnsMultipleErrors()
    {
        var graph = new BatchGraph(
            new SequenceNode([
                Step(0, ("a", "{{steps.1.x}}"), ("b", "{{steps.2.y}}")),
                StepNoArgs(1),
                StepNoArgs(2)
            ]),
            TotalStepCount: 3);

        var errors = TemplateValidator.Validate(graph);

        errors.Count.ShouldBe(2);
        errors.ShouldAllBe(e => e.StepIndex == 0);
        errors.ShouldAllBe(e => e.Reason.Contains("Forward reference"));
    }

    // ── Parallel sibling reference errors ────────────────────────────────

    [Fact]
    public void Validate_ParallelSiblingReference_ReturnsError()
    {
        // step 1 references step 0, both in same parallel group
        var graph = new BatchGraph(
            new ParallelNode([
                Step(0, ("title", "A")),
                Step(1, ("ref", "{{steps.0.id}}"))
            ]),
            TotalStepCount: 2);

        var errors = TemplateValidator.Validate(graph);

        errors.Count.ShouldBe(1);
        errors[0].StepIndex.ShouldBe(1);
        errors[0].Expression.StepIndex.ShouldBe(0);
        errors[0].Reason.ShouldContain("Parallel sibling reference");
        errors[0].Reason.ShouldContain("same parallel group");
    }

    [Fact]
    public void Validate_ParallelSiblingForwardRef_ReportsForwardReference()
    {
        // step 0 references step 1 — both forward ref AND parallel sibling;
        // forward ref takes precedence in reporting.
        var graph = new BatchGraph(
            new ParallelNode([
                Step(0, ("ref", "{{steps.1.id}}")),
                StepNoArgs(1)
            ]),
            TotalStepCount: 2);

        var errors = TemplateValidator.Validate(graph);

        errors.Count.ShouldBe(1);
        errors[0].Reason.ShouldContain("Forward reference");
    }

    [Fact]
    public void Validate_NestedParallelSiblingReference_ReturnsError()
    {
        // sequence { step 0, parallel { sequence { step 1, step 2 }, step 3 } }
        // step 2 references step 3 — parallel siblings across branches
        var graph = new BatchGraph(
            new SequenceNode([
                StepNoArgs(0),
                new ParallelNode([
                    new SequenceNode([
                        StepNoArgs(1),
                        Step(2, ("ref", "{{steps.3.id}}"))
                    ]),
                    StepNoArgs(3)
                ])
            ]),
            TotalStepCount: 4);

        var errors = TemplateValidator.Validate(graph);

        // step 2 → step 3 is a forward ref (3 >= 2)
        errors.Count.ShouldBe(1);
        errors[0].StepIndex.ShouldBe(2);
        errors[0].Expression.StepIndex.ShouldBe(3);
    }

    [Fact]
    public void Validate_ParallelSibling_BackwardRefAcrossBranches_ReturnsError()
    {
        // parallel { sequence { step 0, step 1 }, sequence { step 2 references step 1 } }
        // step 2 references step 1 — they're in different branches of the same parallel node
        var graph = new BatchGraph(
            new ParallelNode([
                new SequenceNode([StepNoArgs(0), StepNoArgs(1)]),
                new SequenceNode([Step(2, ("ref", "{{steps.1.output}}"))])
            ]),
            TotalStepCount: 3);

        var errors = TemplateValidator.Validate(graph);

        errors.Count.ShouldBe(1);
        errors[0].StepIndex.ShouldBe(2);
        errors[0].Expression.StepIndex.ShouldBe(1);
        errors[0].Reason.ShouldContain("Parallel sibling reference");
    }

    [Fact]
    public void Validate_ParallelFollowedByStep_BackwardRefIsValid()
    {
        // sequence { parallel { step 0, step 1 }, step 2 references step 0 }
        // step 2 is after the parallel group — referencing step 0 is valid
        var graph = new BatchGraph(
            new SequenceNode([
                new ParallelNode([StepNoArgs(0), StepNoArgs(1)]),
                Step(2, ("ref", "{{steps.0.id}}"))
            ]),
            TotalStepCount: 3);

        var errors = TemplateValidator.Validate(graph);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ParallelStepRefsBeforeParallelGroup_IsValid()
    {
        // sequence { step 0, parallel { step 1 refs step 0, step 2 refs step 0 } }
        // Both parallel steps reference step 0 which completed before the group
        var graph = new BatchGraph(
            new SequenceNode([
                StepNoArgs(0),
                new ParallelNode([
                    Step(1, ("ref", "{{steps.0.id}}")),
                    Step(2, ("ref", "{{steps.0.name}}"))
                ])
            ]),
            TotalStepCount: 3);

        var errors = TemplateValidator.Validate(graph);

        errors.ShouldBeEmpty();
    }

    // ── Mixed valid and invalid ──────────────────────────────────────────

    [Fact]
    public void Validate_MixedValidAndInvalidReferences_ReturnsOnlyInvalidErrors()
    {
        var graph = new BatchGraph(
            new SequenceNode([
                StepNoArgs(0),
                Step(1, ("valid", "{{steps.0.id}}"), ("invalid", "{{steps.2.id}}")),
                StepNoArgs(2)
            ]),
            TotalStepCount: 3);

        var errors = TemplateValidator.Validate(graph);

        errors.Count.ShouldBe(1);
        errors[0].StepIndex.ShouldBe(1);
        errors[0].Expression.StepIndex.ShouldBe(2);
        errors[0].Reason.ShouldContain("Forward reference");
    }

    // ── Single step graph ────────────────────────────────────────────────

    [Fact]
    public void Validate_SingleStepNoTemplates_ReturnsEmpty()
    {
        var graph = new BatchGraph(
            Step(0, ("title", "hello")),
            TotalStepCount: 1);

        var errors = TemplateValidator.Validate(graph);

        errors.ShouldBeEmpty();
    }

    // ── Null input ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_NullGraph_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => TemplateValidator.Validate(null!));
    }

    // ── Three-branch parallel group ──────────────────────────────────────

    [Fact]
    public void Validate_ThreeBranchParallel_CrossBranchRefDetected()
    {
        // parallel { step 0, step 1, step 2 refs step 0 }
        var graph = new BatchGraph(
            new ParallelNode([
                StepNoArgs(0),
                StepNoArgs(1),
                Step(2, ("ref", "{{steps.0.id}}"))
            ]),
            TotalStepCount: 3);

        var errors = TemplateValidator.Validate(graph);

        errors.Count.ShouldBe(1);
        errors[0].StepIndex.ShouldBe(2);
        errors[0].Expression.StepIndex.ShouldBe(0);
        errors[0].Reason.ShouldContain("Parallel sibling reference");
    }

    // ── Nested parallel within sequence within parallel ──────────────────

    [Fact]
    public void Validate_NestedParallelGroups_InnerGroupRefsDetected()
    {
        // sequence {
        //   step 0,
        //   parallel {
        //     sequence {
        //       step 1,
        //       parallel { step 2, step 3 refs step 2 }  ← inner parallel sibling ref
        //     },
        //     step 4
        //   }
        // }
        var graph = new BatchGraph(
            new SequenceNode([
                StepNoArgs(0),
                new ParallelNode([
                    new SequenceNode([
                        StepNoArgs(1),
                        new ParallelNode([
                            StepNoArgs(2),
                            Step(3, ("ref", "{{steps.2.id}}"))
                        ])
                    ]),
                    StepNoArgs(4)
                ])
            ]),
            TotalStepCount: 5);

        var errors = TemplateValidator.Validate(graph);

        errors.Count.ShouldBe(1);
        errors[0].StepIndex.ShouldBe(3);
        errors[0].Expression.StepIndex.ShouldBe(2);
        errors[0].Reason.ShouldContain("Parallel sibling reference");
    }

    // ── Error message contains full placeholder ──────────────────────────

    [Fact]
    public void Validate_ErrorMessage_ContainsFullPlaceholder()
    {
        var graph = new BatchGraph(
            new SequenceNode([
                Step(0, ("ref", "{{steps.1.item.nested.id}}")),
                StepNoArgs(1)
            ]),
            TotalStepCount: 2);

        var errors = TemplateValidator.Validate(graph);

        errors.Count.ShouldBe(1);
        errors[0].Reason.ShouldContain("{{steps.1.item.nested.id}}");
    }

    // ── Multiple expressions in single string value ──────────────────────

    [Fact]
    public void Validate_MultipleExpressionsInOneValue_ValidatesEach()
    {
        // "{{steps.0.id}}-{{steps.2.name}}" — first is valid, second is forward ref
        var graph = new BatchGraph(
            new SequenceNode([
                StepNoArgs(0),
                Step(1, ("combo", "{{steps.0.id}}-{{steps.2.name}}")),
                StepNoArgs(2)
            ]),
            TotalStepCount: 3);

        var errors = TemplateValidator.Validate(graph);

        errors.Count.ShouldBe(1);
        errors[0].Expression.FullPlaceholder.ShouldBe("{{steps.2.name}}");
    }

    // ── Parallel sibling within sequence context ─────────────────────────

    [Fact]
    public void Validate_SequenceInsideParallel_WithinBranchRefIsValid()
    {
        // parallel { sequence { step 0, step 1 refs step 0 }, step 2 }
        // step 1 → step 0 is valid because they're in the same sequence within one branch
        var graph = new BatchGraph(
            new ParallelNode([
                new SequenceNode([
                    StepNoArgs(0),
                    Step(1, ("ref", "{{steps.0.id}}"))
                ]),
                StepNoArgs(2)
            ]),
            TotalStepCount: 3);

        var errors = TemplateValidator.Validate(graph);

        errors.ShouldBeEmpty();
    }
}
