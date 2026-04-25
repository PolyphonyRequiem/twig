namespace Twig.Domain.ValueObjects;

/// <summary>
/// Immutable snapshot of a published work item's identity and field values,
/// used as the round-trip carrier between <c>WorkItemExportFormat.Generate</c>
/// and <c>WorkItemExportFormat.Parse</c>.
/// </summary>
public sealed record ExportedWorkItem(
    int Id,
    int Revision,
    string TypeName,
    IReadOnlyDictionary<string, string?> Fields);
