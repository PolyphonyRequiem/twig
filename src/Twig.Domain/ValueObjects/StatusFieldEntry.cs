namespace Twig.Domain.ValueObjects;

/// <summary>
/// A single entry from the status-fields configuration file, identifying a field
/// by its canonical reference name and whether the user has selected it for display.
/// </summary>
public readonly record struct StatusFieldEntry(string ReferenceName, bool IsIncluded);
