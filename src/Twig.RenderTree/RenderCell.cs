namespace Twig.RenderTree;

/// <summary>
/// A single cell of presentation data: a human display string plus an optional typed
/// machine value plus an optional semantic severity.
/// </summary>
/// <remarks>
/// <para>
/// The split between <see cref="DisplayText"/> and <see cref="Value"/> is deliberate:
/// </para>
/// <list type="bullet">
/// <item><see cref="DisplayText"/> is the formatted string the human renderer uses
/// directly (e.g. <c>"3d ago"</c>, <c>"#42"</c>, <c>"Active *"</c>).</item>
/// <item><see cref="Value"/> is the typed machine value the JSON renderer uses (e.g.
/// the integer <c>42</c>, an ISO timestamp, or <see cref="RenderAbsent"/> when no
/// machine projection exists).</item>
/// </list>
/// <para>
/// The minimal/ids renderers may use either, depending on context.
/// </para>
/// </remarks>
public sealed record RenderCell(
    string DisplayText,
    RenderValue Value,
    Severity Severity = Severity.None)
{
    /// <summary>Convenience: a display-only cell with no machine value.</summary>
    public static RenderCell DisplayOnly(string displayText, Severity severity = Severity.None)
        => new(displayText, new RenderValue.Absent(), severity);

    /// <summary>Convenience: a cell whose machine value is a string equal to the display text.</summary>
    public static RenderCell String(string value, Severity severity = Severity.None)
        => new(value, new RenderValue.String(value), severity);

    /// <summary>Convenience: a cell whose machine value is an integer.</summary>
    public static RenderCell Integer(long value, string? displayText = null, Severity severity = Severity.None)
        => new(displayText ?? value.ToString(System.Globalization.CultureInfo.InvariantCulture), new RenderValue.Integer(value), severity);

    /// <summary>Convenience: a cell whose machine value is a boolean.</summary>
    public static RenderCell Boolean(bool value, string? displayText = null, Severity severity = Severity.None)
        => new(displayText ?? (value ? "true" : "false"), new RenderValue.Boolean(value), severity);
}
