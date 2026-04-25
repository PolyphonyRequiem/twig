using Twig.Domain.ValueObjects;

namespace Twig.Domain.Aggregates;

/// <summary>
/// Domain record holding process type metadata fetched from ADO during init/refresh.
/// Persisted in the <c>process_types</c> SQLite table.
/// </summary>
public sealed class ProcessTypeRecord
{
    public string TypeName { get; init; } = string.Empty;

    /// <summary>Ordered state sequence derived from the ADO work item type states array.</summary>
    public IReadOnlyList<StateEntry> States { get; init; } = Array.Empty<StateEntry>();

    /// <summary>Default child type name, or null if this type has no children.</summary>
    public string? DefaultChildType { get; init; }

    /// <summary>All valid child type names (empty for leaf-level types).</summary>
    public IReadOnlyList<string> ValidChildTypes { get; init; } = Array.Empty<string>();

    /// <summary>Hex color string from ADO (e.g. "009CCC"), or null.</summary>
    public string? ColorHex { get; init; }

    /// <summary>ADO icon identifier (e.g. "icon_list"), or null.</summary>
    public string? IconId { get; init; }
}
