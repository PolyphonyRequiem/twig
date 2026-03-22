using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Stores and retrieves field definition metadata cached from the ADO
/// <c>GET /{project}/_apis/wit/fields</c> endpoint.
/// </summary>
public interface IFieldDefinitionStore
{
    Task<FieldDefinition?> GetByReferenceNameAsync(string referenceName, CancellationToken ct = default);
    Task<IReadOnlyList<FieldDefinition>> GetAllAsync(CancellationToken ct = default);
    Task SaveBatchAsync(IReadOnlyList<FieldDefinition> definitions, CancellationToken ct = default);
}
