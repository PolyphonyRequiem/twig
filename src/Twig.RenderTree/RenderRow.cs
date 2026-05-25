namespace Twig.RenderTree;

/// <summary>
/// One row of a <see cref="Table"/> or one branch payload in a <see cref="TreeView"/>.
/// </summary>
/// <param name="Kind">
/// Optional stable discriminator (e.g. <c>"workItem"</c>, <c>"seedLink"</c>) the JSON
/// renderer may emit as a <c>kind</c> property to help machine consumers tell row
/// shapes apart. Human/minimal renderers ignore it.
/// </param>
/// <param name="Cells">
/// Per-column values. Keys match a <see cref="RenderColumn.Key"/> for table rows.
/// Loose rows (e.g. inside a <see cref="TreeView"/>) may use any keys.
/// </param>
public sealed record RenderRow(
    string? Kind,
    IReadOnlyDictionary<string, RenderCell> Cells);
