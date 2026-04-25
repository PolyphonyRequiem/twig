using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Mcp.Services;
using Twig.Mcp.Services.Batch;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tool for batch execution: twig_batch.
/// Accepts a JSON graph of sequence/parallel/step nodes and executes them
/// through the <see cref="BatchExecutionEngine"/>.
/// </summary>
[McpServerToolType]
public sealed class BatchTools(IToolDispatcher dispatcher)
{
    [McpServerTool(Name = "twig_batch"), Description(
        "Execute multiple twig tool calls as a single batch request. " +
        "Accepts a JSON graph of 'sequence', 'parallel', and 'step' nodes. " +
        "Sequential steps execute in order with fail-fast semantics. " +
        "Parallel steps execute concurrently. " +
        "Max 50 operations, max 3 nesting levels, no recursive batch calls.")]
    public async Task<CallToolResult> Batch(
        [Description(
            "JSON string containing the batch execution graph. " +
            "Root node must be a sequence, parallel, or step. " +
            "Each step has 'type': 'step', 'tool': '<tool_name>', 'args': {<args>}. " +
            "Containers have 'type': 'sequence'|'parallel' and 'steps': [<children>]."
        )] string graph,
        [Description("Per-batch timeout in seconds (default: 120, max: 300).")] int? timeoutSeconds = null,
        [Description("Target workspace (format: \"org/project\"). Applied to steps without explicit workspace arg.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(graph))
            return McpResultBuilder.ToError("The 'graph' parameter is required and must contain a valid JSON batch graph.");

        var parseResult = BatchGraphParser.Parse(graph);

        if (!parseResult.IsSuccess)
            return McpResultBuilder.ToError($"Batch graph validation failed: {parseResult.Error}");

        var effectiveTimeout = ResolveTimeout(timeoutSeconds);

        var engine = new BatchExecutionEngine(dispatcher);
        var batchResult = await engine.ExecuteAsync(
            parseResult.Value,
            effectiveTimeout,
            workspace,
            ct);

        return McpResultBuilder.FormatBatchResult(batchResult);
    }

    private static TimeSpan ResolveTimeout(int? timeoutSeconds)
    {
        if (timeoutSeconds is null or <= 0)
            return TimeSpan.FromSeconds(BatchConstants.DefaultTimeoutSeconds);

        // Cap at 300 seconds to prevent runaway batches.
        var capped = Math.Min(timeoutSeconds.Value, 300);
        return TimeSpan.FromSeconds(capped);
    }
}
