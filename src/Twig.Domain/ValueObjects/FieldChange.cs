namespace Twig.Domain.ValueObjects;

/// <summary>
/// Immutable record of a field change. All values stored as strings for AOT safety (RD-009).
/// </summary>
public readonly record struct FieldChange(string FieldName, string? OldValue, string? NewValue);
