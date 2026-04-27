using System.Collections.ObjectModel;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Aggregates;

/// <summary>
/// Root aggregate for an Azure DevOps work item.
/// Mutations via <see cref="ChangeState"/>, <see cref="UpdateField"/>,
/// <see cref="AddNote"/> take effect immediately and set <see cref="IsDirty"/>.
/// </summary>
public sealed class WorkItem
{
    private static int _seedIdCounter;

    private readonly Dictionary<string, string?> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingNote> _pendingNotes = new();
    private readonly ReadOnlyDictionary<string, string?> _fieldsView;
    private readonly ReadOnlyCollection<PendingNote> _pendingNotesView;

    public WorkItem()
    {
        _fieldsView = new ReadOnlyDictionary<string, string?>(_fields);
        _pendingNotesView = _pendingNotes.AsReadOnly();
    }

    // ── Identity & metadata ─────────────────────────────────────────
    public int Id { get; init; }
    public WorkItemType Type { get; init; }
    public string Title { get; init; } = string.Empty;
    public string State { get; internal set; } = string.Empty;
    public string? AssignedTo { get; init; }
    public IterationPath IterationPath { get; init; }
    public AreaPath AreaPath { get; init; }
    public int? ParentId { get; init; }
    public int Revision { get; private set; }

    // ── Dirty tracking ──────────────────────────────────────────────
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Restores the dirty flag from persisted state without side effects.
    /// </summary>
    internal void SetDirty() => IsDirty = true;

    // ── Seed support ────────────────────────────────────────────────
    public bool IsSeed { get; init; }
    public DateTimeOffset? SeedCreatedAt { get; init; }

    // ── Cache staleness ─────────────────────────────────────────────
    public DateTimeOffset? LastSyncedAt { get; init; }

    // ── Arbitrary field storage (read-only public surface) ──────────
    public IReadOnlyDictionary<string, string?> Fields => _fieldsView;

    // ── Pending notes (read-only public surface) ────────────────────
    public IReadOnlyList<PendingNote> PendingNotes => _pendingNotesView;

    // ── Internal mutators for commands (same assembly) ──────────────

    internal void SetField(string fieldName, string? value) => _fields[fieldName] = value;

    /// <summary>
    /// Bulk-imports fields without setting the dirty flag.
    /// Intended for hydration from ADO response mapping and test convenience.
    /// </summary>
    internal void ImportFields(IEnumerable<KeyValuePair<string, string?>> fields)
    {
        foreach (var kvp in fields)
            _fields[kvp.Key] = kvp.Value;
    }

    internal bool TryGetField(string fieldName, out string? value) => _fields.TryGetValue(fieldName, out value);

    internal void AddPendingNote(PendingNote note) => _pendingNotes.Add(note);

    // ── Direct mutation methods ─────────────────────────────────────

