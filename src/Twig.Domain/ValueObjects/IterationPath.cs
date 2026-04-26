using Twig.Domain.Common;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Validated iteration path value object. Non-empty, backslash-separated segments.
/// </summary>
public readonly struct IterationPath : IEquatable<IterationPath>
{
    public string Value { get; }

    private IterationPath(string value) => Value = value;

    /// <summary>
    /// Parses a raw string into an <see cref="IterationPath"/>.
    /// Validates non-null, non-empty, and well-formed backslash-separated segments.
    /// </summary>
    public static Result<IterationPath> Parse(string? raw)
    {
        var result = PathValidation.ValidateBackslashPath(raw, "Iteration path");
        if (!result.IsSuccess)
            return Result.Fail<IterationPath>(result.Error);

        return Result.Ok(new IterationPath(result.Value));
    }

    /// <summary>
    /// Returns <c>true</c> when this iteration path is a descendant of (or equal to) <paramref name="ancestor"/>.
    /// Uses case-insensitive ordinal comparison with backslash segment boundaries.
    /// </summary>
    public bool IsUnder(IterationPath ancestor) => PathValidation.IsUnder(Value, ancestor.Value);

    public bool Equals(IterationPath other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is IterationPath other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    public static bool operator ==(IterationPath left, IterationPath right) => left.Equals(right);
    public static bool operator !=(IterationPath left, IterationPath right) => !left.Equals(right);

    public override string ToString() => Value;
}
