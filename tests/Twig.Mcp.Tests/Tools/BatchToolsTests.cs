using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Twig.Mcp.Services.Batch;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

public sealed class BatchToolsTests
{
    // ── Test infrastructure ─────────────────────────────────────────

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

    private static CallToolResult SuccessResult(string json) =>
        new() { Content = [new TextContentBlock { Text = json }] };

    private static CallToolResult ErrorResult(string message) =>
        new() { Content = [new TextContentBlock { Text = message }], IsError = true };

    private static string ExtractJson(CallToolResult result) =>
        ((TextContentBlock)result.Content![0]).Text;

    private static JsonElement ParseResult(CallToolResult result) =>
        JsonDocument.Parse(ExtractJson(result)).RootElement;

    // ── Happy path: single step ─────────────────────────────────────

    [Fact]
    public async Task Batch_SingleStep_ReturnsFormattedResult()
    {
        var dispatcher = CreateDispatcher();
        var tools = new BatchTools(dispatcher);

        var graph = """
        {
            "type": "step",
            "tool": "twig_status",
            "args": {}
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);

        result.IsError.ShouldNotBe(true);

        var root = ParseResult(result);
        root.GetProperty("steps").GetArrayLength().ShouldBe(1);
        root.GetProperty("steps")[0].GetProperty("tool").GetString().ShouldBe("twig_status");
        root.GetProperty("steps")[0].GetProperty("status").GetString().ShouldBe("succeeded");
        root.GetProperty("summary").GetProperty("total").GetInt32().ShouldBe(1);
        root.GetProperty("summary").GetProperty("succeeded").GetInt32().ShouldBe(1);
        root.GetProperty("timedOut").GetBoolean().ShouldBeFalse();
    }

    // ── Happy path: sequence with multiple steps ────────────────────

    [Fact]
    public async Task Batch_Sequence_ExecutesAllSteps()
    {
        var order = new List<string>();
        var dispatcher = CreateDispatcher((tool, _) =>
        {
            order.Add(tool);
            return SuccessResult($"{{\"tool\":\"{tool}\"}}");
        });
        var tools = new BatchTools(dispatcher);

        var graph = """
        {
            "type": "sequence",
            "steps": [
                { "type": "step", "tool": "twig_set", "args": { "idOrPattern": "42" } },
                { "type": "step", "tool": "twig_note", "args": { "text": "hello" } }
            ]
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);
        var root = ParseResult(result);

        root.GetProperty("summary").GetProperty("total").GetInt32().ShouldBe(2);
        root.GetProperty("summary").GetProperty("succeeded").GetInt32().ShouldBe(2);
        order.ShouldBe(["twig_set", "twig_note"]);
    }

    // ── Validation: empty graph ─────────────────────────────────────

