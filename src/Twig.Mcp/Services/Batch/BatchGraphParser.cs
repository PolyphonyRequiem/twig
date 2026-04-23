using System.Text.Json;
using Twig.Domain.Common;

namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Parses a JSON graph string into a validated <see cref="BatchGraph"/>.
/// Uses <see cref="JsonDocument"/> for AOT-safe, reflection-free deserialization.
/// Performs all safety validation at parse time: structural well-formedness,
/// depth limit, operation count, and recursive batch ban.
/// </summary>
internal static class BatchGraphParser
{
    private const string TypeProperty = "type";
    private const string StepsProperty = "steps";
    private const string ToolProperty = "tool";
    private const string ArgsProperty = "args";

    private const string StepType = "step";
    private const string SequenceType = "sequence";
    private const string ParallelType = "parallel";

    /// <summary>
    /// Parses a JSON string into a validated <see cref="BatchGraph"/>.
    /// </summary>
    /// <param name="json">The raw JSON graph string.</param>
    /// <returns>A success result containing the parsed graph, or a failure with an error message.</returns>
    public static Result<BatchGraph> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Result<BatchGraph>.Fail("Graph JSON is empty or whitespace.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return Result<BatchGraph>.Fail($"Invalid JSON: {ex.Message}");
        }

        using (doc)
        {
            var stepIndex = 0;
            var maxDepthSeen = 0;
            var result = ParseNode(doc.RootElement, depth: 0, ref stepIndex, ref maxDepthSeen);

            if (!result.IsSuccess)
            {
                return Result<BatchGraph>.Fail(result.Error);
            }

            return Result<BatchGraph>.Ok(new BatchGraph(result.Value, stepIndex, maxDepthSeen));
        }
    }

    private static Result<BatchNode> ParseNode(
        JsonElement element,
        int depth,
        ref int stepIndex,
        ref int maxDepthSeen)
    {
        if (depth > BatchConstants.MaxDepth)
        {
            return Result<BatchNode>.Fail(
                $"Graph exceeds maximum nesting depth of {BatchConstants.MaxDepth}.");
        }

        if (depth > maxDepthSeen)
        {
            maxDepthSeen = depth;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return Result<BatchNode>.Fail(
                $"Expected a JSON object for graph node at depth {depth}, got {element.ValueKind}.");
        }

        if (!element.TryGetProperty(TypeProperty, out var typeProp) ||
            typeProp.ValueKind != JsonValueKind.String)
        {
            return Result<BatchNode>.Fail(
                "Node is missing required 'type' property or it is not a string.");
        }

        var nodeType = typeProp.GetString()!;

        return nodeType switch
        {
            StepType => ParseStepNode(element, ref stepIndex),
            SequenceType => ParseContainerNode(element, depth, isParallel: false, ref stepIndex, ref maxDepthSeen),
            ParallelType => ParseContainerNode(element, depth, isParallel: true, ref stepIndex, ref maxDepthSeen),
            _ => Result<BatchNode>.Fail(
                $"Unknown node type '{nodeType}'. Valid types are: step, sequence, parallel.")
        };
    }

    private static Result<BatchNode> ParseStepNode(
        JsonElement element,
        ref int stepIndex)
    {
        if (stepIndex >= BatchConstants.MaxOperations)
        {
            return Result<BatchNode>.Fail(
                $"Graph exceeds maximum of {BatchConstants.MaxOperations} step operations.");
        }

        if (!element.TryGetProperty(ToolProperty, out var toolProp) ||
            toolProp.ValueKind != JsonValueKind.String)
        {
            return Result<BatchNode>.Fail(
                "Step node is missing required 'tool' property or it is not a string.");
        }

        var toolName = toolProp.GetString()!;

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Result<BatchNode>.Fail("Step node 'tool' property must not be empty.");
        }

        if (string.Equals(toolName, BatchConstants.BatchToolName, StringComparison.OrdinalIgnoreCase))
        {
            return Result<BatchNode>.Fail(
                $"Recursive batch calls are not allowed. Step at index {stepIndex} " +
                $"references tool '{BatchConstants.BatchToolName}'.");
        }

        var arguments = new Dictionary<string, object?>();

        if (element.TryGetProperty(ArgsProperty, out var argsProp))
        {
            if (argsProp.ValueKind != JsonValueKind.Object)
            {
                return Result<BatchNode>.Fail(
                    $"Step '{toolName}' has 'args' that is not a JSON object.");
            }

            foreach (var prop in argsProp.EnumerateObject())
            {
                arguments[prop.Name] = ConvertJsonValue(prop.Value);
            }
        }

        var node = new StepNode(stepIndex, toolName, arguments);
        stepIndex++;
        return Result<BatchNode>.Ok(node);
    }

    private static Result<BatchNode> ParseContainerNode(
        JsonElement element,
        int depth,
        bool isParallel,
        ref int stepIndex,
        ref int maxDepthSeen)
    {
        if (!element.TryGetProperty(StepsProperty, out var stepsProp) ||
            stepsProp.ValueKind != JsonValueKind.Array)
        {
            var containerType = isParallel ? "Parallel" : "Sequence";
            return Result<BatchNode>.Fail(
                $"{containerType} node is missing required 'steps' array.");
        }

        var children = new List<BatchNode>();

        foreach (var childElement in stepsProp.EnumerateArray())
        {
            var childResult = ParseNode(childElement, depth + 1, ref stepIndex, ref maxDepthSeen);

            if (!childResult.IsSuccess)
            {
                return Result<BatchNode>.Fail(childResult.Error);
            }

            children.Add(childResult.Value);
        }

        BatchNode node = isParallel
            ? new ParallelNode(children)
            : new SequenceNode(children);

        return Result<BatchNode>.Ok(node);
    }

    /// <summary>
    /// Converts a <see cref="JsonElement"/> to a .NET object suitable for tool arguments.
    /// </summary>
    private static object? ConvertJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt32(out var i) => i,
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        // For nested objects/arrays, preserve as raw JSON string for downstream handling
        _ => element.GetRawText()
    };
}
