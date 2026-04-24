using System.Diagnostics;
using ModelContextProtocol.Protocol;

namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Executes a validated <see cref="BatchGraph"/> recursively with sequence/parallel semantics.
/// <para>
/// <b>SequenceNode</b>: Children execute left-to-right. On failure, remaining children are skipped.<br/>
/// <b>ParallelNode</b>: Children execute concurrently via <c>Task.WhenAll</c>. All children run to completion.<br/>
/// <b>StepNode</b>: Dispatches a single tool call and captures the result with timing.
/// </para>
/// Per-batch timeout is enforced via a linked <see cref="CancellationTokenSource"/>.
/// </summary>
internal sealed class BatchExecutionEngine(IToolDispatcher dispatcher)
{
    /// <summary>
    /// Executes the given batch graph with the specified timeout and workspace override.
    /// </summary>
    /// <param name="graph">A validated batch graph produced by <see cref="BatchGraphParser"/>.</param>
    /// <param name="timeout">Maximum duration for the entire batch execution.</param>
    /// <param name="workspaceOverride">Batch-level workspace override applied to steps without an explicit workspace arg.</param>
    /// <param name="ct">External cancellation token.</param>
    /// <returns>A <see cref="BatchResult"/> containing per-step results and aggregate timing.</returns>
    public async Task<BatchResult> ExecuteAsync(
        BatchGraph graph,
        TimeSpan timeout,
        string? workspaceOverride,
        CancellationToken ct)
    {
        var store = new StepOutputStore(graph.TotalStepCount);
        var batchStopwatch = Stopwatch.StartNew();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var timedOut = false;

        try
        {
            await ExecuteNodeAsync(graph.Root, store, workspaceOverride, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Batch timeout fired (not external cancellation).
            timedOut = true;
        }

        batchStopwatch.Stop();

        // Fill any remaining null slots with Skipped results.
        store.FillSkipped(graph.Root, "Skipped due to timeout or prior failure.");

        return new BatchResult(
            store.ToResultList(),
            batchStopwatch.ElapsedMilliseconds,
            timedOut);
    }

    private async Task ExecuteNodeAsync(
        BatchNode node,
        StepOutputStore store,
        string? workspaceOverride,
        CancellationToken ct)
    {
        switch (node)
        {
            case StepNode step:
                await ExecuteStepAsync(step, store, workspaceOverride, ct).ConfigureAwait(false);
                break;

            case SequenceNode sequence:
                await ExecuteSequenceAsync(sequence, store, workspaceOverride, ct).ConfigureAwait(false);
                break;

            case ParallelNode parallel:
                await ExecuteParallelAsync(parallel, store, workspaceOverride, ct).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Unknown batch node type: {node.GetType().Name}");
        }
    }

    private async Task ExecuteStepAsync(
        StepNode step,
        StepOutputStore store,
        string? workspaceOverride,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Resolve template expressions in arguments using prior step outputs.
            var resolvedArgs = TemplateResolver.Resolve(step.Arguments, store.GetSnapshot());

            var callResult = await dispatcher.DispatchAsync(
                step.ToolName, resolvedArgs, workspaceOverride, ct).ConfigureAwait(false);

            stopwatch.Stop();

            var outputJson = ExtractOutputJson(callResult);
            var isError = callResult.IsError == true;

            var result = new StepResult(
                step.GlobalIndex,
                step.ToolName,
                isError ? StepStatus.Failed : StepStatus.Succeeded,
                isError ? null : outputJson,
                isError ? outputJson : null,
                stopwatch.ElapsedMilliseconds);

            store.Record(step.GlobalIndex, result);
        }
        catch (TemplateResolutionException ex)
        {
            stopwatch.Stop();
            store.Record(step.GlobalIndex, new StepResult(
                step.GlobalIndex,
                step.ToolName,
                StepStatus.Failed,
                null,
                ex.Message,
                stopwatch.ElapsedMilliseconds));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            store.Record(step.GlobalIndex, new StepResult(
                step.GlobalIndex,
                step.ToolName,
                StepStatus.Skipped,
                null,
                "Operation was cancelled.",
                stopwatch.ElapsedMilliseconds));
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            store.Record(step.GlobalIndex, new StepResult(
                step.GlobalIndex,
                step.ToolName,
                StepStatus.Failed,
                null,
                ex.Message,
                stopwatch.ElapsedMilliseconds));
        }
    }

    private async Task ExecuteSequenceAsync(
        SequenceNode sequence,
        StepOutputStore store,
        string? workspaceOverride,
        CancellationToken ct)
    {
        var failed = false;

        foreach (var child in sequence.Children)
        {
            if (failed)
            {
                // Skip remaining children after a failure.
                store.FillSkipped(child, "Skipped due to prior step failure.");
                continue;
            }

            await ExecuteNodeAsync(child, store, workspaceOverride, ct).ConfigureAwait(false);

            // Check if any step in this child subtree failed → fail-fast.
            if (HasFailure(child, store))
            {
                failed = true;
            }
        }
    }

    private async Task ExecuteParallelAsync(
        ParallelNode parallel,
        StepOutputStore store,
        string? workspaceOverride,
        CancellationToken ct)
    {
        var tasks = new Task[parallel.Children.Count];

        for (var i = 0; i < parallel.Children.Count; i++)
        {
            var child = parallel.Children[i];
            tasks[i] = ExecuteNodeAsync(child, store, workspaceOverride, ct);
        }

        // Await all tasks, collecting exceptions. We don't fail-fast for parallel blocks.
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Always propagate — timeout or external — let ExecuteAsync decide.
            throw;
        }
        catch
        {
            // Individual step failures are captured in the store.
        }
    }

    /// <summary>
    /// Checks if any step node in the subtree has a Failed result.
    /// </summary>
    private static bool HasFailure(BatchNode node, StepOutputStore store) => node switch
    {
        StepNode step => store.GetResult(step.GlobalIndex)?.Status == StepStatus.Failed,
        SequenceNode seq => seq.Children.Any(c => HasFailure(c, store)),
        ParallelNode par => par.Children.Any(c => HasFailure(c, store)),
        _ => false
    };

    /// <summary>
    /// Extracts the text content from a <see cref="CallToolResult"/> as a JSON string.
    /// Only the first content block is used; additional blocks are discarded.
    /// Current MCP tools emit a single TextContentBlock, so this is safe.
    /// </summary>
    private static string? ExtractOutputJson(CallToolResult result)
    {
        if (result.Content is not { Count: > 0 })
            return null;

        var first = result.Content[0];
        if (first is TextContentBlock textBlock)
            return textBlock.Text;

        return null;
    }
}
