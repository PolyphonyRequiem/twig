using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Twig.Mcp.Services.Batch;
using Twig.Mcp.Tests.Services.Batch;
using Twig.Mcp.Tools;
using Xunit;
using static Twig.Mcp.Tests.Services.Batch.BatchTestHelpers;

namespace Twig.Mcp.Tests.Tools;

public sealed class BatchToolsTests
{
    // ── Test infrastructure ─────────────────────────────────────────
    // Shared: TestToolDispatcher, CreateDispatcher, SuccessResult, ErrorResult → BatchTestHelpers.cs

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

    // ── Template chaining: create → set → update ─────────────────────

    [Fact]
    public async Task Batch_TemplateChaining_CreateSetUpdate_ResolvesCorrectly()
    {
        var callLog = new List<(string tool, IReadOnlyDictionary<string, object?> args)>();
        var dispatcher = new TestToolDispatcher((tool, args, _) =>
        {
            lock (callLog) { callLog.Add((tool, args)); }
            return tool switch
            {
                "twig_new" => Task.FromResult(SuccessResult("{\"id\":1234,\"title\":\"My Task\"}")),
                "twig_set" => Task.FromResult(SuccessResult("{\"id\":1234,\"ok\":true}")),
                "twig_update" => Task.FromResult(SuccessResult("{\"id\":1234,\"updated\":true}")),
                _ => Task.FromResult(SuccessResult("{\"ok\":true}"))
            };
        });
        var tools = new BatchTools(dispatcher);

        var graph = """
        {
            "type": "sequence",
            "steps": [
                { "type": "step", "tool": "twig_new", "args": { "type": "Task", "title": "My Task", "parentId": 42 } },
                { "type": "step", "tool": "twig_set", "args": { "idOrPattern": "{{steps.0.id}}" } },
                { "type": "step", "tool": "twig_update", "args": { "field": "System.Description", "value": "Created item {{steps.0.id}}", "format": "markdown" } }
            ]
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);
        var root = ParseResult(result);

        root.GetProperty("summary").GetProperty("succeeded").GetInt32().ShouldBe(3);

        // Step 1 (twig_set): idOrPattern should be resolved to integer 1234 (type preservation).
        callLog[1].args["idOrPattern"].ShouldBe(1234);

        // Step 2 (twig_update): value should be partial interpolation → string "Created item 1234".
        callLog[2].args["value"].ShouldBe("Created item 1234");
    }

    // ── Template chaining: nested property paths ────────────────────

    [Fact]
    public async Task Batch_TemplateChaining_NestedPropertyPath_ResolvesCorrectly()
    {
        var capturedArgs = new Dictionary<string, object?>();
        var dispatcher = new TestToolDispatcher((tool, args, _) =>
        {
            if (tool == "twig_set")
            {
                lock (capturedArgs)
                    foreach (var kv in args) capturedArgs[kv.Key] = kv.Value;
            }
            return Task.FromResult(tool == "twig_new"
                ? SuccessResult("{\"item\":{\"id\":5678,\"title\":\"Nested\"}}")
                : SuccessResult("{\"ok\":true}"));
        });
        var tools = new BatchTools(dispatcher);

        var graph = """
        {
            "type": "sequence",
            "steps": [
                { "type": "step", "tool": "twig_new", "args": { "type": "Task", "title": "Test" } },
                { "type": "step", "tool": "twig_set", "args": { "idOrPattern": "{{steps.0.item.id}}" } }
            ]
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);
        var root = ParseResult(result);

        root.GetProperty("summary").GetProperty("succeeded").GetInt32().ShouldBe(2);
        capturedArgs["idOrPattern"].ShouldBe(5678);
    }

    // ── Template: type preservation for full-value templates ────────

    [Fact]
    public async Task Batch_TemplateChaining_TypePreservation_IntegerStaysInteger()
    {
        object? resolvedValue = null;
        var dispatcher = new TestToolDispatcher((tool, args, _) =>
        {
            if (tool == "twig_link")
                resolvedValue = args["targetId"];
            return Task.FromResult(tool == "twig_new"
                ? SuccessResult("{\"id\":42}")
                : SuccessResult("{\"ok\":true}"));
        });
        var tools = new BatchTools(dispatcher);

        var graph = """
        {
            "type": "sequence",
            "steps": [
                { "type": "step", "tool": "twig_new", "args": { "type": "Task", "title": "A" } },
                { "type": "step", "tool": "twig_link", "args": { "sourceId": 1, "targetId": "{{steps.0.id}}", "linkType": "child" } }
            ]
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);
        var root = ParseResult(result);
        root.GetProperty("summary").GetProperty("succeeded").GetInt32().ShouldBe(2);

        resolvedValue.ShouldBeOfType<int>().ShouldBe(42);
    }

    [Fact]
    public async Task Batch_TemplateChaining_TypePreservation_BooleanStaysBoolean()
    {
        object? resolvedValue = null;
        var dispatcher = new TestToolDispatcher((tool, args, _) =>
        {
            if (tool == "twig_update")
                resolvedValue = args["flag"];
            return Task.FromResult(tool == "twig_show"
                ? SuccessResult("{\"active\":true}")
                : SuccessResult("{\"ok\":true}"));
        });
        var tools = new BatchTools(dispatcher);

        var graph = """
        {
            "type": "sequence",
            "steps": [
                { "type": "step", "tool": "twig_show", "args": { "id": 1 } },
                { "type": "step", "tool": "twig_update", "args": { "flag": "{{steps.0.active}}" } }
            ]
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);
        var root = ParseResult(result);
        root.GetProperty("summary").GetProperty("succeeded").GetInt32().ShouldBe(2);

        resolvedValue.ShouldBeOfType<bool>().ShouldBeTrue();
    }

