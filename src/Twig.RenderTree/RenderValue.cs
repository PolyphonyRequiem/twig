namespace Twig.RenderTree;

/// <summary>
/// A typed scalar value carried by a <see cref="RenderCell"/> or <see cref="KeyValue"/>.
/// Closed discriminated union so JSON renderers can emit typed primitives (numbers,
/// booleans, ISO timestamps) rather than display strings — without resorting to
/// <c>object?</c>, which is an AOT/trimming footgun.
/// </summary>
/// <remarks>
/// Use <see cref="Null"/> when the value is semantically <c>null</c> in the machine
/// output (JSON null). Use <see cref="Absent"/> when the cell has no machine value
/// at all and renderers should fall back to <see cref="RenderCell.DisplayText"/>.
/// </remarks>
public abstract record RenderValue
{
    private RenderValue() { }

    /// <summary>A string value (UTF-16, not surrogate-validated).</summary>
    public sealed record String(string Value) : RenderValue;

    /// <summary>A 64-bit signed integer value. Use for work item IDs, counts, indices.</summary>
    public sealed record Integer(long Value) : RenderValue;

    /// <summary>A decimal value. Use for story points and other fractional fields.</summary>
    public sealed record Decimal(decimal Value) : RenderValue;

    /// <summary>A boolean value.</summary>
    public sealed record Boolean(bool Value) : RenderValue;

    /// <summary>An instant in time with timezone offset. JSON renderers emit ISO 8601.</summary>
    public sealed record DateTime(DateTimeOffset Value) : RenderValue;

    /// <summary>
    /// Explicit machine-null. JSON renderers emit <c>null</c>; human/minimal renderers
    /// fall back to <see cref="RenderCell.DisplayText"/>.
    /// </summary>
    public sealed record Null : RenderValue;

    /// <summary>
    /// No machine value present. JSON renderers OMIT the property; human/minimal
    /// renderers fall back to <see cref="RenderCell.DisplayText"/>. Use this for
    /// display-only cells (separators, icons, headers) that should not appear in
    /// JSON output.
    /// </summary>
    public sealed record Absent : RenderValue;

    /// <summary>
    /// A nested object value carrying a dictionary of named child cells. JSON
    /// renderers emit a JSON object whose properties are the cell keys with
    /// each cell's <see cref="RenderValue"/> projected recursively. Human and
    /// minimal renderers fall back to <see cref="RenderCell.DisplayText"/>;
    /// the IDs renderer walks into the cells looking for the conventional
    /// <c>id</c> key.
    /// </summary>
    /// <remarks>
    /// Use this for per-node <c>fields</c> blocks (e.g. the work-item field
    /// bag where polyphony reads <c>fields["System.Description"]</c>) — cases
    /// where the machine wire shape needs nested structure but the human
    /// rendering already has a compact display projection.
    /// </remarks>
    public sealed record Object(IReadOnlyDictionary<string, RenderCell> Cells) : RenderValue;
}

