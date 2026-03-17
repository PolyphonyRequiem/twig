using Twig.Domain.Common;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Validated iteration path value object. Non-empty, backslash-separated segments.
/// </summary>
public readonly record struct IterationPath
{
    public string Value { get; }

    private IterationPath(string value) => Value = value;

    /// <summary>
    /// Parses a raw string into an <see cref="IterationPath"/>.
    /// Validates non-null, non-empty, and well-formed backslash-separated segments.
    /// </summary>
    public static Result<IterationPath> Parse(string? raw)
    {
        if (raw is null)
            return Result.Fail<IterationPath>("Iteration path cannot be null.");

        if (string.IsNullOrWhiteSpace(raw))
            return Result.Fail<IterationPath>("Iteration path cannot be empty.");

        var trimmed = raw.Trim();

        // Validate segments: split on backslash, each segment must be non-empty
        var segments = trimmed.Split('\\');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
                return Result.Fail<IterationPath>($"Iteration path contains empty segment: '{raw}'.");
        }

        return Result.Ok(new IterationPath(trimmed));
    }

    public override string ToString() => Value;
}
