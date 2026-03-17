using System.Collections.ObjectModel;
using Twig.Domain.Commands;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Aggregates;

/// <summary>
/// Root aggregate for an Azure DevOps work item.
/// Supports a command-queue pattern: enqueue mutations via
/// <see cref="ChangeState"/>, <see cref="UpdateField"/>, <see cref="AddNote"/>,
/// then flush them atomically with <see cref="ApplyCommands"/>.
/// </summary>
public sealed class WorkItem
{
    private static int _seedIdCounter;

    private readonly Queue<IWorkItemCommand> _commandQueue = new();
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

    // ── Arbitrary field storage (read-only public surface) ──────────
    public IReadOnlyDictionary<string, string?> Fields => _fieldsView;

    // ── Pending notes (read-only public surface) ────────────────────
    public IReadOnlyList<PendingNote> PendingNotes => _pendingNotesView;

    // ── Internal mutators for commands (same assembly) ──────────────

    internal void SetField(string fieldName, string? value) => _fields[fieldName] = value;

    internal bool TryGetField(string fieldName, out string? value) => _fields.TryGetValue(fieldName, out value);

    internal void AddPendingNote(PendingNote note) => _pendingNotes.Add(note);

    // ── Command enqueueing ──────────────────────────────────────────

    /// <summary>Enqueues a state change command.</summary>
    public void ChangeState(string newState, string? confirmation = null)
    {
        _commandQueue.Enqueue(new ChangeStateCommand(newState, confirmation));
        IsDirty = true;
    }

    /// <summary>Enqueues an arbitrary field update command.</summary>
    public void UpdateField(string fieldName, string? value)
    {
        _commandQueue.Enqueue(new UpdateFieldCommand(fieldName, value));
        IsDirty = true;
    }

    /// <summary>Enqueues an add-note command.</summary>
    public void AddNote(PendingNote note)
    {
        _commandQueue.Enqueue(new AddNoteCommand(note));
        IsDirty = true;
    }

    // ── Command application ─────────────────────────────────────────

    /// <summary>
    /// Dequeues and executes all pending commands.
    /// Returns non-null <see cref="FieldChange"/> entries (notes are filtered out).
    /// </summary>
    public IReadOnlyList<FieldChange> ApplyCommands()
    {
        var changes = new List<FieldChange>();

        while (_commandQueue.TryDequeue(out var command))
        {
            command.Execute(this);
            var change = command.ToFieldChange();
            if (change.HasValue)
            {
                changes.Add(change.Value);
            }
        }

        return changes;
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

    // ── Seed factory ────────────────────────────────────────────────

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
        IterationPath iterationPath = default)
    {
        return new WorkItem
        {
            Id = Interlocked.Decrement(ref _seedIdCounter),
            Type = type,
            Title = title,
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            ParentId = parentId,
            AreaPath = areaPath,
            IterationPath = iterationPath,
        };
    }
}
