using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;

namespace Twig.TestKit;

/// <summary>
/// Fluent builder for <see cref="Workspace"/> instances in tests.
/// </summary>
public sealed class WorkspaceBuilder
{
    private WorkItem? _context;
    private IReadOnlyList<WorkItem> _sprintItems = Array.Empty<WorkItem>();
    private IReadOnlyList<WorkItem> _seeds = Array.Empty<WorkItem>();
    private SprintHierarchy? _hierarchy;
    private WorkspaceSections? _sections;
    private IReadOnlyList<TrackedItem>? _trackedItems;
    private IReadOnlyList<int>? _excludedIds;

    public WorkspaceBuilder WithContext(WorkItem? context) { _context = context; return this; }
    public WorkspaceBuilder WithSprintItems(params WorkItem[] items) { _sprintItems = items; return this; }
    public WorkspaceBuilder WithSeeds(params WorkItem[] seeds) { _seeds = seeds; return this; }
    public WorkspaceBuilder WithHierarchy(SprintHierarchy? hierarchy) { _hierarchy = hierarchy; return this; }
    public WorkspaceBuilder WithSections(WorkspaceSections? sections) { _sections = sections; return this; }
    public WorkspaceBuilder WithTrackedItems(params TrackedItem[] trackedItems) { _trackedItems = trackedItems; return this; }
    public WorkspaceBuilder WithExcludedIds(params int[] excludedIds) { _excludedIds = excludedIds; return this; }

    public Workspace Build() => Workspace.Build(_context, _sprintItems, _seeds, _hierarchy, _sections, _trackedItems, _excludedIds);
}
