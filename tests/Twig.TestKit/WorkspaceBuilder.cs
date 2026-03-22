using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;

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

    public WorkspaceBuilder WithContext(WorkItem? context) { _context = context; return this; }
    public WorkspaceBuilder WithSprintItems(params WorkItem[] items) { _sprintItems = items; return this; }
    public WorkspaceBuilder WithSeeds(params WorkItem[] seeds) { _seeds = seeds; return this; }
    public WorkspaceBuilder WithHierarchy(SprintHierarchy? hierarchy) { _hierarchy = hierarchy; return this; }

    public Workspace Build() => Workspace.Build(_context, _sprintItems, _seeds, _hierarchy);
}
