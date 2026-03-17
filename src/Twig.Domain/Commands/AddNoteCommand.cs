using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Commands;

/// <summary>
/// Command that appends a pending note to a work item.
/// Does not produce a <see cref="FieldChange"/>.
/// </summary>
public sealed class AddNoteCommand : IWorkItemCommand
{
    public PendingNote Note { get; }

    public AddNoteCommand(PendingNote note)
    {
        Note = note;
    }

    public void Execute(WorkItem target)
    {
        target.AddPendingNote(Note);
    }

    /// <summary>Notes do not map to field changes.</summary>
    public FieldChange? ToFieldChange() => null;
}
