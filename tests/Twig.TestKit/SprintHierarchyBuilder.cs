using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;

namespace Twig.TestKit;

/// <summary>
/// Fluent builder for <see cref="SprintHierarchy"/> instances in tests.
/// Automatically constructs the parent lookup from <see cref="WithParents"/>.
/// </summary>
public sealed class SprintHierarchyTestBuilder
{
    private readonly List<WorkItem> _sprintItems = new();
    private readonly Dictionary<int, WorkItem> _parentLookup = new();
    private IReadOnlyList<string>? _ceilingTypeNames;
    private IReadOnlyDictionary<string, int>? _typeLevelMap;

    public SprintHierarchyTestBuilder WithSprintItems(params WorkItem[] items)
    {
        _sprintItems.AddRange(items);
        return this;
    }

    public SprintHierarchyTestBuilder WithParents(params WorkItem[] parents)
    {
        foreach (var parent in parents)
            _parentLookup[parent.Id] = parent;
        return this;
    }

    public SprintHierarchyTestBuilder WithCeilingTypes(params string[] typeNames)
    {
        _ceilingTypeNames = typeNames;
        return this;
    }

    public SprintHierarchyTestBuilder WithTypeLevelMap(IReadOnlyDictionary<string, int> map)
    {
        _typeLevelMap = map;
        return this;
    }

    public SprintHierarchy Build()
    {
        var builder = new SprintHierarchyBuilder();
        return builder.Build(_sprintItems, _parentLookup, _ceilingTypeNames, _typeLevelMap);
    }
}