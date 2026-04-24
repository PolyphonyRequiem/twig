using Shouldly;
using Twig.Mcp.Services.Batch;
using Xunit;

namespace Twig.Mcp.Tests.Services.Batch;

public sealed class BatchModelsTests
{
    // ── StepNode ────────────────────────────────────────────────────

    [Fact]
    public void StepNode_StoresProperties()
    {
        var args = new Dictionary<string, object?> { ["title"] = "test", ["id"] = 42 };
        var step = new StepNode(0, "twig_new", args);

        step.GlobalIndex.ShouldBe(0);
        step.ToolName.ShouldBe("twig_new");
        step.Arguments.ShouldBeSameAs(args);
    }

    [Fact]
    public void StepNode_EmptyArguments()
    {
        var step = new StepNode(5, "twig_status", new Dictionary<string, object?>());

        step.Arguments.ShouldBeEmpty();
        step.GlobalIndex.ShouldBe(5);
    }

    [Fact]
    public void StepNode_ArgumentsWithNullValue()
    {
        var args = new Dictionary<string, object?> { ["workspace"] = null };
        var step = new StepNode(0, "twig_set", args);

        step.Arguments["workspace"].ShouldBeNull();
    }

    // ── SequenceNode ────────────────────────────────────────────────

    [Fact]
    public void SequenceNode_StoresChildren()
    {
        var children = new BatchNode[]
        {
            new StepNode(0, "twig_new", new Dictionary<string, object?>()),
            new StepNode(1, "twig_set", new Dictionary<string, object?>())
        };

        var seq = new SequenceNode(children);
        seq.Children.Count.ShouldBe(2);
        seq.Children[0].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(0);
        seq.Children[1].ShouldBeOfType<StepNode>().GlobalIndex.ShouldBe(1);
    }

    [Fact]
    public void SequenceNode_EmptyChildren()
    {
        var seq = new SequenceNode([]);
        seq.Children.ShouldBeEmpty();
    }

    [Fact]
    public void SequenceNode_NestedParallel()
    {
        var parallel = new ParallelNode(
        [
            new StepNode(0, "twig_note", new Dictionary<string, object?>()),
            new StepNode(1, "twig_update", new Dictionary<string, object?>())
        ]);

        var seq = new SequenceNode([parallel]);
        seq.Children.Count.ShouldBe(1);
        seq.Children[0].ShouldBeOfType<ParallelNode>().Children.Count.ShouldBe(2);
    }

    // ── ParallelNode ────────────────────────────────────────────────

    [Fact]
    public void ParallelNode_StoresChildren()
    {
        var children = new BatchNode[]
        {
            new StepNode(0, "twig_update", new Dictionary<string, object?>()),
            new StepNode(1, "twig_note", new Dictionary<string, object?>())
        };

        var par = new ParallelNode(children);
        par.Children.Count.ShouldBe(2);
    }

    // ── BatchGraph ──────────────────────────────────────────────────

    [Fact]
    public void BatchGraph_StoresRootAndMetrics()
    {
        var root = new SequenceNode(
        [
            new StepNode(0, "twig_new", new Dictionary<string, object?>()),
            new StepNode(1, "twig_set", new Dictionary<string, object?>())
        ]);

        var graph = new BatchGraph(root, TotalStepCount: 2);

        graph.Root.ShouldBeSameAs(root);
        graph.TotalStepCount.ShouldBe(2);
    }

    [Fact]
    public void BatchGraph_SingleStep()
    {
        var step = new StepNode(0, "twig_status", new Dictionary<string, object?>());
        var graph = new BatchGraph(step, TotalStepCount: 1);

        graph.Root.ShouldBeOfType<StepNode>();
        graph.TotalStepCount.ShouldBe(1);
    }

    // ── StepStatus ──────────────────────────────────────────────────

    [Fact]
    public void StepStatus_HasExactlyThreeMembers()
    {
        Enum.GetValues<StepStatus>().Length.ShouldBe(3);
    }

    // ── StepResult ──────────────────────────────────────────────────

