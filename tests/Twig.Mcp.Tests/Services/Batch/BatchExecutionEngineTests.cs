using ModelContextProtocol.Protocol;
using Shouldly;
using Twig.Mcp.Services.Batch;
using Xunit;

namespace Twig.Mcp.Tests.Services.Batch;

public sealed class BatchExecutionEngineTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // ── Test infrastructure ─────────────────────────────────────────

    /// <summary>
    /// Concrete test dispatcher — NSubstitute cannot proxy internal interfaces
    /// because DynamicProxy lacks InternalsVisibleTo access.
    /// </summary>
    private sealed class TestToolDispatcher(
        Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<CallToolResult>> handler)
        : IToolDispatcher
    {
        public string? LastWorkspaceOverride { get; private set; }

        public Task<CallToolResult> DispatchAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> args,
            string? workspaceOverride,
            CancellationToken ct)
        {
            LastWorkspaceOverride = workspaceOverride;
            return handler(toolName, args, ct);
        }
    }

    private static TestToolDispatcher CreateDispatcher(
        Func<string, IReadOnlyDictionary<string, object?>, CallToolResult>? handler = null) =>
        new((tool, args, _) =>
            Task.FromResult(handler is not null
                ? handler(tool, args)
                : SuccessResult($"{{\"tool\":\"{tool}\",\"ok\":true}}")));

    private static TestToolDispatcher CreateDelayedDispatcher(TimeSpan delay) =>
        new(async (tool, _, ct) =>
        {
            await Task.Delay(delay, ct);
            return SuccessResult($"{{\"tool\":\"{tool}\",\"ok\":true}}");
        });

    private static TestToolDispatcher CreateThrowingDispatcher(Exception ex) =>
        new((_, _, _) => throw ex);

    private static CallToolResult SuccessResult(string json) =>
        new() { Content = [new TextContentBlock { Text = json }] };

    private static CallToolResult ErrorResult(string message) =>
        new() { Content = [new TextContentBlock { Text = message }], IsError = true };

    // ── Single step execution ───────────────────────────────────────

    [Fact]
    public async Task Execute_SingleStep_ReturnsSuccessResult()
    {
        var dispatcher = CreateDispatcher();
        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new StepNode(0, "twig_status", new Dictionary<string, object?>()),
            TotalStepCount: 1,
            MaxDepth: 0);

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
            TotalStepCount: 1,
            MaxDepth: 0);

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
            TotalStepCount: 1,
            MaxDepth: 0);

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
            TotalStepCount: 3,
            MaxDepth: 1);

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
            TotalStepCount: 3,
            MaxDepth: 1);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps.Count.ShouldBe(3);
        result.Steps[0].Status.ShouldBe(StepStatus.Succeeded);
        result.Steps[1].Status.ShouldBe(StepStatus.Failed);
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
            TotalStepCount: 2,
            MaxDepth: 1);

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
            TotalStepCount: 3,
            MaxDepth: 1);

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
            TotalStepCount: 3,
            MaxDepth: 1);

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
            TotalStepCount: 4,
            MaxDepth: 2);

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
            TotalStepCount: 4,
            MaxDepth: 2);

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
            TotalStepCount: 3,
            MaxDepth: 1);

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
            TotalStepCount: 1,
            MaxDepth: 0);

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
            TotalStepCount: 1,
            MaxDepth: 0);

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
            TotalStepCount: 1,
            MaxDepth: 0);

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
            TotalStepCount: 0,
            MaxDepth: 1);

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
            TotalStepCount: 0,
            MaxDepth: 1);

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
            TotalStepCount: 1,
            MaxDepth: 0);

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
            TotalStepCount: 1,
            MaxDepth: 0);

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
            TotalStepCount: 5,
            MaxDepth: 3);

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
            TotalStepCount: 6,
            MaxDepth: 3);

        var result = await engine.ExecuteAsync(graph, DefaultTimeout, null, CancellationToken.None);

        result.Steps[0].Status.ShouldBe(StepStatus.Succeeded);
        result.Steps[1].Status.ShouldBe(StepStatus.Failed);
        result.Steps[2].Status.ShouldBe(StepStatus.Skipped); // Inner sequence fail-fast
        result.Steps[3].Status.ShouldBe(StepStatus.Succeeded); // Parallel branch independent
        result.Steps[4].Status.ShouldBe(StepStatus.Succeeded);
        result.Steps[5].Status.ShouldBe(StepStatus.Skipped); // Outer sequence fail-fast
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
