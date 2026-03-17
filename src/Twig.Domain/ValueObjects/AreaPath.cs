using Twig.Domain.Common;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Validated area path value object. Non-empty, backslash-separated segments.
/// </summary>
public readonly record struct AreaPath
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

    // Override equality to compare by Value only, ignoring the cached _segments array
    // (which would fail reference equality even for identical paths).
    public bool Equals(AreaPath other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <summary>
    /// Parses a raw string into an <see cref="AreaPath"/>.
    /// Validates non-null, non-empty, and well-formed backslash-separated segments.
    /// </summary>
    public static Result<AreaPath> Parse(string? raw)
    {
        if (raw is null)
            return Result.Fail<AreaPath>("Area path cannot be null.");

        if (string.IsNullOrWhiteSpace(raw))
            return Result.Fail<AreaPath>("Area path cannot be empty.");

        var trimmed = raw.Trim();

        var segments = trimmed.Split('\\');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
                return Result.Fail<AreaPath>($"Area path contains empty segment: '{raw}'.");
        }

        return Result.Ok(new AreaPath(trimmed));
    }

    public override string ToString() => Value;
}
