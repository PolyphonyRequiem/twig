namespace Twig.Domain.ValueObjects;

/// <summary>
/// Cached metadata for an ADO work item field — sourced from the
/// <c>GET /{project}/_apis/wit/fields</c> endpoint or derived from the reference name.
/// </summary>
public sealed record FieldDefinition(
    string ReferenceName,
    string DisplayName,
    string DataType,
    bool IsReadOnly);
