using Twig.Domain.Common;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Validated area path value object. Non-empty, backslash-separated segments.
/// </summary>
public readonly struct AreaPath : IEquatable<AreaPath>
{
    public string Value { get; }

    private readonly string[] _segments;

    /// <summary>
    /// Gets the individual segments of the area path.
    /// Computed once at construction; subsequent accesses return the cached array.
    /// </summary>
    public IReadOnlyList<string> Segments => _segments ?? Array.Empty<string>();

    private AreaPath(string value)
    {
        Value = value;
        _segments = value.Split('\\');
    }

    /// <summary>
    /// Parses a raw string into an <see cref="AreaPath"/>.
    /// Validates non-null, non-empty, and well-formed backslash-separated segments.
    /// </summary>
    public static Result<AreaPath> Parse(string? raw)
    {
        var result = PathValidation.ValidateBackslashPath(raw, "Area path");
        if (!result.IsSuccess)
            return Result.Fail<AreaPath>(result.Error);

        return Result.Ok(new AreaPath(result.Value));
    }

    /// <summary>
    /// Returns <c>true</c> when this area path is a descendant of (or equal to) <paramref name="ancestor"/>.
    /// Uses case-insensitive ordinal comparison with backslash segment boundaries.
    /// </summary>
    public bool IsUnder(AreaPath ancestor) => PathValidation.IsUnder(Value, ancestor.Value);

    public bool Equals(AreaPath other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is AreaPath other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode(StringComparison.Ordinal) ?? 0;

    public static bool operator ==(AreaPath left, AreaPath right) => left.Equals(right);
    public static bool operator !=(AreaPath left, AreaPath right) => !left.Equals(right);

    public override string ToString() => Value;
}
