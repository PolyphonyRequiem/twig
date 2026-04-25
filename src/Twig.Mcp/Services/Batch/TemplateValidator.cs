namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Represents a template validation error found during graph analysis.
/// </summary>
internal sealed record TemplateValidationError(
    int StepIndex,
    TemplateExpression Expression,
    string Reason);

/// <summary>
/// Validates <see cref="TemplateExpression"/> references within a <see cref="BatchGraph"/>
/// against execution ordering constraints. Rejects forward references (referencing a step
/// that has not yet executed) and parallel sibling references (referencing a step in the
/// same parallel execution group).
/// </summary>
internal static class TemplateValidator
{
    /// <summary>
    /// Validates all template expressions in the graph's step arguments.
    /// Returns an empty list when all references are valid.
    /// </summary>
    public static IReadOnlyList<TemplateValidationError> Validate(BatchGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var parallelSiblings = BuildParallelSiblingMap(graph.Root);

        var steps = new List<StepNode>();
        CollectSteps(graph.Root, steps);

        var errors = new List<TemplateValidationError>();

        foreach (var step in steps)
        {
            ValidateStep(step, parallelSiblings, errors);
        }

        return errors;
    }

    private static void ValidateStep(
        StepNode step,
        Dictionary<int, HashSet<int>> parallelSiblings,
        List<TemplateValidationError> errors)
    {
        foreach (var (_, value) in step.Arguments)
        {
            if (value is not string stringValue)
                continue;

            var expressions = TemplateParser.ExtractExpressions(stringValue);

            foreach (var expr in expressions)
            {
                if (expr.StepIndex >= step.GlobalIndex)
                {
                    errors.Add(new TemplateValidationError(
                        step.GlobalIndex,
                        expr,
                        $"Forward reference: step {step.GlobalIndex} references " +
                        $"step {expr.StepIndex} via '{expr.FullPlaceholder}', " +
                        $"but step {expr.StepIndex} has not executed yet."));
                }
                else if (parallelSiblings.TryGetValue(step.GlobalIndex, out var siblings) &&
                         siblings.Contains(expr.StepIndex))
                {
                    errors.Add(new TemplateValidationError(
                        step.GlobalIndex,
                        expr,
                        $"Parallel sibling reference: step {step.GlobalIndex} references " +
                        $"step {expr.StepIndex} via '{expr.FullPlaceholder}', " +
                        $"but both steps execute in the same parallel group."));
                }
            }
        }
    }

    /// <summary>
    /// Builds a mapping from each step index to the set of step indices that are
    /// parallel siblings (in different branches of the same <see cref="ParallelNode"/>).
    /// </summary>
    private static Dictionary<int, HashSet<int>> BuildParallelSiblingMap(BatchNode root)
    {
        var map = new Dictionary<int, HashSet<int>>();
        BuildParallelSiblingMapRecursive(root, map);
        return map;
    }

    private static void BuildParallelSiblingMapRecursive(
        BatchNode node,
        Dictionary<int, HashSet<int>> map)
    {
        switch (node)
        {
            case StepNode:
                break;

            case ParallelNode parallel:
                RegisterParallelSiblings(parallel, map);
                foreach (var child in parallel.Children)
                    BuildParallelSiblingMapRecursive(child, map);
                break;

            case SequenceNode sequence:
                foreach (var child in sequence.Children)
                    BuildParallelSiblingMapRecursive(child, map);
                break;
        }
    }

    private static void RegisterParallelSiblings(
        ParallelNode parallel,
        Dictionary<int, HashSet<int>> map)
    {
        var branches = new List<HashSet<int>>(parallel.Children.Count);
        foreach (var child in parallel.Children)
        {
            var branchIndices = new HashSet<int>();
            CollectStepIndices(child, branchIndices);
            branches.Add(branchIndices);
        }

        for (var i = 0; i < branches.Count; i++)
        {
            foreach (var stepIndex in branches[i])
            {
                if (!map.TryGetValue(stepIndex, out var siblings))
                {
                    siblings = [];
                    map[stepIndex] = siblings;
                }

                for (var j = 0; j < branches.Count; j++)
                {
                    if (i != j)
                        siblings.UnionWith(branches[j]);
                }
            }
        }
    }

    private static void CollectStepIndices(BatchNode node, HashSet<int> indices)
    {
        switch (node)
        {
            case StepNode step:
                indices.Add(step.GlobalIndex);
                break;
            case SequenceNode sequence:
                foreach (var child in sequence.Children)
                    CollectStepIndices(child, indices);
                break;
            case ParallelNode parallel:
                foreach (var child in parallel.Children)
                    CollectStepIndices(child, indices);
                break;
        }
    }

    private static void CollectSteps(BatchNode node, List<StepNode> steps)
    {
        switch (node)
        {
            case StepNode step:
                steps.Add(step);
                break;
            case SequenceNode sequence:
                foreach (var child in sequence.Children)
                    CollectSteps(child, steps);
                break;
            case ParallelNode parallel:
                foreach (var child in parallel.Children)
                    CollectSteps(child, steps);
                break;
        }
    }
}
