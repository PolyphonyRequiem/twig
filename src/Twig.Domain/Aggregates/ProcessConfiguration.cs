using Twig.Domain.Enums;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Domain.Aggregates;

namespace Twig.Domain.Aggregates;

/// <summary>
/// Configuration for a specific work item type within a process template.
/// </summary>
public sealed class TypeConfig
{
    /// <summary>Ordered state names for this work item type.</summary>
    public IReadOnlyList<string> States { get; }

    /// <summary>Ordered state entries with category metadata for shorthand resolution.</summary>
    public IReadOnlyList<StateEntry> StateEntries { get; }

    /// <summary>Work item types that can be children of this type.</summary>
    public IReadOnlyList<WorkItemType> AllowedChildTypes { get; }

    /// <summary>
    /// Classifies each (from, to) state pair as Forward or Cut. Keyed case-INSENSITIVELY
    /// (AB#369) — see <see cref="StatePairComparer"/>.
    /// </summary>
    public IReadOnlyDictionary<(string From, string To), TransitionKind> TransitionRules { get; }

    public TypeConfig(
        IReadOnlyList<string> states,
        IReadOnlyList<StateEntry> stateEntries,
        IReadOnlyList<WorkItemType> allowedChildTypes,
        IReadOnlyDictionary<(string From, string To), TransitionKind> transitionRules)
    {
        States = states;
        StateEntries = stateEntries;
        AllowedChildTypes = allowedChildTypes;

        // AB#369: rebuilt here rather than trusting the caller's comparer. A
        // (string, string) ValueTuple key uses the DEFAULT comparer — ordinal and
        // case-SENSITIVE — so a rules dictionary keyed by the process definition's casing
        // ("To do") missed on an item's stored casing ("To Do"), GetTransitionKind returned
        // null, and a perfectly legal transition was reported as forbidden by process rules.
        // Every other state comparison in this codebase is OrdinalIgnoreCase
        // (StateTransitionWorkflow.Validate/ExecuteAsync, StateResolver.ResolveByName), so
        // the case-sensitive lookup was an inconsistency rather than a decision.
        //
        // Normalising at construction rather than at the lookup site is deliberate: the
        // property is public, callers hand in their own dictionaries, and a fix applied only
        // in GetTransitionKind would be silently bypassed by anyone reading TransitionRules
        // directly.
        TransitionRules = transitionRules as Dictionary<(string From, string To), TransitionKind>
                is { } d && ReferenceEquals(d.Comparer, StatePairComparer.Instance)
            ? transitionRules
            : new Dictionary<(string From, string To), TransitionKind>(
                transitionRules, StatePairComparer.Instance);
    }
}

/// <summary>
/// Compares (from, to) state-name pairs case-insensitively (AB#369).
/// </summary>
/// <remarks>
/// ADO returns state names with inconsistent casing between the process definition
/// (<c>"To do"</c>) and the values stored on individual work items (<c>"To Do"</c>), and both
/// spellings are observable on the same board simultaneously. Matching the semantics every
/// other state comparison in the codebase already uses.
/// </remarks>
internal sealed class StatePairComparer : IEqualityComparer<(string From, string To)>
{
    public static readonly StatePairComparer Instance = new();

    public bool Equals((string From, string To) x, (string From, string To) y)
        => StringComparer.OrdinalIgnoreCase.Equals(x.From, y.From)
        && StringComparer.OrdinalIgnoreCase.Equals(x.To, y.To);

    public int GetHashCode((string From, string To) obj)
        => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.From),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.To));
}

/// <summary>
/// Compares <see cref="WorkItemType"/> keys case-insensitively (AB#369).
/// </summary>
/// <remarks>
/// The same defect as <see cref="StatePairComparer"/>, one level up and found by auditing for
/// it rather than by a report. <c>WorkItemType.Parse</c> normalises casing for the thirteen
/// WELL-KNOWN types only and explicitly "preserv[es] original casing" for custom ones, so a
/// board with a custom type looked up under different casing than it was stored missed the
/// <c>TypeConfigs</c> entry entirely — surfacing as ProcessConfigNotFound rather than as a
/// transition error, but from the identical root cause.
/// </remarks>
internal sealed class WorkItemTypeComparer : IEqualityComparer<WorkItemType>
{
    public static readonly WorkItemTypeComparer Instance = new();

    public bool Equals(WorkItemType x, WorkItemType y)
        => StringComparer.OrdinalIgnoreCase.Equals(x.Value, y.Value);

    public int GetHashCode(WorkItemType obj)
        => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value);
}

/// <summary>
/// Immutable aggregate encoding ADO process configuration rules.
/// Built via <see cref="FromRecords"/> factory from dynamic <see cref="ProcessTypeRecord"/> data.
/// </summary>
public sealed class ProcessConfiguration
{
    /// <summary>Per-type configuration (states, child types, transition rules).</summary>
    public IReadOnlyDictionary<WorkItemType, TypeConfig> TypeConfigs { get; }

    private readonly IReadOnlySet<WorkItemType> _hierarchyConstrainedTypes;

