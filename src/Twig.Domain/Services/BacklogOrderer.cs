using System.Globalization;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Best-effort backlog ordering: positions a newly published item after the last
/// sibling under its parent by setting the process-specific ordering field
/// (StackRank for Agile, BacklogPriority for Scrum/CMMI).
/// Failure is non-fatal — any exception is caught and returns false.
/// </summary>
public sealed class BacklogOrderer
{
    private const string StackRankField = "Microsoft.VSTS.Common.StackRank";
    private const string BacklogPriorityField = "Microsoft.VSTS.Common.BacklogPriority";

    private readonly IAdoWorkItemService _adoService;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;

    public BacklogOrderer(
        IAdoWorkItemService adoService,
        IFieldDefinitionStore fieldDefinitionStore)
    {
        _adoService = adoService;
        _fieldDefinitionStore = fieldDefinitionStore;
    }

    /// <summary>
    /// Attempts to set the backlog ordering field on the published item so it
    /// appears after its siblings under <paramref name="parentId"/>.
    /// </summary>
    /// <param name="itemId">The positive ADO ID of the newly published item.</param>
    /// <param name="parentId">The parent work item ID, or null if no parent.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the ordering field was set; false on no-op or failure.</returns>
    public async Task<bool> TryOrderAsync(int itemId, int? parentId, CancellationToken ct = default)
    {
        try
        {
            // Step 1: No parent → no sibling ordering possible
            if (parentId is null)
                return false;

            // Step 2: Detect process-specific ordering field
            string? orderingField = null;
            if (await _fieldDefinitionStore.GetByReferenceNameAsync(StackRankField, ct) is not null)
                orderingField = StackRankField;
            else if (await _fieldDefinitionStore.GetByReferenceNameAsync(BacklogPriorityField, ct) is not null)
                orderingField = BacklogPriorityField;
            else
                return false;

            // Step 3: Fetch siblings (children of the parent)
            var siblings = await _adoService.FetchChildrenAsync(parentId.Value, ct);

            // Step 4: Find the maximum ordering field value among siblings (excluding the new item itself)
            var maxValue = 0.0;
            foreach (var sibling in siblings)
            {
                if (sibling.Id == itemId)
                    continue;

                if (sibling.Fields.TryGetValue(orderingField, out var rawValue)
                    && rawValue is not null
                    && double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    if (parsed > maxValue)
                        maxValue = parsed;
                }
            }

            // Step 5: Set the new item's ordering field to maxValue + 1.0
            var newValue = (maxValue + 1.0).ToString(CultureInfo.InvariantCulture);

            // Step 6: Fetch the item to get its current revision for the patch
            var item = await _adoService.FetchAsync(itemId, ct);
            var changes = new[] { new FieldChange(orderingField, null, newValue) };

            await _adoService.PatchAsync(itemId, changes, item.Revision, ct);

            return true;
        }
        catch
        {
            // Best-effort: any failure is swallowed (caller logs warning)
            return false;
        }
    }

}