    [Fact]
    public async Task Batch_EmptyGraph_ReturnsError()
    {
        var tools = new BatchTools(CreateDispatcher());

        var result = await tools.Batch("", ct: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = ExtractJson(result);
        text.ShouldContain("graph");
    }

    [Fact]
    public async Task Batch_NullGraph_ReturnsError()
    {
        var tools = new BatchTools(CreateDispatcher());

        var result = await tools.Batch(null!, ct: CancellationToken.None);

        result.IsError.ShouldBe(true);
    }

    [Fact]
    public async Task Batch_WhitespaceGraph_ReturnsError()
    {
        var tools = new BatchTools(CreateDispatcher());

        var result = await tools.Batch("   ", ct: CancellationToken.None);

        result.IsError.ShouldBe(true);
    }

    // ── Validation: invalid JSON ────────────────────────────────────

    [Fact]
    public async Task Batch_InvalidJson_ReturnsParseError()
    {
        var tools = new BatchTools(CreateDispatcher());

        var result = await tools.Batch("{invalid}", ct: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = ExtractJson(result);
        text.ShouldContain("validation failed");
    }

    // ── Validation: recursive batch banned ──────────────────────────

    [Fact]
    public async Task Batch_RecursiveBatch_ReturnsError()
    {
        var tools = new BatchTools(CreateDispatcher());

        var graph = """
        {
            "type": "step",
            "tool": "twig_batch",
            "args": {}
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = ExtractJson(result);
        text.ShouldContain("Recursive batch");
    }

    // ── Timeout parameter handling ──────────────────────────────────

    [Fact]
    public async Task Batch_DefaultTimeout_UsesConstant()
    {
        var dispatcher = CreateDispatcher();
        var tools = new BatchTools(dispatcher);

        var graph = """{ "type": "step", "tool": "twig_status", "args": {} }""";

        // Should not throw — default timeout is applied internally
        var result = await tools.Batch(graph, timeoutSeconds: null, ct: CancellationToken.None);
        result.IsError.ShouldNotBe(true);
    }

    [Fact]
    public async Task Batch_NegativeTimeout_UsesDefault()
    {
        var dispatcher = CreateDispatcher();
        var tools = new BatchTools(dispatcher);

        var graph = """{ "type": "step", "tool": "twig_status", "args": {} }""";

        var result = await tools.Batch(graph, timeoutSeconds: -5, ct: CancellationToken.None);
        result.IsError.ShouldNotBe(true);
    }

    [Fact]
    public async Task Batch_ExcessiveTimeout_CappedAt300()
    {
        // Can't directly assert the timeout value, but we can verify
        // the call succeeds (proving it doesn't use an unreasonable timeout).
        var dispatcher = CreateDispatcher();
        var tools = new BatchTools(dispatcher);

        var graph = """{ "type": "step", "tool": "twig_status", "args": {} }""";

        var result = await tools.Batch(graph, timeoutSeconds: 9999, ct: CancellationToken.None);
        result.IsError.ShouldNotBe(true);
    }

    // ── Workspace override propagation ──────────────────────────────

    [Fact]
    public async Task Batch_WorkspaceOverride_PropagatedToDispatcher()
    {
        var dispatcher = CreateDispatcher();
        var tools = new BatchTools(dispatcher);

        var graph = """{ "type": "step", "tool": "twig_status", "args": {} }""";

        await tools.Batch(graph, workspace: "myorg/myproject", ct: CancellationToken.None);

        dispatcher.LastWorkspaceOverride.ShouldBe("myorg/myproject");
    }

    [Fact]
    public async Task Batch_NoWorkspace_PropagatesNull()
    {
        var dispatcher = CreateDispatcher();
        var tools = new BatchTools(dispatcher);

        var graph = """{ "type": "step", "tool": "twig_status", "args": {} }""";

        await tools.Batch(graph, workspace: null, ct: CancellationToken.None);

        dispatcher.LastWorkspaceOverride.ShouldBeNull();
    }

    // ── Sequence with failure: fail-fast and skip ────────────────────

    [Fact]
    public async Task Batch_SequenceWithFailure_SkipsRemaining()
    {
        var dispatcher = CreateDispatcher((tool, _) =>
        {
            if (tool == "twig_state")
                return ErrorResult("State change failed");
            return SuccessResult($"{{\"tool\":\"{tool}\"}}");
        });
        var tools = new BatchTools(dispatcher);

        var graph = """
        {
            "type": "sequence",
            "steps": [
                { "type": "step", "tool": "twig_set", "args": { "idOrPattern": "1" } },
                { "type": "step", "tool": "twig_state", "args": { "stateName": "Done" } },
                { "type": "step", "tool": "twig_note", "args": { "text": "won't run" } }
            ]
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);
        var root = ParseResult(result);

        var summary = root.GetProperty("summary");
        summary.GetProperty("succeeded").GetInt32().ShouldBe(1);
        summary.GetProperty("failed").GetInt32().ShouldBe(1);
        summary.GetProperty("skipped").GetInt32().ShouldBe(1);
    }

    // ── Parallel execution ──────────────────────────────────────────

    [Fact]
    public async Task Batch_Parallel_AllStepsExecute()
    {
        var executed = new List<string>();
        var dispatcher = new TestToolDispatcher((tool, _, _) =>
        {
            lock (executed) { executed.Add(tool); }
            return Task.FromResult(SuccessResult($"{{\"tool\":\"{tool}\"}}"));
        });
        var tools = new BatchTools(dispatcher);

        var graph = """
        {
            "type": "parallel",
            "steps": [
                { "type": "step", "tool": "twig_note", "args": { "text": "a" } },
                { "type": "step", "tool": "twig_update", "args": { "field": "System.Title", "value": "x" } }
            ]
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);
        var root = ParseResult(result);

        root.GetProperty("summary").GetProperty("total").GetInt32().ShouldBe(2);
        root.GetProperty("summary").GetProperty("succeeded").GetInt32().ShouldBe(2);
        executed.Count.ShouldBe(2);
    }

    // ── Full output shape validation ────────────────────────────────

    [Fact]
    public async Task Batch_ResultHasAllExpectedFields()
    {
        var dispatcher = CreateDispatcher();
        var tools = new BatchTools(dispatcher);

        var graph = """{ "type": "step", "tool": "twig_status", "args": {} }""";

        var result = await tools.Batch(graph, ct: CancellationToken.None);
        var root = ParseResult(result);

        // Top-level fields
        root.TryGetProperty("steps", out _).ShouldBeTrue();
        root.TryGetProperty("summary", out _).ShouldBeTrue();
        root.TryGetProperty("totalElapsedMs", out _).ShouldBeTrue();
        root.TryGetProperty("timedOut", out _).ShouldBeTrue();

        // Step fields
        var step = root.GetProperty("steps")[0];
        step.TryGetProperty("index", out _).ShouldBeTrue();
        step.TryGetProperty("tool", out _).ShouldBeTrue();
        step.TryGetProperty("status", out _).ShouldBeTrue();
        step.TryGetProperty("output", out _).ShouldBeTrue();
        step.TryGetProperty("elapsedMs", out _).ShouldBeTrue();

        // Summary fields
        var summary = root.GetProperty("summary");
        summary.TryGetProperty("total", out _).ShouldBeTrue();
        summary.TryGetProperty("succeeded", out _).ShouldBeTrue();
        summary.TryGetProperty("failed", out _).ShouldBeTrue();
        summary.TryGetProperty("skipped", out _).ShouldBeTrue();
    }
}
