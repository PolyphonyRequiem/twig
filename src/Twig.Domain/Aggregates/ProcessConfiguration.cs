using Twig.Domain.Enums;
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

    /// <summary>Classifies each (from, to) state pair as Forward or Cut.</summary>
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
        TransitionRules = transitionRules;
    }
}

/// <summary>
/// Immutable aggregate encoding ADO process configuration rules.
/// Built via <see cref="FromRecords"/> factory from dynamic <see cref="ProcessTypeRecord"/> data.
/// </summary>
public sealed class ProcessConfiguration
{
    /// <summary>Per-type configuration (states, child types, transition rules).</summary>
    public IReadOnlyDictionary<WorkItemType, TypeConfig> TypeConfigs { get; }

    private ProcessConfiguration(IReadOnlyDictionary<WorkItemType, TypeConfig> typeConfigs)
    {
        TypeConfigs = typeConfigs;
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
    /// Builds a ProcessConfiguration from stored process type records.
    /// Records with empty type names or no states are skipped.
    /// </summary>
    public static ProcessConfiguration FromRecords(IReadOnlyList<ProcessTypeRecord> typeRecords)
    {
        var configs = new Dictionary<WorkItemType, TypeConfig>();
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
        return new ProcessConfiguration(configs);
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
