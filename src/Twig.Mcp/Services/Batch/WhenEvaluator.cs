namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Evaluates <c>when</c> guard expressions for conditional batch steps.
/// <para>
/// Supports a minimal expression surface — equality (<c>==</c>) and inequality (<c>!=</c>)
/// comparisons only. Template references (<c>{{steps.N.field}}</c>) in the expression are
/// resolved before evaluation using <see cref="TemplateResolver"/>.
/// </para>
/// <para>
/// Expression format: <c>"resolved_value == 'literal'"</c> or <c>"resolved_value != 'literal'"</c>.
/// Both single-quoted and unquoted literals are supported. Comparisons are case-insensitive.
/// </para>
/// <para>
/// Boolean shorthand: A resolved expression that is exactly <c>"true"</c> or <c>"false"</c>
/// (case-insensitive) without an operator evaluates directly to its boolean value.
/// Any other single-value expression (no operator) evaluates to <c>true</c> if non-empty.
/// </para>
/// </summary>
internal static class WhenEvaluator
{
    private static readonly string[] EqualityOperators = ["==", "!="];

    /// <summary>
    /// Evaluates a <c>when</c> expression against completed step results.
    /// Returns <c>true</c> if the step should execute, <c>false</c> if it should be skipped.
    /// </summary>
    /// <param name="whenExpression">The raw <c>when</c> expression (may contain template references).</param>
    /// <param name="completedSteps">Snapshot of completed step results for template resolution.</param>
    /// <returns><c>true</c> if the condition is met and the step should execute.</returns>
    /// <exception cref="WhenEvaluationException">
    /// Thrown when the expression contains an unsupported operator or cannot be parsed.
    /// </exception>
    public static bool Evaluate(string whenExpression, StepResult?[] completedSteps)
    {
        ArgumentNullException.ThrowIfNull(whenExpression);
        ArgumentNullException.ThrowIfNull(completedSteps);

        // Resolve any {{steps.N.field}} templates in the expression.
        var resolved = ResolveTemplates(whenExpression, completedSteps);

        // Try to find an equality/inequality operator.
        foreach (var op in EqualityOperators)
        {
            var opIndex = FindOperator(resolved, op);
            if (opIndex >= 0)
            {
                var left = resolved[..opIndex].Trim();
                var right = resolved[(opIndex + op.Length)..].Trim();

                left = Unquote(left);
                right = Unquote(right);

                var areEqual = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
                return op == "==" ? areEqual : !areEqual;
            }
        }

        // No operator found — treat as boolean shorthand.
        var trimmed = resolved.Trim();
        return EvaluateAsBoolean(trimmed);
    }

    /// <summary>
    /// Resolves template expressions within a <c>when</c> expression string.
    /// Each <c>{{steps.N.field}}</c> is replaced with its string representation.
    /// </summary>
    private static string ResolveTemplates(string expression, StepResult?[] completedSteps)
    {
        var parsed = TemplateParser.Parse(expression);
        if (!parsed.HasExpressions)
            return expression;

        var result = TemplateResolver.ResolveTemplateString(parsed, completedSteps);
        return result?.ToString() ?? "";
    }

    /// <summary>
    /// Finds the index of an operator in the expression, skipping occurrences inside
    /// single-quoted strings and <c>{{...}}</c> template delimiters.
    /// </summary>
    private static int FindOperator(string expression, string op)
    {
        var inQuote = false;
        var braceDepth = 0;

        for (var i = 0; i <= expression.Length - op.Length; i++)
        {
            var ch = expression[i];

            if (ch == '\'')
            {
                inQuote = !inQuote;
                continue;
            }

            if (inQuote) continue;

            if (ch == '{' && i + 1 < expression.Length && expression[i + 1] == '{')
            {
                braceDepth++;
                i++; // Skip second brace.
                continue;
            }

            if (ch == '}' && i + 1 < expression.Length && expression[i + 1] == '}')
            {
                braceDepth = Math.Max(0, braceDepth - 1);
                i++;
                continue;
            }

            if (braceDepth > 0) continue;

            if (expression.AsSpan(i, op.Length).SequenceEqual(op.AsSpan()))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Removes surrounding single quotes from a value if present.
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];
        return value;
    }

    /// <summary>
    /// Evaluates a resolved expression without operators as a boolean.
    /// <c>"true"</c> → true, <c>"false"</c> → false, empty → false, any other → true.
    /// </summary>
    private static bool EvaluateAsBoolean(string value)
    {
        if (value.Length == 0) return false;
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return true; // Non-empty, non-boolean values are truthy.
    }
}

/// <summary>
/// Thrown when a <c>when</c> guard expression cannot be evaluated.
/// </summary>
internal sealed class WhenEvaluationException(string expression, string reason)
    : InvalidOperationException($"When expression evaluation failed for '{expression}': {reason}")
{
    public string Expression { get; } = expression;
}
