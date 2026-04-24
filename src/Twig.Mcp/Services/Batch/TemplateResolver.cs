using System.Text;
using System.Text.Json;

namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Resolves <c>{{steps.N.path}}</c> mustache-style template expressions in batch step
/// arguments by navigating prior step output JSON with <see cref="System.Text.Json"/>.
/// <para>
/// <b>Full-expression arguments</b> (the entire value is one <c>{{...}}</c> placeholder)
/// preserve the resolved JSON type — integers stay integers, booleans stay booleans.
/// This is critical for parameters like <c>parentId</c> and <c>id</c>.
/// </para>
/// <para>
/// <b>Partial-expression arguments</b> (template mixed with literal text) are resolved
/// to concatenated strings using JSON-to-string coercion rules.
/// </para>
/// </summary>
internal static class TemplateResolver
{
    /// <summary>
    /// Resolves all template expressions in a step's argument dictionary by substituting
    /// <c>{{steps.N.path}}</c> references with values from completed step outputs.
    /// Non-string values and strings without template expressions are passed through unchanged.
    /// </summary>
    /// <param name="arguments">The step's argument dictionary (may contain template strings).</param>
    /// <param name="completedSteps">Array of completed step results indexed by global step index.</param>
    /// <returns>A new dictionary with all template expressions resolved to concrete values.</returns>
    /// <exception cref="TemplateResolutionException">
    /// Thrown when a template references a step that has not completed successfully,
    /// when a property path cannot be navigated, or when a non-scalar JSON value is encountered.
    /// </exception>
    public static Dictionary<string, object?> Resolve(
        Dictionary<string, object?> arguments,
        StepResult?[] completedSteps)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(completedSteps);

        var resolved = new Dictionary<string, object?>(arguments.Count);

        foreach (var (key, value) in arguments)
        {
            if (value is not string stringValue)
            {
                resolved[key] = value;
                continue;
            }

            var template = TemplateParser.Parse(stringValue);

            if (!template.HasExpressions)
            {
                resolved[key] = value;
                continue;
            }

            resolved[key] = ResolveTemplateString(template, completedSteps);
        }