    private ProcessConfiguration(
        IReadOnlyDictionary<WorkItemType, TypeConfig> typeConfigs,
        IReadOnlySet<WorkItemType> hierarchyConstrainedTypes)
    {
        TypeConfigs = typeConfigs;
        _hierarchyConstrainedTypes = hierarchyConstrainedTypes;
    }

    /// <summary>
    /// Classifies a state transition for the given work item type.
    /// </summary>
    public TransitionKind? GetTransitionKind(WorkItemType workItemType, string fromState, string toState)
    {
        if (!TypeConfigs.TryGetValue(workItemType, out var config))
            return null;

        if (config.TransitionRules.TryGetValue((fromState, toState), out var kind))
            return kind;

        return null;
    }

    /// <summary>
    /// Returns allowed child types for the given work item type.
    /// </summary>
    public IReadOnlyList<WorkItemType> GetAllowedChildTypes(WorkItemType workItemType)
    {
        if (TypeConfigs.TryGetValue(workItemType, out var config))
            return config.AllowedChildTypes;

        return Array.Empty<WorkItemType>();
    }

    /// <summary>
    /// Returns whether the process metadata permits <paramref name="childType"/> under
    /// <paramref name="parentType"/>. Backlog types are constrained to their adjacent
    /// hierarchy level. Relationships involving a process type outside the backlog
    /// hierarchy are accepted when both types exist because ADO does not publish
    /// hierarchy constraints for those types.
    /// </summary>
    internal bool IsChildTypeAllowed(WorkItemType parentType, WorkItemType childType)
    {
        if (!TypeConfigs.ContainsKey(parentType))
            return false;

        if (!_hierarchyConstrainedTypes.Contains(parentType))
            return TypeConfigs.ContainsKey(childType);

        if (TypeConfigs.ContainsKey(childType) &&
            !_hierarchyConstrainedTypes.Contains(childType))
            return true;

        return GetAllowedChildTypes(parentType).Contains(childType);
    }

    /// <summary>
    /// Builds a ProcessConfiguration from stored process type records.
    /// Records with empty type names or no states are skipped.
    /// </summary>
    public static ProcessConfiguration FromRecords(IReadOnlyList<ProcessTypeRecord> typeRecords) =>
        FromRecordsCore(typeRecords, processConfigurationData: null);

    /// <summary>
    /// Builds a ProcessConfiguration from stored type records and the full ADO backlog
    /// hierarchy metadata. Types omitted from the backlog hierarchy remain valid process
    /// types, but their parent-child constraints are deferred to ADO.
    /// </summary>
    internal static ProcessConfiguration FromRecords(
        IReadOnlyList<ProcessTypeRecord> typeRecords,
        ProcessConfigurationData processConfigurationData) =>
        FromRecordsCore(typeRecords, processConfigurationData);

    private static ProcessConfiguration FromRecordsCore(
        IReadOnlyList<ProcessTypeRecord> typeRecords,
        ProcessConfigurationData? processConfigurationData)
    {
        var configs = new Dictionary<WorkItemType, TypeConfig>(WorkItemTypeComparer.Instance);
        foreach (var record in typeRecords)
        {
            if (string.IsNullOrWhiteSpace(record.TypeName) || record.States.Count == 0)
                continue;

            var parseResult = WorkItemType.Parse(record.TypeName);
            if (!parseResult.IsSuccess)
                continue;

            var type = parseResult.Value;
            var childTypes = record.ValidChildTypes
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => WorkItemType.Parse(n))
                .Where(r => r.IsSuccess)
                .Select(r => r.Value)
                .ToArray();
            configs[type] = BuildTypeConfig(
                record.States.Select(s => s.Name).ToArray(),
                record.States.ToArray(),
                childTypes);
        }

        IReadOnlySet<WorkItemType> constrainedTypes;
        if (processConfigurationData is null)
        {
            constrainedTypes = configs.Keys.ToHashSet();
        }
        else
        {
            constrainedTypes = BacklogHierarchyService.GetTypeLevelMap(processConfigurationData)
                .Keys
                .Select(WorkItemType.Parse)
                .Where(result => result.IsSuccess)
                .Select(result => result.Value)
                .ToHashSet();
        }

        return new ProcessConfiguration(configs, constrainedTypes);
    }

    /// <summary>
    /// Builds a TypeConfig with automatically generated transition rules.
    /// Forward = any move between non-removed states, Cut = transitioning to a <see cref="StateCategory.Removed"/> state.
    /// ADO enforces process-specific ordering; twig treats all non-cut transitions equally.
    /// </summary>
    private static TypeConfig BuildTypeConfig(string[] states, StateEntry[] stateEntries, WorkItemType[] childTypes)
    {
        var transitions = new Dictionary<(string From, string To), TransitionKind>();

        for (var i = 0; i < states.Length; i++)
        {
            for (var j = 0; j < states.Length; j++)
            {
                if (i == j) continue;

                var from = states[i];
                var to = states[j];

                var kind = stateEntries[j].Category == StateCategory.Removed
                    ? TransitionKind.Cut
                    : TransitionKind.Forward;

                transitions[(from, to)] = kind;
            }
        }

        return new TypeConfig(
            Array.AsReadOnly(states),
            Array.AsReadOnly(stateEntries),
            Array.AsReadOnly(childTypes),
            transitions);
    }
}
