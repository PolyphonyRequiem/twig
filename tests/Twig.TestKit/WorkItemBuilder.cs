using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.TestKit;

/// <summary>
/// Fluent builder for <see cref="WorkItem"/> instances in tests.
/// All fields default to sensible values so only the relevant properties need to be set.
/// </summary>
public sealed class WorkItemBuilder
{
    private int _id;
    private string _title;
    private WorkItemType _type = WorkItemType.Task;
    private string _state = "New";
    private int? _parentId;
    private string? _assignedTo;
    private IterationPath _iterationPath;
    private AreaPath _areaPath;
    private bool _isSeed;
    private DateTimeOffset? _seedCreatedAt;
    private DateTimeOffset? _lastSyncedAt;
    private bool _dirty;
    private readonly Dictionary<string, string?> _fields = new(StringComparer.OrdinalIgnoreCase);

    public WorkItemBuilder(int id, string title)
    {
        _id = id;
        _title = title;
    }

    public WorkItemBuilder AsType(WorkItemType type) { _type = type; return this; }
    public WorkItemBuilder AsTask() => AsType(WorkItemType.Task);
    public WorkItemBuilder AsUserStory() => AsType(WorkItemType.UserStory);
    public WorkItemBuilder AsFeature() => AsType(WorkItemType.Feature);
    public WorkItemBuilder AsEpic() => AsType(WorkItemType.Epic);
    public WorkItemBuilder AsBug() => AsType(WorkItemType.Bug);
    public WorkItemBuilder AsProductBacklogItem() => AsType(WorkItemType.ProductBacklogItem);
    public WorkItemBuilder AsRequirement() => AsType(WorkItemType.Requirement);
    public WorkItemBuilder AsIssue() => AsType(WorkItemType.Issue);

    public WorkItemBuilder InState(string state) { _state = state; return this; }
    public WorkItemBuilder WithParent(int parentId) { _parentId = parentId; return this; }
    public WorkItemBuilder AssignedTo(string? assignee) { _assignedTo = assignee; return this; }
    public WorkItemBuilder WithIterationPath(string path) { _iterationPath = IterationPath.Parse(path).Value; return this; }
    public WorkItemBuilder WithAreaPath(string path) { _areaPath = AreaPath.Parse(path).Value; return this; }
    public WorkItemBuilder AsSeed(int daysOld = 0) { _isSeed = true; _seedCreatedAt = DateTimeOffset.UtcNow.AddDays(-daysOld); return this; }
    public WorkItemBuilder LastSyncedAt(DateTimeOffset? value) { _lastSyncedAt = value; return this; }
    public WorkItemBuilder Dirty() { _dirty = true; return this; }

    public WorkItemBuilder WithField(string key, string? value)
    {
        _fields[key] = value;
        return this;
    }

    public WorkItemBuilder WithFields(IEnumerable<KeyValuePair<string, string?>> fields)
    {
        foreach (var kvp in fields)
            _fields[kvp.Key] = kvp.Value;
        return this;
    }

    public WorkItem Build()
    {
        var item = new WorkItem
        {
            Id = _id,
            Type = _type,
            Title = _title,
            State = _state,
            ParentId = _parentId,
            AssignedTo = _assignedTo,
            IterationPath = _iterationPath,
            AreaPath = _areaPath,
            IsSeed = _isSeed,
            SeedCreatedAt = _seedCreatedAt,
            LastSyncedAt = _lastSyncedAt,
        };

        if (_dirty)
            item.SetDirty();

        if (_fields.Count > 0)
            item.ImportFields(_fields);

        return item;
    }

    /// <summary>
    /// Shorthand factory for a minimal work item. Equivalent to
    /// <c>new WorkItemBuilder(id, title).Build()</c>.
    /// </summary>
    public static WorkItem Simple(int id, string title) =>
        new WorkItemBuilder(id, title).Build();
}
