using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Commands;

internal static class ConflictRetryHelper
{
    /// <summary>
    /// Attempts to patch a work item; on <see cref="AdoConflictException"/>
    /// re-fetches the current revision and retries exactly once.
    /// A second conflict is rethrown for the caller to handle.
    /// </summary>
    /// <returns>The new revision number after a successful patch.</returns>
    internal static async Task<int> PatchWithRetryAsync(
        IAdoWorkItemService adoService,
        int itemId,
        IReadOnlyList<FieldChange> changes,
        int expectedRevision,
        CancellationToken ct)
    {
        try
        {
            return await adoService.PatchAsync(itemId, changes, expectedRevision, ct);
        }
        catch (AdoConflictException)
        {
        }

        var freshItem = await adoService.FetchAsync(itemId, ct);

        // Attempt 2 — let any exception (including AdoConflictException) propagate
        return await adoService.PatchAsync(itemId, changes, freshItem.Revision, ct);
    }
}