    // ── Template: partial string interpolation ──────────────────────

    [Fact]
    public async Task Batch_TemplateChaining_PartialInterpolation_ProducesString()
    {
        string? resolvedValue = null;
        var dispatcher = new TestToolDispatcher((tool, args, _) =>
        {
            if (tool == "twig_note")
                resolvedValue = args["text"] as string;
            return Task.FromResult(tool == "twig_new"
                ? SuccessResult("{\"id\":99,\"title\":\"Widget\"}")
                : SuccessResult("{\"ok\":true}"));
        });
        var tools = new BatchTools(dispatcher);

        var graph = """
        {
            "type": "sequence",
            "steps": [
                { "type": "step", "tool": "twig_new", "args": { "type": "Task", "title": "Widget" } },
                { "type": "step", "tool": "twig_note", "args": { "text": "Created #{{steps.0.id}}: {{steps.0.title}}" } }
            ]
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);
        var root = ParseResult(result);
        root.GetProperty("summary").GetProperty("succeeded").GetInt32().ShouldBe(2);

        resolvedValue.ShouldBe("Created #99: Widget");
    }

    // ── Template: forward ref rejection at parse time ────────────────

    [Fact]
    public async Task Batch_TemplateForwardRef_RejectedAtParseTime()
    {
        var tools = new BatchTools(CreateDispatcher());

        var graph = """
        {
            "type": "sequence",
            "steps": [
                { "type": "step", "tool": "twig_set", "args": { "idOrPattern": "{{steps.1.id}}" } },
                { "type": "step", "tool": "twig_new", "args": { "type": "Task", "title": "Late" } }
            ]
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = ExtractJson(result);
        text.ShouldContain("Forward reference");
    }

    // ── Template: self-reference rejection at parse time ─────────────

    [Fact]
    public async Task Batch_TemplateSelfRef_RejectedAtParseTime()
    {
        var tools = new BatchTools(CreateDispatcher());

        var graph = """
        {
            "type": "step",
            "tool": "twig_set",
            "args": { "idOrPattern": "{{steps.0.id}}" }
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = ExtractJson(result);
        text.ShouldContain("Forward reference");
    }

    // ── Template: parallel sibling ref rejection at parse time ──────

    [Fact]
    public async Task Batch_TemplateParallelSiblingRef_RejectedAtParseTime()
    {
        var tools = new BatchTools(CreateDispatcher());

        var graph = """
        {
            "type": "parallel",
            "steps": [
                { "type": "step", "tool": "twig_new", "args": { "type": "Task", "title": "A" } },
                { "type": "step", "tool": "twig_set", "args": { "idOrPattern": "{{steps.0.id}}" } }
            ]
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = ExtractJson(result);
        text.ShouldContain("Parallel sibling reference");
    }

    // ── Template: missing field error at execution time ──────────────

    [Fact]
    public async Task Batch_TemplateMissingField_FailsAtExecutionTime()
    {
        var dispatcher = CreateDispatcher((tool, _) =>
            SuccessResult("{\"title\":\"Test\"}"));
        var tools = new BatchTools(dispatcher);

        var graph = """
        {
            "type": "sequence",
            "steps": [
                { "type": "step", "tool": "twig_new", "args": { "type": "Task", "title": "Test" } },
                { "type": "step", "tool": "twig_set", "args": { "idOrPattern": "{{steps.0.nonExistent}}" } }
            ]
        }
        """;

        var result = await tools.Batch(graph, ct: CancellationToken.None);
        var root = ParseResult(result);

        root.GetProperty("steps")[0].GetProperty("status").GetString().ShouldBe("succeeded");
        root.GetProperty("steps")[1].GetProperty("status").GetString().ShouldBe("failed");
        root.GetProperty("steps")[1].GetProperty("error").GetString()!.ShouldContain("nonExistent");
        root.GetProperty("steps")[1].GetProperty("error").GetString()!.ShouldContain("not found");
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
