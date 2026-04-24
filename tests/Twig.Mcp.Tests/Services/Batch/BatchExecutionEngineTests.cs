using ModelContextProtocol.Protocol;
using Shouldly;
using Twig.Mcp.Services.Batch;
using Xunit;
using static Twig.Mcp.Tests.Services.Batch.BatchTestHelpers;

namespace Twig.Mcp.Tests.Services.Batch;

public sealed class BatchExecutionEngineTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // ── Test infrastructure ─────────────────────────────────────────
    // Shared: TestToolDispatcher, CreateDispatcher, CreateDelayedDispatcher,
    //         CreateThrowingDispatcher, SuccessResult, ErrorResult → BatchTestHelpers.cs

    // ── Single step execution ───────────────────────────────────────

    [Fact]
    public async Task Execute_SingleStep_ReturnsSuccessResult()
    {
        var dispatcher = CreateDispatcher();
        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new StepNode(0, "twig_status", new Dictionary<string, object?>()),
            TotalStepCount: 1);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(1);
        result.Steps[0].StepIndex.ShouldBe(0);
        result.Steps[0].ToolName.ShouldBe("twig_status");
        result.Steps[0].Status.ShouldBe(StepStatus.Succeeded);
        result.Steps[0].OutputJson.ShouldNotBeNull();
        result.Steps[0].Error.ShouldBeNull();
        result.Steps[0].ElapsedMs.ShouldBeGreaterThanOrEqualTo(0);
        result.TimedOut.ShouldBeFalse();
    }

    [Fact]
    public async Task Execute_SingleStep_FailedToolResult_RecordsFailure()
    {
        var dispatcher = CreateDispatcher((tool, _) => ErrorResult("Something went wrong"));
        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new StepNode(0, "twig_set", new Dictionary<string, object?> { ["idOrPattern"] = "999" }),
            TotalStepCount: 1);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(1);
        result.Steps[0].Status.ShouldBe(StepStatus.Failed);
        result.Steps[0].Error.ShouldBe("Something went wrong");
    }

    [Fact]
    public async Task Execute_SingleStep_DispatcherThrows_RecordsFailure()
    {
        var dispatcher = CreateThrowingDispatcher(new InvalidOperationException("Dispatcher crashed"));

        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new StepNode(0, "twig_status", new Dictionary<string, object?>()),
            TotalStepCount: 1);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(1);
        result.Steps[0].Status.ShouldBe(StepStatus.Failed);
        result.Steps[0].Error.ShouldBe("Dispatcher crashed");
    }

    // ── Sequence execution ──────────────────────────────────────────

    [Fact]
    public async Task Execute_Sequence_ExecutesInOrder()
    {
        var order = new List<string>();
        var dispatcher = CreateDispatcher((tool, _) =>
        {
            order.Add(tool);
            return SuccessResult($"{{\"tool\":\"{tool}\"}}");
        });

        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new SequenceNode([
                new StepNode(0, "twig_set", new Dictionary<string, object?> { ["idOrPattern"] = "1" }),
                new StepNode(1, "twig_state", new Dictionary<string, object?> { ["stateName"] = "Doing" }),
                new StepNode(2, "twig_note", new Dictionary<string, object?> { ["text"] = "hello" })
            ]),
            TotalStepCount: 3);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(3);
        result.Steps.ShouldAllBe(s => s.Status == StepStatus.Succeeded);
        order.ShouldBe(["twig_set", "twig_state", "twig_note"]);
    }

    [Fact]
    public async Task Execute_Sequence_FailFast_SkipsRemaining()
    {
        var dispatcher = CreateDispatcher((tool, _) =>
        {
            if (tool == "twig_state")
                return ErrorResult("State change failed");
            return SuccessResult($"{{\"tool\":\"{tool}\"}}");
        });

        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new SequenceNode([
                new StepNode(0, "twig_set", new Dictionary<string, object?> { ["idOrPattern"] = "1" }),
                new StepNode(1, "twig_state", new Dictionary<string, object?> { ["stateName"] = "Doing" }),
                new StepNode(2, "twig_note", new Dictionary<string, object?> { ["text"] = "hello" })
            ]),
            TotalStepCount: 3);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(3);
        result.Steps[0].Status.ShouldBe(StepStatus.Succeeded);
        result.Steps[1].Status.ShouldBe(StepStatus.Failed);
        result.Steps[1].OutputJson.ShouldBeNull();
        result.Steps[1].Error.ShouldBe("State change failed");
        result.Steps[2].Status.ShouldBe(StepStatus.Skipped);
        result.Steps[2].Error!.ShouldContain("prior step failure");
    }

    [Fact]
    public async Task Execute_Sequence_FirstStepFails_SkipsAll()
    {
        var dispatcher = CreateDispatcher((tool, _) =>
            ErrorResult($"{tool} failed"));

        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new SequenceNode([
                new StepNode(0, "twig_set", new Dictionary<string, object?>()),
                new StepNode(1, "twig_status", new Dictionary<string, object?>()),
            ]),
            TotalStepCount: 2);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps[0].Status.ShouldBe(StepStatus.Failed);
        result.Steps[1].Status.ShouldBe(StepStatus.Skipped);
    }

    // ── Parallel execution ──────────────────────────────────────────

    [Fact]
    public async Task Execute_Parallel_ExecutesAllConcurrently()
    {
        var concurrencyTracker = new ConcurrencyTracker();
        var dispatcher = new TestToolDispatcher(async (tool, _, ct) =>
        {
            concurrencyTracker.Enter();
            await Task.Delay(50, ct);
            concurrencyTracker.Exit();
            return SuccessResult($"{{\"tool\":\"{tool}\"}}");
        });

        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new ParallelNode([
                new StepNode(0, "twig_show", new Dictionary<string, object?> { ["id"] = 1 }),
                new StepNode(1, "twig_show", new Dictionary<string, object?> { ["id"] = 2 }),
                new StepNode(2, "twig_show", new Dictionary<string, object?> { ["id"] = 3 })
            ]),
            TotalStepCount: 3);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(3);
        result.Steps.ShouldAllBe(s => s.Status == StepStatus.Succeeded);
        concurrencyTracker.MaxConcurrency.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task Execute_Parallel_OneFailure_OthersStillComplete()
    {
        var dispatcher = CreateDispatcher((tool, args) =>
        {
            // Fail the second step
            if (args.TryGetValue("id", out var id) && id is int idInt && idInt == 2)
                return ErrorResult("Not found");
            return SuccessResult($"{{\"id\":{args["id"]}}}");
        });

        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new ParallelNode([
                new StepNode(0, "twig_show", new Dictionary<string, object?> { ["id"] = 1 }),
                new StepNode(1, "twig_show", new Dictionary<string, object?> { ["id"] = 2 }),
                new StepNode(2, "twig_show", new Dictionary<string, object?> { ["id"] = 3 })
            ]),
            TotalStepCount: 3);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps[0].Status.ShouldBe(StepStatus.Succeeded);
        result.Steps[1].Status.ShouldBe(StepStatus.Failed);
        result.Steps[2].Status.ShouldBe(StepStatus.Succeeded);
    }

    // ── Nested graphs ───────────────────────────────────────────────

    [Fact]
    public async Task Execute_SequenceContainingParallel_ExecutesCorrectly()
    {
        var order = new List<int>();
        var dispatcher = CreateDispatcher((tool, args) =>
        {
            if (args.TryGetValue("id", out var id) && id is int idInt)
                lock (order) { order.Add(idInt); }
            return SuccessResult($"{{\"tool\":\"{tool}\"}}");
        });

        var engine = new BatchExecutionEngine(dispatcher);

        // Sequence: set → parallel(show×2) → note
        var graph = new BatchGraph(
            new SequenceNode([
                new StepNode(0, "twig_set", new Dictionary<string, object?> { ["id"] = 100 }),
                new ParallelNode([
                    new StepNode(1, "twig_show", new Dictionary<string, object?> { ["id"] = 200 }),
                    new StepNode(2, "twig_show", new Dictionary<string, object?> { ["id"] = 201 })
                ]),
                new StepNode(3, "twig_note", new Dictionary<string, object?> { ["id"] = 300 })
            ]),
            TotalStepCount: 4);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(4);
        result.Steps.ShouldAllBe(s => s.Status == StepStatus.Succeeded);

        // Step 0 (set) must come before the parallel block
        order.First().ShouldBe(100);
        // Step 3 (note) must come after the parallel block
        order.Last().ShouldBe(300);
    }

    [Fact]
    public async Task Execute_SequenceWithNestedParallelFailure_SkipsRemaining()
    {
        var dispatcher = CreateDispatcher((tool, args) =>
        {
            if (tool == "twig_show" && args.TryGetValue("id", out var id) && id is int idInt && idInt == 200)
                return ErrorResult("show failed");
            return SuccessResult($"{{\"tool\":\"{tool}\"}}");
        });

        var engine = new BatchExecutionEngine(dispatcher);

        // Sequence: step0 → parallel(step1-fail, step2) → step3-should-skip
        var graph = new BatchGraph(
            new SequenceNode([
                new StepNode(0, "twig_set", new Dictionary<string, object?> { ["id"] = 100 }),
                new ParallelNode([
                    new StepNode(1, "twig_show", new Dictionary<string, object?> { ["id"] = 200 }),
                    new StepNode(2, "twig_show", new Dictionary<string, object?> { ["id"] = 201 })
                ]),
                new StepNode(3, "twig_note", new Dictionary<string, object?> { ["id"] = 300 })
            ]),
            TotalStepCount: 4);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps[0].Status.ShouldBe(StepStatus.Succeeded);
        result.Steps[1].Status.ShouldBe(StepStatus.Failed);
        result.Steps[2].Status.ShouldBe(StepStatus.Succeeded); // Parallel doesn't fail-fast
        result.Steps[3].Status.ShouldBe(StepStatus.Skipped);   // Sequence fail-fast after parallel block
    }

    // ── Timeout handling ────────────────────────────────────────────

    [Fact]
    public async Task Execute_Timeout_MarksRemainingAsSkipped()
    {
        var callCount = 0;
        var dispatcher = new TestToolDispatcher(async (_, _, ct) =>
        {
            var current = Interlocked.Increment(ref callCount);
            if (current == 2)
            {
                // This step should trigger timeout
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            return SuccessResult("{\"ok\":true}");
        });

        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new SequenceNode([
                new StepNode(0, "twig_set", new Dictionary<string, object?>()),
                new StepNode(1, "twig_state", new Dictionary<string, object?>()),
                new StepNode(2, "twig_note", new Dictionary<string, object?>())
            ]),
            TotalStepCount: 3);

        var result = await engine.ExecuteAsync(
            graph,
            TimeSpan.FromMilliseconds(200),
            null,
            CancellationToken.None);

        result.TimedOut.ShouldBeTrue();
        result.Steps[0].Status.ShouldBe(StepStatus.Succeeded);
        // Step 1 was interrupted by timeout
        result.Steps[1].Status.ShouldBe(StepStatus.Skipped);
        // Step 2 never started
        result.Steps[2].Status.ShouldBe(StepStatus.Skipped);
    }

    [Fact]
    public async Task Execute_ExternalCancellation_PropagatesException()
    {
        var dispatcher = CreateDelayedDispatcher(TimeSpan.FromSeconds(10));
        var engine = new BatchExecutionEngine(dispatcher);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var graph = new BatchGraph(
            new StepNode(0, "twig_status", new Dictionary<string, object?>()),
            TotalStepCount: 1);

        await Should.ThrowAsync<OperationCanceledException>(
            () => engine.ExecuteAsync(graph, DefaultTimeout, null, cts.Token));
    }

    [Fact]
    public async Task Execute_Timeout_InParallelBlock_SetsTimedOutTrue()
    {
        var dispatcher = CreateDelayedDispatcher(TimeSpan.FromSeconds(10));
        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new ParallelNode([
                new StepNode(0, "twig_show", new Dictionary<string, object?> { ["id"] = 1 }),
                new StepNode(1, "twig_show", new Dictionary<string, object?> { ["id"] = 2 })
            ]),
            TotalStepCount: 2);

        var result = await engine.ExecuteAsync(
            graph,
            TimeSpan.FromMilliseconds(100),
            null,
            CancellationToken.None);

        result.TimedOut.ShouldBeTrue();
        result.Steps.ShouldAllBe(s => s.Status == StepStatus.Skipped);
    }

    [Fact]
    public async Task Execute_ExternalCancellation_InParallelBlock_PropagatesException()
    {
        var dispatcher = CreateDelayedDispatcher(TimeSpan.FromSeconds(10));
        var engine = new BatchExecutionEngine(dispatcher);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var graph = new BatchGraph(
            new ParallelNode([
                new StepNode(0, "twig_show", new Dictionary<string, object?> { ["id"] = 1 }),
                new StepNode(1, "twig_show", new Dictionary<string, object?> { ["id"] = 2 })
            ]),
            TotalStepCount: 2);

        await Should.ThrowAsync<OperationCanceledException>(
            () => engine.ExecuteAsync(graph, DefaultTimeout, null, cts.Token));
    }

    // ── Workspace override passthrough ──────────────────────────────

    [Fact]
    public async Task Execute_WorkspaceOverride_PassedToDispatcher()
    {
        var dispatcher = CreateDispatcher();

        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new StepNode(0, "twig_status", new Dictionary<string, object?>()),
            TotalStepCount: 1);

        await engine.ExecuteAsync(graph, DefaultTimeout, "org/project", CancellationToken.None);

        dispatcher.LastWorkspaceOverride.ShouldBe("org/project");
    }

    // ── Timing ──────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_RecordsPerStepTiming()
    {
        var dispatcher = CreateDelayedDispatcher(TimeSpan.FromMilliseconds(50));
        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new StepNode(0, "twig_status", new Dictionary<string, object?>()),
            TotalStepCount: 1);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps[0].ElapsedMs.ShouldBeGreaterThanOrEqualTo(40); // Allow for timer precision
        result.TotalElapsedMs.ShouldBeGreaterThanOrEqualTo(40);
    }

    // ── Empty containers ────────────────────────────────────────────

    [Fact]
    public async Task Execute_EmptySequence_ReturnsEmptyResult()
    {
        var dispatcher = CreateDispatcher();
        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new SequenceNode([]),
            TotalStepCount: 0);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(0);
        result.TimedOut.ShouldBeFalse();
    }

    [Fact]
    public async Task Execute_EmptyParallel_ReturnsEmptyResult()
    {
        var dispatcher = CreateDispatcher();
        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new ParallelNode([]),
            TotalStepCount: 0);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(0);
        result.TimedOut.ShouldBeFalse();
    }

    // ── Output JSON extraction ──────────────────────────────────────

    [Fact]
    public async Task Execute_StepOutput_CapturedAsJson()
    {
        var expectedJson = "{\"id\":42,\"title\":\"My Task\"}";
        var dispatcher = CreateDispatcher((_, _) => SuccessResult(expectedJson));
        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new StepNode(0, "twig_show", new Dictionary<string, object?> { ["id"] = 42 }),
            TotalStepCount: 1);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps[0].OutputJson.ShouldBe(expectedJson);
    }

    [Fact]
    public async Task Execute_StepWithEmptyContent_OutputJsonIsNull()
    {
        var dispatcher = new TestToolDispatcher((_, _, _) =>
            Task.FromResult(new CallToolResult { Content = [] }));

        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new StepNode(0, "twig_status", new Dictionary<string, object?>()),
            TotalStepCount: 1);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps[0].OutputJson.ShouldBeNull();
        result.Steps[0].Status.ShouldBe(StepStatus.Succeeded);
    }

    // ── Complex multi-level nesting ─────────────────────────────────

    [Fact]
    public async Task Execute_DeeplyNested_SequenceParallelSequence_WorksCorrectly()
    {
        var dispatcher = CreateDispatcher();
        var engine = new BatchExecutionEngine(dispatcher);

        // sequence(
        //   parallel(
        //     sequence(step0, step1),
        //     sequence(step2, step3)
        //   ),
        //   step4
        // )
        var graph = new BatchGraph(
            new SequenceNode([
                new ParallelNode([
                    new SequenceNode([
                        new StepNode(0, "twig_set", new Dictionary<string, object?>()),
                        new StepNode(1, "twig_status", new Dictionary<string, object?>())
                    ]),
                    new SequenceNode([
                        new StepNode(2, "twig_set", new Dictionary<string, object?>()),
                        new StepNode(3, "twig_status", new Dictionary<string, object?>())
                    ])
                ]),
                new StepNode(4, "twig_note", new Dictionary<string, object?>())
            ]),
            TotalStepCount: 5);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(5);
        result.Steps.ShouldAllBe(s => s.Status == StepStatus.Succeeded);
    }

    [Fact]
    public async Task Execute_NestedSequenceFailure_SkipsCascadesCorrectly()
    {
        var dispatcher = CreateDispatcher((tool, _) =>
        {
            // step2 fails
            return tool == "fail_step" ? ErrorResult("forced failure") : SuccessResult("{}");
        });

        var engine = new BatchExecutionEngine(dispatcher);

        // sequence(
        //   parallel(
        //     sequence(step0, step1-fail, step2-skip),  ← inner fail-fast
        //     sequence(step3, step4)                     ← runs independently
        //   ),
        //   step5-skip ← sequence fail-fast after parallel contains failure
        // )
        var graph = new BatchGraph(
            new SequenceNode([
                new ParallelNode([
                    new SequenceNode([
                        new StepNode(0, "twig_set", new Dictionary<string, object?>()),
                        new StepNode(1, "fail_step", new Dictionary<string, object?>()),
                        new StepNode(2, "twig_note", new Dictionary<string, object?>())
                    ]),
                    new SequenceNode([
                        new StepNode(3, "twig_set", new Dictionary<string, object?>()),
                        new StepNode(4, "twig_status", new Dictionary<string, object?>())
                    ])
                ]),
                new StepNode(5, "twig_note", new Dictionary<string, object?>())
            ]),
            TotalStepCount: 6);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps[0].Status.ShouldBe(StepStatus.Succeeded);
        result.Steps[1].Status.ShouldBe(StepStatus.Failed);
        result.Steps[2].Status.ShouldBe(StepStatus.Skipped); // Inner sequence fail-fast
        result.Steps[3].Status.ShouldBe(StepStatus.Succeeded); // Parallel branch independent
        result.Steps[4].Status.ShouldBe(StepStatus.Succeeded);
        result.Steps[5].Status.ShouldBe(StepStatus.Skipped); // Outer sequence fail-fast
    }

    // ── Template resolution failure ────────────────────────────────

    [Fact]
    public async Task Execute_StepWithBadTemplate_RecordsFailureWithMessage()
    {
        // Step 0 succeeds with output that has no "id" property.
        // Step 1 references {{steps.0.id}} which doesn't exist → TemplateResolutionException.
        var dispatcher = CreateDispatcher((_, _) => SuccessResult("{\"name\":\"test\"}"));
        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new SequenceNode([
                new StepNode(0, "twig_show", new Dictionary<string, object?>()),
                new StepNode(1, "twig_set", new Dictionary<string, object?>
                {
                    ["idOrPattern"] = "{{steps.0.id}}"
                })
            ]),
            TotalStepCount: 2);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(2);
        result.Steps[0].Status.ShouldBe(StepStatus.Succeeded);
        result.Steps[1].Status.ShouldBe(StepStatus.Failed);
        result.Steps[1].Error.ShouldNotBeNull();
        result.Steps[1].Error!.ShouldContain("steps.0.id");
        result.Steps[1].Error!.ShouldContain("not found");
        result.TimedOut.ShouldBeFalse();
    }

    // ── Template resolution success ────────────────────────────────

    [Fact]
    public async Task Execute_TemplateChaining_ResolvesAcrossSequentialSteps()
    {
        var capturedArgs = new List<IReadOnlyDictionary<string, object?>>();
        var dispatcher = new TestToolDispatcher((tool, args, _) =>
        {
            lock (capturedArgs) { capturedArgs.Add(new Dictionary<string, object?>(args)); }
            return tool switch
            {
                "twig_new" => Task.FromResult(SuccessResult("{\"id\":100,\"title\":\"Created\"}")),
                _ => Task.FromResult(SuccessResult("{\"ok\":true}"))
            };
        });
        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new SequenceNode([
                new StepNode(0, "twig_new", new Dictionary<string, object?>
                {
                    ["type"] = "Task",
                    ["title"] = "Test"
                }),
                new StepNode(1, "twig_set", new Dictionary<string, object?>
                {
                    ["idOrPattern"] = "{{steps.0.id}}"
                }),
                new StepNode(2, "twig_note", new Dictionary<string, object?>
                {
                    ["text"] = "Set up item {{steps.0.id}}: {{steps.0.title}}"
                })
            ]),
            TotalStepCount: 3);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(3);
        result.Steps.ShouldAllBe(s => s.Status == StepStatus.Succeeded);

        // Full expression: integer type preserved.
        capturedArgs[1]["idOrPattern"].ShouldBeOfType<int>().ShouldBe(100);

        // Partial expression: string interpolation.
        capturedArgs[2]["text"].ShouldBeOfType<string>().ShouldBe("Set up item 100: Created");
    }

    [Fact]
    public async Task Execute_TemplateChaining_NestedPath_ResolvesCorrectly()
    {
        object? resolved = null;
        var dispatcher = new TestToolDispatcher((tool, args, _) =>
        {
            if (tool == "twig_set")
                resolved = args["idOrPattern"];
            return Task.FromResult(tool == "twig_show"
                ? SuccessResult("{\"item\":{\"fields\":{\"id\":777}}}")
                : SuccessResult("{\"ok\":true}"));
        });
        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new SequenceNode([
                new StepNode(0, "twig_show", new Dictionary<string, object?>()),
                new StepNode(1, "twig_set", new Dictionary<string, object?>
                {
                    ["idOrPattern"] = "{{steps.0.item.fields.id}}"
                })
            ]),
            TotalStepCount: 2);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.ShouldAllBe(s => s.Status == StepStatus.Succeeded);
        resolved.ShouldBeOfType<int>().ShouldBe(777);
    }

    [Fact]
    public async Task Execute_TemplateResolutionFailure_CascadesSkipInSequence()
    {
        var dispatcher = CreateDispatcher((_, _) => SuccessResult("{\"name\":\"x\"}"));
        var engine = new BatchExecutionEngine(dispatcher);

        // Step 1 fails template resolution → Step 2 is skipped (fail-fast).
        var graph = new BatchGraph(
            new SequenceNode([
                new StepNode(0, "twig_show", new Dictionary<string, object?>()),
                new StepNode(1, "twig_set", new Dictionary<string, object?>
                {
                    ["idOrPattern"] = "{{steps.0.missing}}"
                }),
                new StepNode(2, "twig_note", new Dictionary<string, object?>
                {
                    ["text"] = "should not run"
                })
            ]),
            TotalStepCount: 3);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps[0].Status.ShouldBe(StepStatus.Succeeded);
        result.Steps[1].Status.ShouldBe(StepStatus.Failed);
        result.Steps[2].Status.ShouldBe(StepStatus.Skipped);
    }

    [Fact]
    public async Task Execute_TemplateWithNonStringArgs_PassedThrough()
    {
        var capturedArgs = new Dictionary<string, object?>();
        var dispatcher = new TestToolDispatcher((tool, args, _) =>
        {
            if (tool == "twig_link")
                lock (capturedArgs)
                    foreach (var kv in args) capturedArgs[kv.Key] = kv.Value;
            return Task.FromResult(tool == "twig_new"
                ? SuccessResult("{\"id\":55}")
                : SuccessResult("{\"ok\":true}"));
        });
        var engine = new BatchExecutionEngine(dispatcher);

        // sourceId is a non-string int — should pass through without template processing.
        // targetId is a template — should resolve to int 55.
        var graph = new BatchGraph(
            new SequenceNode([
                new StepNode(0, "twig_new", new Dictionary<string, object?>
                {
                    ["type"] = "Task",
                    ["title"] = "Parent"
                }),
                new StepNode(1, "twig_link", new Dictionary<string, object?>
                {
                    ["sourceId"] = 1,
                    ["targetId"] = "{{steps.0.id}}",
                    ["linkType"] = "child"
                })
            ]),
            TotalStepCount: 2);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.ShouldAllBe(s => s.Status == StepStatus.Succeeded);
        capturedArgs["sourceId"].ShouldBe(1); // Non-string: passed through.
        capturedArgs["targetId"].ShouldBeOfType<int>().ShouldBe(55); // Template: resolved.
        capturedArgs["linkType"].ShouldBe("child"); // Non-template string: passed through.
    }

    // ── Helper for tracking concurrent execution ────────────────────

    private sealed class ConcurrencyTracker
    {
        private int _current;
        private int _max;

        public int MaxConcurrency => _max;

        public void Enter()
        {
            var value = Interlocked.Increment(ref _current);
            // Update max using CAS loop
            int currentMax;
            do
            {
                currentMax = _max;
                if (value <= currentMax) break;
            } while (Interlocked.CompareExchange(ref _max, value, currentMax) != currentMax);
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }
}
