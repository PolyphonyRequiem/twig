namespace Twig.RenderTree;

/// <summary>
/// One named field of a <see cref="RenderNode.Document"/>.
/// </summary>
/// <param name="Key">
/// Stable machine name. Becomes the JSON property name and the key the minimal
/// renderer uses when emitting <c>key=value</c> lines. Must be unique within
/// the document.
/// </param>
/// <param name="Node">
/// The default node projection used by every renderer that the field is
/// visible to (see <paramref name="Audience"/>). When
/// <paramref name="HumanOverride"/> is supplied, the human renderer uses that
/// instead.
/// </param>
/// <param name="Audience">Audience filter — see <see cref="RenderAudience"/>.</param>
/// <param name="Header">
/// Optional human-facing header text emitted before the field's rendered
/// content by the human renderer. Has no effect on machine renderers.
/// </param>
/// <param name="HumanOverride">
/// Optional alternative projection for the human renderer. Use when a field's
/// machine shape (e.g. a <see cref="RenderNode.Table"/> with column metadata)
/// differs from how humans should see it (e.g. plain aligned text lines).
/// Ignored when null.
/// </param>
public sealed record DocumentField(
    string Key,
    RenderNode Node,
    RenderAudience Audience = RenderAudience.All,
    string? Header = null,
    RenderNode? HumanOverride = null);