    [Fact]
    public void StepResult_SuccessCase()
    {
        var result = new StepResult(
            StepIndex: 0,
            ToolName: "twig_new",
            Status: StepStatus.Succeeded,
            OutputJson: """{"id":42}""",
            Error: null,
            ElapsedMs: 150);

        result.StepIndex.ShouldBe(0);
        result.ToolName.ShouldBe("twig_new");
        result.Status.ShouldBe(StepStatus.Succeeded);
        result.OutputJson.ShouldBe("""{"id":42}""");
        result.Error.ShouldBeNull();
        result.ElapsedMs.ShouldBe(150);
    }

    [Fact]
    public void StepResult_FailureCase()
    {
        var result = new StepResult(
            StepIndex: 3,
            ToolName: "twig_state",
            Status: StepStatus.Failed,
            OutputJson: null,
            Error: "Invalid state transition",
            ElapsedMs: 50);

        result.Status.ShouldBe(StepStatus.Failed);
        result.OutputJson.ShouldBeNull();
        result.Error.ShouldBe("Invalid state transition");
    }

    [Fact]
    public void StepResult_SkippedCase()
    {
        var result = new StepResult(
            StepIndex: 2,
            ToolName: "twig_update",
            Status: StepStatus.Skipped,
            OutputJson: null,
            Error: null,
            ElapsedMs: 0);

        result.Status.ShouldBe(StepStatus.Skipped);
        result.OutputJson.ShouldBeNull();
        result.Error.ShouldBeNull();
        result.ElapsedMs.ShouldBe(0);
    }

    // ── BatchResult ─────────────────────────────────────────────────

    [Fact]
    public void BatchResult_AggregatesSteps()
    {
        var steps = new[]
        {
            new StepResult(0, "twig_new", StepStatus.Succeeded, """{"id":1}""", null, 100),
            new StepResult(1, "twig_set", StepStatus.Succeeded, """{"id":1}""", null, 50),
            new StepResult(2, "twig_update", StepStatus.Skipped, null, null, 0)
        };

        var batch = new BatchResult(steps, TotalElapsedMs: 200, TimedOut: false);

        batch.Steps.Count.ShouldBe(3);
        batch.TotalElapsedMs.ShouldBe(200);
        batch.TimedOut.ShouldBeFalse();
    }

    [Fact]
    public void BatchResult_TimedOut()
    {
        var steps = new[]
        {
            new StepResult(0, "twig_new", StepStatus.Succeeded, """{"id":1}""", null, 100),
            new StepResult(1, "twig_set", StepStatus.Skipped, null, null, 0)
        };

        var batch = new BatchResult(steps, TotalElapsedMs: 120_000, TimedOut: true);

        batch.TimedOut.ShouldBeTrue();
        batch.TotalElapsedMs.ShouldBe(120_000);
    }

    [Fact]
    public void BatchResult_EmptySteps()
    {
        var batch = new BatchResult([], TotalElapsedMs: 0, TimedOut: false);

        batch.Steps.ShouldBeEmpty();
        batch.TotalElapsedMs.ShouldBe(0);
        batch.TimedOut.ShouldBeFalse();
    }

    // ── Deep Nesting ────────────────────────────────────────────────

    [Fact]
    public void DeepNesting_SequenceInParallelInSequence()
    {
        var innerStep = new StepNode(0, "twig_note", new Dictionary<string, object?>());
        var innerSeq = new SequenceNode([innerStep]);
        var parallel = new ParallelNode([innerSeq]);
        var outerSeq = new SequenceNode([parallel]);

        var graph = new BatchGraph(outerSeq, TotalStepCount: 1);

        // Navigate the nesting
        var outSeq = graph.Root.ShouldBeOfType<SequenceNode>();
        var par = outSeq.Children[0].ShouldBeOfType<ParallelNode>();
        var inSeq = par.Children[0].ShouldBeOfType<SequenceNode>();
        var step = inSeq.Children[0].ShouldBeOfType<StepNode>();
        step.ToolName.ShouldBe("twig_note");
    }
}
