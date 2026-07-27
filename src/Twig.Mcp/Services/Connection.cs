namespace Twig.Mcp.Services;

/// <summary>
/// Identifies a connection by its Azure DevOps organization and project.
/// Format: <c>{org}/{project}</c>.
/// </summary>
/// <remarks>
/// Named <c>Connection</c> per wayfinder 0004, which retired <c>Workspace</c> as overloaded
/// vocabulary. This type was previously <c>WorkspaceKey</c>.
/// </remarks>
public sealed record Connection(string Org, string Project)
{
    public override string ToString() => $"{Org}/{Project}";

    /// <summary>
    /// Parses a <c>"org/project"</c> string into a <see cref="Connection"/>.
    /// Whitespace around org and project is trimmed; casing is preserved.
    /// </summary>
    /// <exception cref="FormatException">
    /// Thrown when <paramref name="value"/> is null, empty, or does not contain exactly one slash.
    /// </exception>
    public static Connection Parse(string value)
    {
        if (!TryParse(value, out var result))
            throw new FormatException($"Invalid workspace key '{value}'. Expected format: org/project");

        return result!;
    }

    /// <summary>
    /// Attempts to parse a <c>"org/project"</c> string into a <see cref="Connection"/>.
    /// Returns <c>false</c> when <paramref name="value"/> is null, empty, or malformed.
    /// </summary>
    public static bool TryParse(string? value, out Connection? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var slashIndex = value.IndexOf('/');
        if (slashIndex < 0 || slashIndex != value.LastIndexOf('/'))
            return false;

        var org = value[..slashIndex].Trim();
        var project = value[(slashIndex + 1)..].Trim();

        if (org.Length == 0 || project.Length == 0)
            return false;

        result = new Connection(org, project);
        return true;
    }
}
