namespace Twig.Commands.SetTree;

/// <summary>
/// A caller-supplied annotation attached to one node of an annotated working-set
/// tree (twig#277): free text, a named style, and an optional icon id.
/// </summary>
/// <param name="Note">Free text shown alongside the node. May be empty.</param>
/// <param name="Style">Named style resolved through <see cref="Rendering.SpectreTheme"/>.</param>
/// <param name="IconId">
/// Optional ADO icon id (e.g. <c>icon_parachute</c>) resolved through
/// <see cref="Domain.ValueObjects.IconSet.GetIconByIconId"/> so nerd/unicode
/// behaviour is inherited rather than reimplemented. An icon id the
/// <see cref="Domain.ValueObjects.IconSet"/> does not know is an error.
/// </param>
internal sealed record TreeAnnotation(
    string Note,
    AnnotationStyle Style,
    string? IconId);
