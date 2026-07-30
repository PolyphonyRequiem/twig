namespace Twig.RenderTree;

/// <summary>
/// Renderer-agnostic semantic severity hint attached to a <see cref="RenderNode"/>,
/// <see cref="RenderCell"/>, or <see cref="KeyValue"/>.
/// </summary>
/// <remarks>
/// The render tree carries semantic intent (this row is an error, this hint is informational)
/// and lets the renderer choose how to express it — ANSI red for the human renderer, a
/// <c>severity</c> field for the JSON renderer, a prefix for the minimal renderer. No
/// renderer-specific styling (color names, ANSI codes, Spectre markup) belongs in the
/// render tree.
/// </remarks>
public enum Severity
{
    /// <summary>No semantic severity. Renderers use default styling.</summary>
    None = 0,

    /// <summary>Informational. Typically rendered neutral / blue / dim.</summary>
    Info = 1,

    /// <summary>Successful outcome. Typically rendered green / bold.</summary>
    Success = 2,

    /// <summary>Warning. Typically rendered yellow.</summary>
    Warning = 3,

    /// <summary>Error. Typically rendered red.</summary>
    Error = 4,

    /// <summary>
    /// De-emphasized. Typically rendered dim grey. Use for content that is present
    /// as context rather than as the subject of the output — e.g. the ancestor spine
    /// above a working-set member, which explains where an item lives without being
    /// something the reader is acting on.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="None"/>: <c>None</c> means "no semantic intent, render
    /// normally", whereas <c>Muted</c> is a positive instruction to recede. Machine
    /// renderers treat it like any other severity tag.
    /// </remarks>
    Muted = 5,
}
