using System.Text.RegularExpressions;

namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Parses raw strings for <c>{{steps.N.field.path}}</c> mustache-style template
/// expressions and produces <see cref="TemplateString"/> instances with ordered
/// segments for later resolution by <c>TemplateResolver</c>.
/// </summary>
internal static partial class TemplateParser
{
    // Matches {{steps.N.dotted.path}} — N is a non-negative integer,
    // path is one or more dot-separated identifiers.
    [GeneratedRegex(@"\{\{steps\.(\d{1,10})\.([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\}\}")]
    private static partial Regex TemplatePattern();

    /// <summary>
    /// Parses <paramref name="input"/> for template expressions and returns a
    /// <see cref="TemplateString"/> containing an ordered list of literal and
    /// expression segments.
    /// </summary>
    public static TemplateString Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var matches = TemplatePattern().Matches(input);

        if (matches.Count == 0)
        {
            // Pure literal — no template expressions found.
            return new TemplateString(
                Segments: input.Length == 0
                    ? []
                    : [new LiteralSegment(input)],
                Raw: input,
                IsFullExpression: false);
        }

        var segments = new List<TemplateSegment>();
        var cursor = 0;

        foreach (Match match in matches)
        {
            // Emit any literal text before this match.
            if (match.Index > cursor)
            {
                segments.Add(new LiteralSegment(input[cursor..match.Index]));
            }

            if (!int.TryParse(match.Groups[1].ValueSpan, out var stepIndex))
            {
                // Digit sequence matched by regex but overflows Int32 — treat match text as literal.
                segments.Add(new LiteralSegment(match.Value));
                cursor = match.Index + match.Length;
                continue;
            }

            var fieldPath = match.Groups[2].Value.Split('.');
            var fullPlaceholder = match.Value;

            var expr = new TemplateExpression(stepIndex, fieldPath, fullPlaceholder);
            segments.Add(new ExpressionSegment(expr));

            cursor = match.Index + match.Length;
        }

        // Emit any trailing literal text after the last match.
        if (cursor < input.Length)
        {
            segments.Add(new LiteralSegment(input[cursor..]));
        }

        // IsFullExpression is true when the entire input is exactly one expression
        // with no surrounding text.
        var isFullExpression = segments.Count == 1 && segments[0] is ExpressionSegment;

        return new TemplateString(segments, Raw: input, IsFullExpression: isFullExpression);
    }

    /// <summary>
    /// Extracts all <see cref="TemplateExpression"/> instances from the input string
    /// without building the full segment list. Useful for validation passes that only
    /// need to know which steps are referenced.
    /// </summary>
    public static IReadOnlyList<TemplateExpression> ExtractExpressions(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var parsed = Parse(input);
        if (!parsed.HasExpressions) return [];
        var exprs = new List<TemplateExpression>(parsed.Segments.Count);
        foreach (var seg in parsed.Segments)
            if (seg is ExpressionSegment es) exprs.Add(es.Expr);
        return exprs;
    }
}
