namespace Twig.Domain.ValueObjects;

/// <summary>
/// Describes a single dynamic column to render in workspace/sprint tables.
/// Produced by <see cref="Twig.Domain.Services.ColumnResolver"/>.
/// </summary>
public sealed record ColumnSpec(
    string ReferenceName,
    string DisplayName,
    string DataType);
