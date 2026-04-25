using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Commands;

/// <summary>
/// Command that sets an arbitrary field value on a work item.
/// </summary>
public sealed class UpdateFieldCommand : IWorkItemCommand
{
    public string FieldName { get; }
    public string? Value { get; }

    private string? _oldValue;

    public UpdateFieldCommand(string fieldName, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        FieldName = fieldName;
        Value = value;
    }

    public void Execute(WorkItem target)
    {
        target.TryGetField(FieldName, out _oldValue);
        target.SetField(FieldName, Value);
    }

    public FieldChange? ToFieldChange() =>
        new FieldChange(FieldName, _oldValue, Value);
}
