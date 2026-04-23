namespace Twig.Domain.ValueObjects;

/// <summary>
/// Represents a workspace mode that determines how work items are sourced for display.
/// Uses static instances (same pattern as <see cref="WorkItemType"/>) for extensibility and AOT-safe serialization.
/// </summary>
public sealed record WorkspaceMode
{
    public static readonly WorkspaceMode Sprint = new("Sprint");
    public static readonly WorkspaceMode Area = new("Area");
    public static readonly WorkspaceMode Recent = new("Recent");

    public string Value { get; }

    private WorkspaceMode(string value) => Value = value;

    /// <summary>
    /// Attempts to parse a string into a known <see cref="WorkspaceMode"/>.
    /// Case-sensitive to match stored values exactly.
    /// Returns null for unrecognised values.
    /// </summary>
    public static WorkspaceMode? TryParse(string? value) => value switch
    {
        "Sprint" => Sprint,
        "Area" => Area,
        "Recent" => Recent,
        _ => null
    };

    public override string ToString() => Value;
}
