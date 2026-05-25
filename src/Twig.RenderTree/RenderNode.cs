namespace Twig.RenderTree;

/// <summary>
/// One node of a <see cref="RenderTree"/>. Commands construct these to describe
/// presentation intent; renderers switch on the variant to produce format-specific
/// output (Spectre human output, JSON, minimal, ids).
/// </summary>
/// <remarks>
/// <para>
/// The variants intentionally cover a small vocabulary:
/// </para>
/// <list type="bullet">
/// <item><see cref="Text"/> — a flat string line. Use for prose, errors, successes.</item>
/// <item><see cref="Hint"/> — secondary guidance (typically dim, suppressed in machine output).</item>
/// <item><see cref="KeyValue"/> — a single labelled value.</item>
/// <item><see cref="Record"/> — a structured object with named fields. Use for single work
/// items, set confirmations, status summaries.</item>
/// <item><see cref="Table"/> — a homogeneous list of rows with column metadata. Use for
/// query results, link lists, validation results.</item>
/// <item><see cref="TreeView"/> — a hierarchy of rows. Use for work trees, seed groupings.</item>
/// <item><see cref="Section"/> — a visual grouping container that holds further nodes.</item>
/// <item><see cref="Document"/> — a named bag of child nodes; the structured-object analog
/// of <see cref="Section"/>'s visual grouping.</item>
/// </list>
/// </remarks>
public abstract record RenderNode
{
    private RenderNode() { }

    /// <summary>A flat text line. Carries optional semantic severity.</summary>
    public sealed record Text(string Content, Severity Severity = Severity.None) : RenderNode;

    /// <summary>
    /// Secondary guidance shown dim in human output and typically suppressed in
    /// machine output (JSON / minimal / ids).
    /// </summary>
    public sealed record Hint(string Content) : RenderNode;

    /// <summary>
    /// A single labelled value (e.g. <c>State: Active</c>, <c>Assigned: Daniel Green</c>).
    /// </summary>
    public sealed record KeyValue(string Key, RenderCell Value, Severity Severity = Severity.None) : RenderNode;

    /// <summary>
    /// A structured object with named fields. The JSON renderer projects this as a JSON
    /// object keyed by field name; human/minimal renderers project it as a sequence of
    /// key-value lines.
    /// </summary>
    /// <param name="Kind">
    /// Optional stable discriminator (e.g. <c>"workItem"</c>) the JSON renderer may
    /// emit as a <c>kind</c> property.
    /// </param>
    /// <param name="Fields">Named fields keyed by stable machine-name.</param>
    public sealed record Record(
        string? Kind,
        IReadOnlyDictionary<string, RenderCell> Fields) : RenderNode;

    /// <summary>
    /// A homogeneous list of rows with column metadata. <see cref="Caption"/> is the
    /// human-facing table title (optional). <see cref="Rows"/> entries should use keys
    /// matching the <see cref="Columns"/> keys.
    /// </summary>
    public sealed record Table(
        string? Caption,
        IReadOnlyList<RenderColumn> Columns,
        IReadOnlyList<RenderRow> Rows) : RenderNode;

    /// <summary>A hierarchy of rows. See <see cref="RenderTreeBranch"/>.</summary>
    public sealed record TreeView(RenderTreeBranch Root) : RenderNode;

    /// <summary>
    /// A visual grouping container. The human renderer typically draws a header and
    /// indents the children; the JSON renderer projects it as a named array. Optional
    /// header is human-facing only.
    /// </summary>
    public sealed record Section(
        string? Header,
        IReadOnlyList<RenderNode> Children) : RenderNode;

    /// <summary>
    /// A named bag of child nodes — the structured-object analog of
    /// <see cref="Section"/>. The JSON renderer projects this as a top-level
    /// (or nested) JSON object whose properties come from each
    /// <see cref="DocumentField.Key"/>. The human renderer walks each visible
    /// field and emits the field's optional header followed by its rendered
    /// content (using <see cref="DocumentField.HumanOverride"/> when set).
    /// </summary>
    /// <remarks>
    /// Keep <see cref="Section"/> for visual grouping (header + indented
    /// children) and <see cref="Document"/> for structured wrapper-object
    /// payloads. Use <see cref="RenderAudience"/> on individual
    /// <see cref="DocumentField"/>s when a field belongs to only one audience.
    /// </remarks>
    /// <param name="Kind">
    /// Optional stable discriminator the JSON renderer emits as a <c>kind</c>
    /// property when the document is one of several roots; suppressed when
    /// the document is the single root (matching <see cref="Record"/>).
    /// </param>
    /// <param name="Fields">Ordered list of named fields.</param>
    public sealed record Document(
        string? Kind,
        IReadOnlyList<DocumentField> Fields) : RenderNode;
}

