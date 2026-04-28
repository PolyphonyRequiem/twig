using Twig.Domain.Interfaces;
using Twig.Domain.Services.Process;

namespace Twig.Domain.Services.Field;

/// <summary>
/// Fetches field definitions from ADO and persists them to the local store.
/// Static class with method parameters — same pattern as <see cref="ProcessTypeSyncService"/>.
/// </summary>
public static class FieldDefinitionSyncService
{
    /// <summary>
    /// Fetches field definitions from <paramref name="iterationService"/> and saves them
    /// via <paramref name="fieldDefinitionStore"/>. Returns the count of definitions synced.
    /// Does not catch exceptions — callers handle errors.
    /// </summary>
    public static async Task<int> SyncAsync(
        IIterationService iterationService,
        IFieldDefinitionStore fieldDefinitionStore,
        CancellationToken ct = default)
    {
        var definitions = await iterationService.GetFieldDefinitionsAsync(ct);

        if (definitions.Count == 0)
            return 0;

        await fieldDefinitionStore.SaveBatchAsync(definitions, ct);
        return definitions.Count;
    }
}