    /// <summary>Transitions the work item to a new state, returning the field change.</summary>
    public FieldChange ChangeState(string newState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newState);
        var oldState = State;
        State = newState;
        IsDirty = true;
        return new FieldChange("System.State", oldState, newState);
    }

    /// <summary>Sets an arbitrary field value, returning the field change.</summary>
    public FieldChange UpdateField(string fieldName, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        TryGetField(fieldName, out var oldValue);
        SetField(fieldName, value);
        IsDirty = true;
        return new FieldChange(fieldName, oldValue, value);
    }

    /// <summary>Appends a pending note. Notes do not produce field changes.</summary>
    public void AddNote(PendingNote note)
    {
        AddPendingNote(note);
        IsDirty = true;
    }

    /// <summary>
    /// Marks the work item as synchronized with the remote store.
    /// Clears the dirty flag and updates the revision number.
    /// </summary>
    public void MarkSynced(int revision)
    {
        Revision = revision;
        IsDirty = false;
    }

    // ── Seed copy ──────────────────────────────────────────────────

    /// <summary>
    /// Returns a new <see cref="WorkItem"/> with updated title and fields,
    /// preserving all other properties (Id, Type, IsSeed, SeedCreatedAt,
    /// ParentId, AreaPath, IterationPath, State, AssignedTo, Revision).
    /// The new instance is not dirty.
    /// </summary>
    public WorkItem WithSeedFields(
        string title,
        IReadOnlyDictionary<string, string?> fields)
    {
        var copy = new WorkItem
        {
            Id = Id,
            Type = Type,
            Title = title,
            State = State,
            AssignedTo = AssignedTo,
            IterationPath = IterationPath,
            AreaPath = AreaPath,
            ParentId = ParentId,
            IsSeed = IsSeed,
            SeedCreatedAt = SeedCreatedAt,
            LastSyncedAt = LastSyncedAt,
        };

        // Restore revision without side-effecting dirty flag
        if (Revision > 0)
            copy.MarkSynced(Revision);

        copy.ImportFields(fields);
        return copy;
    }

    /// <summary>
    /// Returns a copy with a different <see cref="ParentId"/>.
    /// All other properties (including fields and dirty state) are preserved.
    /// </summary>
    public WorkItem WithParentId(int? newParentId)
    {
        var copy = new WorkItem
        {
            Id = Id,
            Type = Type,
            Title = Title,
            State = State,
            AssignedTo = AssignedTo,
            IterationPath = IterationPath,
            AreaPath = AreaPath,
            ParentId = newParentId,
            IsSeed = IsSeed,
            SeedCreatedAt = SeedCreatedAt,
            LastSyncedAt = LastSyncedAt,
        };

        if (Revision > 0) copy.MarkSynced(Revision);
        copy.ImportFields(Fields);
        if (IsDirty) copy.SetDirty();

        return copy;
    }

    /// <summary>
    /// Returns a copy with a different <see cref="IsSeed"/> flag.
    /// Used to mark a fetched-back ADO item as seed provenance.
    /// </summary>
    public WorkItem WithIsSeed(bool isSeed)
    {
        var copy = new WorkItem
        {
            Id = Id,
            Type = Type,
            Title = Title,
            State = State,
            AssignedTo = AssignedTo,
            IterationPath = IterationPath,
            AreaPath = AreaPath,
            ParentId = ParentId,
            IsSeed = isSeed,
            SeedCreatedAt = SeedCreatedAt,
            LastSyncedAt = LastSyncedAt,
        };

        if (Revision > 0) copy.MarkSynced(Revision);
        copy.ImportFields(Fields);

        return copy;
    }

    // ── Seed factory ────────────────────────────────────────────────

    /// <summary>
    /// Initializes the seed ID counter so that the next <see cref="CreateSeed"/>
    /// call produces an ID below all existing seeds. Thread-safe via
    /// <see cref="Interlocked.Exchange"/>.
    /// </summary>
    public static void InitializeSeedCounter(int minExistingId)
    {
        Interlocked.Exchange(ref _seedIdCounter, Math.Min(minExistingId, 0));
    }

    /// <summary>
    /// Creates a seed work item (not yet persisted to ADO).
    /// Each seed receives a unique negative sentinel ID via thread-safe counter.
    /// Optional <paramref name="parentId"/>, <paramref name="areaPath"/>, and
    /// <paramref name="iterationPath"/> are inherited from the parent context when provided.
    /// </summary>
    public static WorkItem CreateSeed(
        WorkItemType type,
        string title,
        int? parentId = null,
        AreaPath areaPath = default,
        IterationPath iterationPath = default,
        string? assignedTo = null)
    {
        var seed = new WorkItem
        {
            Id = Interlocked.Decrement(ref _seedIdCounter),
            Type = type,
            Title = title,
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            ParentId = parentId,
            AreaPath = areaPath,
            IterationPath = iterationPath,
            AssignedTo = assignedTo,
        };

        if (!string.IsNullOrWhiteSpace(assignedTo))
            seed.SetField("System.AssignedTo", assignedTo);

        return seed;
    }
}
