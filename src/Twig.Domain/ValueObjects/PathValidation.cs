using Twig.Domain.Common;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Shared validation logic for backslash-separated path value objects
/// such as <see cref="AreaPath"/> and <see cref="IterationPath"/>.
/// </summary>
internal static class PathValidation
{
    /// <summary>
    /// Validates a raw backslash-separated path string.
    /// Returns the trimmed path on success, or a failure <see cref="Result{T}"/>
    /// with an appropriate error message using <paramref name="pathKind"/> (e.g. "Area path").
    /// </summary>
    internal static Result<string> ValidateBackslashPath(string? raw, string pathKind)
    {
        if (raw is null)
            return Result.Fail<string>($"{pathKind} cannot be null.");

        if (string.IsNullOrWhiteSpace(raw))
            return Result.Fail<string>($"{pathKind} cannot be empty.");

        var trimmed = raw.Trim();

        var segments = trimmed.Split('\\');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
                return Result.Fail<string>($"{pathKind} contains empty segment: '{raw}'.");
        }

        return Result.Ok(trimmed);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> is a descendant of
    /// (or equal to) <paramref name="ancestor"/>. Uses case-insensitive ordinal
    /// comparison with backslash segment boundaries.
    /// </summary>
    internal static bool IsUnder(string path, string ancestor)
    {
        if (string.Equals(path, ancestor, StringComparison.OrdinalIgnoreCase))
            return true;

        return path.StartsWith(ancestor + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
