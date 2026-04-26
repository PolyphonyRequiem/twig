using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Commands;

/// <summary>
/// Command that transitions a work item to a new state.
/// </summary>
public sealed class ChangeStateCommand : IWorkItemCommand
{
    public string NewState { get; }

    // Captured during Execute() for use in ToFieldChange(). This makes the command
    // stateful after execution; that is intentional — commands are dequeued and
    // executed exactly once, so post-execution state is always consistent.
    private string? _oldState;

    public ChangeStateCommand(string newState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newState);
        NewState = newState;
    }

    public void Execute(WorkItem target)
    {
        _oldState = target.State;
        target.State = NewState;
    }

    public FieldChange? ToFieldChange() =>
        new FieldChange("System.State", _oldState, NewState);
}
