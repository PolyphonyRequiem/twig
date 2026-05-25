namespace Twig.RenderTree;

/// <summary>
/// Column metadata for a <see cref="Table"/>. <see cref="Key"/> is the stable
/// machine-name used as a JSON property key and as the lookup key in
/// <see cref="RenderRow.Cells"/>. <see cref="DisplayName"/> is the human-facing
/// column header.
/// </summary>
public sealed record RenderColumn(string Key, string DisplayName);
