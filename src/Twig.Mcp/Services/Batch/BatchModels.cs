namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Abstract base for all batch execution graph nodes.
/// </summary>
internal abstract record BatchNode;

/// <summary>
/// A single tool invocation within a batch graph.
/// <paramref name="GlobalIndex"/> is the zero-based position assigned during
/// depth-first traversal of the graph — used for <c>{{steps.N.field}}</c> references.
/// </summary>
internal sealed record StepNode(
    int GlobalIndex,
    string ToolName,
    Dictionary<string, object?> Arguments) : BatchNode;

/// <summary>
/// An ordered list of child nodes executed sequentially with fail-fast semantics.
/// </summary>
internal sealed record SequenceNode(
    IReadOnlyList<BatchNode> Children) : BatchNode;

/// <summary>
/// A set of child nodes executed concurrently via <c>Task.WhenAll</c>.
/// </summary>
internal sealed record ParallelNode(
    IReadOnlyList<BatchNode> Children) : BatchNode;

/// <summary>
/// A fully parsed and validated batch execution graph.
/// </summary>
internal sealed record BatchGraph(
    BatchNode Root,
    int TotalStepCount,
    int MaxDepth);

/// <summary>
/// Execution status of an individual batch step.
/// </summary>
internal enum StepStatus
{
    Succeeded,
    Failed,
    Skipped
}

/// <summary>
/// The execution result of a single step within a batch.
/// </summary>
internal sealed record StepResult(
    int StepIndex,
    string ToolName,
    StepStatus Status,
    string? OutputJson,
    string? Error,
    long ElapsedMs);

/// <summary>
/// Aggregate result for an entire batch execution.
/// </summary>
internal sealed record BatchResult(
    IReadOnlyList<StepResult> Steps,
    long TotalElapsedMs,
    bool TimedOut);
