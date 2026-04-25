namespace Twig.Domain.Interfaces;

/// <summary>
/// Repository contract for recording seed-to-ADO ID mappings after publish.
/// </summary>
public interface IPublishIdMapRepository
{
    Task RecordMappingAsync(int oldId, int newId, CancellationToken ct = default);
    Task<int?> GetNewIdAsync(int oldId, CancellationToken ct = default);
    Task<IReadOnlyList<(int OldId, int NewId)>> GetAllMappingsAsync(CancellationToken ct = default);
}
