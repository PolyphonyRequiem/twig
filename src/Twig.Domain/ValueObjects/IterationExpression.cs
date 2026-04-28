using System.Globalization;
using Twig.Domain.Common;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Distinguishes between relative (@current±N) and absolute iteration expressions.
/// </summary>
public enum ExpressionKind
{
    Relative,
    Absolute
}

/// <summary>
/// Validated, immutable value object representing a sprint iteration expression.
/// Relative expressions use the form <c>@current</c>, <c>@current-N</c>, or <c>@current+N</c>.
/// Absolute expressions are literal iteration paths (e.g., <c>Project\Sprint 5</c>).
/// </summary>
public readonly record struct IterationExpression
{
    /// <summary>The original expression string (e.g., "@current-1", "Project\Sprint 5").</summary>
    public string Raw { get; }

    /// <summary>Whether this is a relative or absolute expression.</summary>
    public ExpressionKind Kind { get; }

    /// <summary>
    /// The offset from the current iteration. Only meaningful when <see cref="Kind"/> is <see cref="ExpressionKind.Relative"/>.
    /// 0 for @current, -1 for @current-1, +1 for @current+1, etc.
    /// </summary>
    public int Offset { get; }

    /// <summary>Returns <c>true</c> when this is a relative expression.</summary>
    public bool IsRelative => Kind == ExpressionKind.Relative;

    private IterationExpression(string raw, ExpressionKind kind, int offset)
    {
        Raw = raw;
        Kind = kind;
        Offset = offset;
    }

    /// <summary>
    /// Parses a raw expression string into an <see cref="IterationExpression"/>.
    /// </summary>
    /// <remarks>
    /// Parsing rules:
    /// <list type="bullet">
    /// <item><c>@current</c> → Relative, offset 0</item>
    /// <item><c>@current-N</c> → Relative, offset -N (N is a positive integer)</item>
    /// <item><c>@current+N</c> → Relative, offset +N</item>
    /// <item>Anything else → Absolute, treated as a literal iteration path</item>
    /// <item>Empty/whitespace → error</item>
    /// </list>
    /// </remarks>
    public static Result<IterationExpression> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Result.Fail<IterationExpression>("Sprint expression cannot be empty.");

        var trimmed = raw.Trim();

        if (!trimmed.StartsWith('@'))
            return Result.Ok(new IterationExpression(trimmed, ExpressionKind.Absolute, 0));

        // Must start with @current (case-insensitive)
        if (!trimmed.StartsWith("@current", StringComparison.OrdinalIgnoreCase))
            return Result.Fail<IterationExpression>($"Unknown relative expression '{trimmed}'. Expected '@current', '@current-N', or '@current+N'.");

        var remainder = trimmed.AsSpan()["@current".Length..];

        if (remainder.IsEmpty)
            return Result.Ok(new IterationExpression(trimmed, ExpressionKind.Relative, 0));

        if (remainder[0] != '+' && remainder[0] != '-')
            return Result.Fail<IterationExpression>($"Invalid relative expression '{trimmed}'. Expected '+' or '-' after '@current'.");

        var sign = remainder[0] == '+' ? 1 : -1;
        var digits = remainder[1..];

        if (digits.IsEmpty)
            return Result.Fail<IterationExpression>($"Invalid relative expression '{trimmed}'. Expected a number after '{remainder[0]}'.");

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return Result.Fail<IterationExpression>($"Invalid relative expression '{trimmed}'. Offset must be a positive integer.");

        return Result.Ok(new IterationExpression(trimmed, ExpressionKind.Relative, sign * n));
    }

    /// <inheritdoc/>
    public override string ToString() => Raw;
}
