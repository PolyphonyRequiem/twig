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

    /// <summary>
    /// Reference names of every work item type category this type belongs to, from
    /// <c>_apis/wit/workitemtypecategories</c>. Empty when the process places the type in
    /// no category, which is a real answer and not an error.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>A SET, not a single value, and not derivable from the type name</b> (AB#656).
    /// Measured on the Hyperbright process: <c>Issue</c> belongs to
    /// <c>Microsoft.HiddenCategory</c>, <c>Microsoft.BugCategory</c> AND
    /// <c>Microsoft.RequirementCategory</c> simultaneously, while <c>Bug</c> belongs to
    /// <c>Microsoft.RequirementCategory</c> and NOT <c>Microsoft.BugCategory</c>. Modelling
    /// this as one category, or inferring it from the name, produces a confidently wrong
    /// answer on a real process — and a hardcoded list of hidden type names rots the moment
    /// a customer process differs.
    /// </remarks>
    public IReadOnlyList<string> CategoryReferenceNames { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether ADO reserves this type for its own tooling, i.e. a user must not create one
    /// manually. Derived from membership of <see cref="WorkItemTypeCategories.Hidden"/>.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored: a second persisted field could disagree with the
    /// membership set it summarises, and <c>CONTEXT.md</c> rule 1 says one concept gets one
    /// name. The full set stays the domain fact; this is the one question every consumer
    /// actually asks.
    /// </remarks>
    public bool IsHidden =>
        CategoryReferenceNames.Contains(WorkItemTypeCategories.Hidden, StringComparer.OrdinalIgnoreCase);
}