        return resolved;
    }

    /// <summary>
    /// Resolves a single <see cref="TemplateString"/> against step outputs.
    /// For full expressions, returns the native JSON type. For partial expressions,
    /// returns a concatenated string.
    /// </summary>
    internal static object? ResolveTemplateString(
        TemplateString template,
        StepResult?[] completedSteps)
    {
        if (template.IsFullExpression)
        {
            var expr = ((ExpressionSegment)template.Segments[0]).Expr;
            return ResolveExpressionTyped(expr, completedSteps);
        }

        // Partial template: concatenate all segments as strings.
        var sb = new StringBuilder();
        foreach (var segment in template.Segments)
        {
            switch (segment)
            {
                case LiteralSegment literal:
                    sb.Append(literal.Text);
                    break;
                case ExpressionSegment exprSeg:
                    sb.Append(ResolveExpressionToString(exprSeg.Expr, completedSteps));
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolves a template expression to its native JSON type. Used when the entire
    /// argument value is a single template expression (type preservation).
    /// </summary>
    private static object? ResolveExpressionTyped(
        TemplateExpression expr,
        StepResult?[] completedSteps)
    {
        var json = GetStepOutputJson(expr, completedSteps);

        using var doc = JsonDocument.Parse(json);
        var element = NavigateToElement(doc.RootElement, expr);

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => CoerceNumber(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object or JsonValueKind.Array =>
                throw new TemplateResolutionException(
                    expr.FullPlaceholder,
                    $"Cannot use {FormatValueKind(element.ValueKind)} as a template value; " +
                    "only scalar values (string, number, boolean, null) are supported."),
            _ => throw new TemplateResolutionException(
                    expr.FullPlaceholder,
                    $"Unsupported JSON value kind: {element.ValueKind}.")
        };
    }

    /// <summary>
    /// Resolves a template expression to a string. Used for partial templates where
    /// the resolved value is interpolated into surrounding text.
    /// </summary>
    private static string ResolveExpressionToString(
        TemplateExpression expr,
        StepResult?[] completedSteps)
    {
        var json = GetStepOutputJson(expr, completedSteps);

        using var doc = JsonDocument.Parse(json);
        var element = NavigateToElement(doc.RootElement, expr);

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            JsonValueKind.Object or JsonValueKind.Array =>
                throw new TemplateResolutionException(
                    expr.FullPlaceholder,
                    $"Cannot interpolate {FormatValueKind(element.ValueKind)} into a string; " +
                    "only scalar values (string, number, boolean, null) are supported."),
            _ => throw new TemplateResolutionException(
                    expr.FullPlaceholder,
                    $"Unsupported JSON value kind: {element.ValueKind}.")
        };
    }

    /// <summary>
    /// Retrieves and validates the JSON output for the referenced step.
    /// </summary>
    private static string GetStepOutputJson(
        TemplateExpression expr,
        StepResult?[] completedSteps)
    {
        if (expr.StepIndex < 0 || expr.StepIndex >= completedSteps.Length)
        {
            throw new TemplateResolutionException(
                expr.FullPlaceholder,
                $"Step index {expr.StepIndex} is out of range (valid: 0..{completedSteps.Length - 1}).");
        }

        var stepResult = completedSteps[expr.StepIndex];
        if (stepResult is null)
        {
            throw new TemplateResolutionException(
                expr.FullPlaceholder,
                $"Step {expr.StepIndex} has not completed yet.");
        }

        if (stepResult.Status != StepStatus.Succeeded)
        {
            throw new TemplateResolutionException(
                expr.FullPlaceholder,
                $"Step {expr.StepIndex} did not complete successfully (status: {stepResult.Status}).");
        }

        if (stepResult.OutputJson is null)
        {
            throw new TemplateResolutionException(
                expr.FullPlaceholder,
                $"Step {expr.StepIndex} produced no output.");
        }

        return stepResult.OutputJson;
    }

    /// <summary>
    /// Navigates a <see cref="JsonElement"/> tree using the expression's dot-separated field path.
    /// </summary>
    private static JsonElement NavigateToElement(JsonElement root, TemplateExpression expr)
    {
        var current = root;

        for (var i = 0; i < expr.FieldPath.Length; i++)
        {
            var segment = expr.FieldPath[i];

            if (current.ValueKind != JsonValueKind.Object)
            {
                var traversed = string.Join('.', expr.FieldPath.AsSpan(0, i));
                throw new TemplateResolutionException(
                    expr.FullPlaceholder,
                    $"Cannot navigate to '{segment}': the value at " +
                    (traversed.Length > 0 ? $"'{traversed}'" : "root") +
                    $" is {FormatValueKind(current.ValueKind)}, not an object.");
            }

            if (!current.TryGetProperty(segment, out var child))
            {
                var available = GetAvailableProperties(current);
                throw new TemplateResolutionException(
                    expr.FullPlaceholder,
                    $"Property '{segment}' not found in step {expr.StepIndex} output. " +
                    $"Available properties: {available}");
            }

            current = child;
        }

        return current;
    }

    /// <summary>
    /// Coerces a JSON number to the narrowest .NET numeric type that can represent it losslessly.
    /// Tries <c>int</c> → <c>long</c> → <c>double</c>.
    /// </summary>
    private static object CoerceNumber(JsonElement element)
    {
        if (element.TryGetInt32(out var intVal))
            return intVal;
        if (element.TryGetInt64(out var longVal))
            return longVal;
        return element.GetDouble();
    }

    private static string FormatValueKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => kind.ToString()
    };

    private static string GetAvailableProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return "(not an object)";

        var properties = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            properties.Add(property.Name);
            if (properties.Count >= 10)
            {
                properties.Add("...");
                break;
            }
        }

        return properties.Count > 0
            ? string.Join(", ", properties)
            : "(empty object)";
    }
}
