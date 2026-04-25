namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Represents a single parsed <c>{{steps.N.path}}</c> template reference
/// extracted from a parameter value. Used by the template parser and resolver
/// to locate step outputs during batch execution.
/// </summary>
internal sealed record TemplateExpression(
    int StepIndex,              // The step being referenced (0-based global index)
    string[] FieldPath,         // e.g., ["item", "id"] for steps.0.item.id
    string FullPlaceholder)     // Original {{steps.0.field}} text including delimiters
{
    public bool Equals(TemplateExpression? other) =>
        other is not null &&
        StepIndex == other.StepIndex &&
        FieldPath.AsSpan().SequenceEqual(other.FieldPath) &&
        FullPlaceholder == other.FullPlaceholder;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(StepIndex);
        foreach (var segment in FieldPath)
            hash.Add(segment);
        hash.Add(FullPlaceholder);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Base type for segments within a <see cref="TemplateString"/>.
/// Each segment is either literal text or a template expression reference.
/// </summary>
internal abstract record TemplateSegment;

/// <summary>
/// A segment of literal (non-template) text within a <see cref="TemplateString"/>.
/// </summary>
internal sealed record LiteralSegment(string Text) : TemplateSegment;

/// <summary>
/// A segment containing a <see cref="TemplateExpression"/> reference within a <see cref="TemplateString"/>.
/// </summary>
internal sealed record ExpressionSegment(TemplateExpression Expr) : TemplateSegment;

/// <summary>
/// Represents a string value that may contain zero or more
/// <c>{{steps.N.path}}</c> template expressions intermixed with literal text.
/// The <see cref="Segments"/> list preserves ordering of literals and expressions
/// so the resolver can iterate them sequentially to build the output string.
/// </summary>
internal sealed record TemplateString(
    IReadOnlyList<TemplateSegment> Segments,
    string Raw,                     // Original unparsed value
    bool IsFullExpression)          // True when the entire value is one {{...}} expression
{
    /// <summary>
    /// Returns <c>true</c> when the value contains at least one template expression.
    /// </summary>
    public bool HasExpressions
    {
        get
        {
            for (var i = 0; i < Segments.Count; i++)
                if (Segments[i] is ExpressionSegment) return true;
            return false;
        }
    }

    public bool Equals(TemplateString? other) =>
        other is not null &&
        Raw == other.Raw &&
        IsFullExpression == other.IsFullExpression &&
        Segments.SequenceEqual(other.Segments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Raw);
        hash.Add(IsFullExpression);
        foreach (var seg in Segments)
            hash.Add(seg);
        return hash.ToHashCode();
    }
}
