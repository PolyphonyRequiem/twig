using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Commands;

/// <summary>
/// Command that can be enqueued against a <see cref="WorkItem"/> aggregate.
/// </summary>
public interface IWorkItemCommand
{
    /// <summary>Mutates the target work item.</summary>
    void Execute(WorkItem target);

    /// <summary>
    /// Returns the <see cref="FieldChange"/> this command represents,
    /// or <c>null</c> if the command does not produce a field change (e.g. notes).
    /// </summary>
    /// <remarks>
    /// <b>Precondition</b>: <see cref="Execute"/> must be called before this method.
    /// If called before <see cref="Execute"/>, state-capturing commands (e.g.
    /// <c>ChangeStateCommand</c>, <c>UpdateFieldCommand</c>) will produce a
    /// <see cref="FieldChange"/> with a <c>null</c> old value, which may be misleading.
    /// </remarks>
    FieldChange? ToFieldChange();
}
