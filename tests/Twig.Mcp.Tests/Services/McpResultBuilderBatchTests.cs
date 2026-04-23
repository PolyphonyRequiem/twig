using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Twig.Mcp.Services;
using Twig.Mcp.Services.Batch;
using Xunit;

namespace Twig.Mcp.Tests.Services;

public sealed class McpResultBuilderBatchTests
{
    // ── Helper ──────────────────────────────────────────────────────

    private static string ExtractJson(CallToolResult result)
    {
        result.Content.ShouldNotBeNull();
        result.Content.Count.ShouldBeGreaterThan(0);
        var text = (result.Content[0] as TextContentBlock)!.Text;
        text.ShouldNotBeNull();
        return text;
    }

    private static JsonElement ParseJson(CallToolResult result)
    {
        var json = ExtractJson(result);
        return JsonDocument.Parse(json).RootElement;
    }

    // ── All-succeeded batch ─────────────────────────────────────────

    [Fact]
    public void FormatBatchResult_AllSucceeded_ReturnsCorrectShape()
    {
        var batch = new BatchResult(
            Steps:
            [
                new StepResult(0, "twig_set", StepStatus.Succeeded, "{\"id\":42}", null, 10),
                new StepResult(1, "twig_note", StepStatus.Succeeded, "{\"noteAdded\":true}", null, 5)
            ],
            TotalElapsedMs: 15,
            TimedOut: false);

        var result = McpResultBuilder.FormatBatchResult(batch);

        result.IsError.ShouldNotBe(true);

        var root = ParseJson(result);

        // Steps array
        var steps = root.GetProperty("steps");
        steps.GetArrayLength().ShouldBe(2);

        var step0 = steps[0];
        step0.GetProperty("index").GetInt32().ShouldBe(0);
        step0.GetProperty("tool").GetString().ShouldBe("twig_set");
        step0.GetProperty("status").GetString().ShouldBe("succeeded");
        step0.GetProperty("output").GetProperty("id").GetInt32().ShouldBe(42);
        step0.GetProperty("elapsedMs").GetInt64().ShouldBe(10);
        step0.TryGetProperty("error", out _).ShouldBeFalse();

        var step1 = steps[1];
        step1.GetProperty("index").GetInt32().ShouldBe(1);
        step1.GetProperty("tool").GetString().ShouldBe("twig_note");
        step1.GetProperty("status").GetString().ShouldBe("succeeded");
        step1.GetProperty("output").GetProperty("noteAdded").GetBoolean().ShouldBeTrue();

        // Summary
        var summary = root.GetProperty("summary");
        summary.GetProperty("total").GetInt32().ShouldBe(2);
        summary.GetProperty("succeeded").GetInt32().ShouldBe(2);
        summary.GetProperty("failed").GetInt32().ShouldBe(0);
        summary.GetProperty("skipped").GetInt32().ShouldBe(0);

        root.GetProperty("totalElapsedMs").GetInt64().ShouldBe(15);
        root.GetProperty("timedOut").GetBoolean().ShouldBeFalse();
    }

    // ── Mixed statuses ──────────────────────────────────────────────

    [Fact]
    public void FormatBatchResult_MixedStatuses_SummaryCountsCorrect()
    {
        var batch = new BatchResult(
            Steps:
            [
                new StepResult(0, "twig_set", StepStatus.Succeeded, "{\"id\":1}", null, 10),
                new StepResult(1, "twig_state", StepStatus.Failed, null, "State change failed", 20),
                new StepResult(2, "twig_note", StepStatus.Skipped, null, "Skipped due to prior failure.", 0)
            ],
            TotalElapsedMs: 30,
            TimedOut: false);

        var result = McpResultBuilder.FormatBatchResult(batch);
        var root = ParseJson(result);

        var summary = root.GetProperty("summary");
        summary.GetProperty("total").GetInt32().ShouldBe(3);
        summary.GetProperty("succeeded").GetInt32().ShouldBe(1);
        summary.GetProperty("failed").GetInt32().ShouldBe(1);
        summary.GetProperty("skipped").GetInt32().ShouldBe(1);

        // Failed step has error and null output
        var failedStep = root.GetProperty("steps")[1];
        failedStep.GetProperty("status").GetString().ShouldBe("failed");
        failedStep.GetProperty("error").GetString().ShouldBe("State change failed");
        failedStep.GetProperty("output").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── TimedOut flag ───────────────────────────────────────────────

    [Fact]
    public void FormatBatchResult_TimedOut_FlagIsTrue()
    {
        var batch = new BatchResult(
            Steps:
            [
                new StepResult(0, "twig_set", StepStatus.Succeeded, "{\"id\":1}", null, 100),
                new StepResult(1, "twig_state", StepStatus.Skipped, null, "Operation was cancelled.", 0)
            ],
            TotalElapsedMs: 120000,
            TimedOut: true);

        var result = McpResultBuilder.FormatBatchResult(batch);
        var root = ParseJson(result);

        root.GetProperty("timedOut").GetBoolean().ShouldBeTrue();
    }

    // ── Empty batch ─────────────────────────────────────────────────

    [Fact]
    public void FormatBatchResult_EmptySteps_ReturnsEmptyArrayAndZeroSummary()
    {
        var batch = new BatchResult(Steps: [], TotalElapsedMs: 0, TimedOut: false);

        var result = McpResultBuilder.FormatBatchResult(batch);
        var root = ParseJson(result);

        root.GetProperty("steps").GetArrayLength().ShouldBe(0);

        var summary = root.GetProperty("summary");
        summary.GetProperty("total").GetInt32().ShouldBe(0);
        summary.GetProperty("succeeded").GetInt32().ShouldBe(0);
        summary.GetProperty("failed").GetInt32().ShouldBe(0);
        summary.GetProperty("skipped").GetInt32().ShouldBe(0);
    }

    // ── Non-JSON output ─────────────────────────────────────────────

    [Fact]
    public void FormatBatchResult_NonJsonOutput_EmitsAsString()
    {
        var batch = new BatchResult(
            Steps:
            [
                new StepResult(0, "twig_status", StepStatus.Succeeded, "plain text output", null, 5)
            ],
            TotalElapsedMs: 5,
            TimedOut: false);

        var result = McpResultBuilder.FormatBatchResult(batch);
        var root = ParseJson(result);

        // Non-JSON output is emitted as a string value
        var output = root.GetProperty("steps")[0].GetProperty("output");
        output.ValueKind.ShouldBe(JsonValueKind.String);
        output.GetString().ShouldBe("plain text output");
    }

    // ── Error field only present on failed steps ────────────────────

    [Fact]
    public void FormatBatchResult_SucceededStep_NoErrorField()
    {
        var batch = new BatchResult(
            Steps:
            [
                new StepResult(0, "twig_set", StepStatus.Succeeded, "{\"ok\":true}", null, 3)
            ],
            TotalElapsedMs: 3,
            TimedOut: false);

        var result = McpResultBuilder.FormatBatchResult(batch);
        var root = ParseJson(result);

        // Succeeded steps should NOT have an "error" field
        root.GetProperty("steps")[0].TryGetProperty("error", out _).ShouldBeFalse();
    }
}
