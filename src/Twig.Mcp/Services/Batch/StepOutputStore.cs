namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Thread-safe store for completed step results during batch execution.
/// <para>
/// Each slot in the store corresponds to a step's global index in the batch graph.
/// Writes use <see cref="Volatile.Write{T}(ref T, T)"/> to ensure visibility across
/// threads — parallel steps may read prior step outputs while their siblings are
/// still recording results.
/// </para>
/// <para>
/// <b>Concurrency model:</b> Each slot is written exactly once (by the step that owns it).
/// Reads may occur concurrently from any thread. <c>Volatile</c> fences guarantee that
/// a read on thread B sees the result written by thread A, provided the write happened-before
/// the read in program order (which the batch engine's sequence/parallel semantics ensure).
/// </para>
/// </summary>
internal sealed class StepOutputStore
{
    private readonly StepResult?[] _results;

    /// <summary>
    /// Creates a new store with the specified capacity (one slot per step in the batch).
    /// </summary>
    /// <param name="capacity">The total number of steps in the batch graph.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is negative.</exception>
    public StepOutputStore(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _results = new StepResult?[capacity];
    }

    /// <summary>
    /// The total number of step slots in this store.
    /// </summary>
    public int Capacity => _results.Length;

    /// <summary>
    /// Records a completed step result at the given index.
    /// Each index should be written at most once during a batch execution.
    /// </summary>
    /// <param name="stepIndex">The step's global index in the batch graph.</param>
    /// <param name="result">The step result to store.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="stepIndex"/> is out of range.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="result"/> is null.</exception>
    public void Record(int stepIndex, StepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateIndex(stepIndex);
        Volatile.Write(ref _results[stepIndex], result);
    }

    /// <summary>
    /// Retrieves the result for a given step, or <c>null</c> if not yet recorded.
    /// Safe to call concurrently from any thread.
    /// </summary>
    /// <param name="stepIndex">The step's global index in the batch graph.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="stepIndex"/> is out of range.</exception>
    public StepResult? GetResult(int stepIndex)
    {
        ValidateIndex(stepIndex);
        return Volatile.Read(ref _results[stepIndex]);
    }

    /// <summary>
    /// Creates a point-in-time snapshot of all step results for use by
    /// <see cref="TemplateResolver"/>. Each element is read with volatile
    /// semantics to ensure cross-thread visibility.
    /// </summary>
    public StepResult?[] GetSnapshot()
    {
        var snapshot = new StepResult?[_results.Length];
        for (var i = 0; i < _results.Length; i++)
            snapshot[i] = Volatile.Read(ref _results[i]);
        return snapshot;
    }

    /// <summary>
    /// Fills any unrecorded (null) slots in the store with a <see cref="StepStatus.Skipped"/>
    /// result. Called after batch execution completes to ensure every slot has a result.
    /// </summary>
    /// <param name="node">The root node of the batch graph subtree to fill.</param>
    /// <param name="reason">The skip reason message.</param>
    public void FillSkipped(BatchNode node, string reason)
    {
        switch (node)
        {
            case StepNode step:
                if (Volatile.Read(ref _results[step.GlobalIndex]) is null)
                {
                    Volatile.Write(ref _results[step.GlobalIndex],
                        new StepResult(step.GlobalIndex, step.ToolName, StepStatus.Skipped, null, reason, 0));
                }
                break;

            case SequenceNode seq:
                foreach (var child in seq.Children)
                    FillSkipped(child, reason);
                break;

            case ParallelNode par:
                foreach (var child in par.Children)
                    FillSkipped(child, reason);
                break;
        }
    }

    /// <summary>
    /// Returns all results as a list, in step-index order. Every slot must
    /// be non-null (call <see cref="FillSkipped"/> first).
    /// </summary>
    public IReadOnlyList<StepResult> ToResultList()
    {
        var list = new List<StepResult>(_results.Length);
        for (var i = 0; i < _results.Length; i++)
        {
            var result = Volatile.Read(ref _results[i]);
            list.Add(result ?? throw new InvalidOperationException(
                $"Step {i} has no result. Call FillSkipped before ToResultList."));
        }
        return list;
    }

    private void ValidateIndex(int stepIndex)
    {
        if ((uint)stepIndex >= (uint)_results.Length)
        {
            var message = _results.Length == 0
                ? "Store has no capacity (zero steps)."
                : $"Step index must be between 0 and {_results.Length - 1}.";

            throw new ArgumentOutOfRangeException(
                nameof(stepIndex),
                stepIndex,
                message);
        }
    }
}
