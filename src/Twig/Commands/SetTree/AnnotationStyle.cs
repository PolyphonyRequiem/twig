namespace Twig.Commands.SetTree;

/// <summary>
/// The small, closed set of named styles a caller may attach to an annotated
/// working-set node (twig#277).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a closed vocabulary rather than free-form colour: the render is a
/// <em>consent surface</em> (a human approves a bulk write from what it shows), so
/// callers name semantic intent and twig owns the presentation. Raw colour codes
/// from a caller would bypass <see cref="Rendering.SpectreTheme"/> and break under
/// a different theme or icon mode.
/// </para>
/// <para>
/// The wire names are lower-case (<c>default</c>, <c>muted</c>, <c>proposed</c>,
/// <c>warn</c>, <c>error</c>) and are parsed case-insensitively. An unrecognized
/// style name is an <em>error</em>, not a silent downgrade to default — see
/// <see cref="AnnotationMap"/>.
/// </para>
/// </remarks>
internal enum AnnotationStyle
{
    /// <summary>No emphasis. The node renders in the tree's normal styling.</summary>
    Default = 0,

    /// <summary>De-emphasized — "this is context, not something you are deciding on".</summary>
    Muted = 1,

    /// <summary>A proposed change the reviewer is being asked to approve.</summary>
    Proposed = 2,

    /// <summary>Needs attention before approving.</summary>
    Warn = 3,

    /// <summary>Something is wrong with this node.</summary>
    Error = 4,
}

internal static class AnnotationStyleParser
{
    /// <summary>Every accepted wire name, in declaration order, for error messages.</summary>
    internal static readonly IReadOnlyList<string> Accepted =
    [
        "default",
        "muted",
        "proposed",
        "warn",
        "error",
    ];

    /// <summary>
    /// Parses a wire style name. Returns <see langword="false"/> for anything not on
    /// <see cref="Accepted"/> so the caller can fail loudly.
    /// </summary>
    internal static bool TryParse(string? value, out AnnotationStyle style)
    {
        style = AnnotationStyle.Default;
        if (value is null)
            return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "": case "default": style = AnnotationStyle.Default; return true;
            case "muted": style = AnnotationStyle.Muted; return true;
            case "proposed": style = AnnotationStyle.Proposed; return true;
            case "warn": style = AnnotationStyle.Warn; return true;
            case "error": style = AnnotationStyle.Error; return true;
            default: return false;
        }
    }

    /// <summary>The canonical lower-case wire name, for JSON output.</summary>
    internal static string ToWireName(AnnotationStyle style) => style switch
    {
        AnnotationStyle.Muted => "muted",
        AnnotationStyle.Proposed => "proposed",
        AnnotationStyle.Warn => "warn",
        AnnotationStyle.Error => "error",
        _ => "default",
    };
}
