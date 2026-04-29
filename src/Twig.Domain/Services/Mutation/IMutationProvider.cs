using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Mutation;

/// <summary>
/// Abstraction for applying field and state mutations to work items.
/// Implementations decide the target (local SQLite for seeds, ADO REST for published items).
/// </summary>
public interface IMutationProvider
{
    Task<MutationResult> UpdateFieldAsync(int itemId, FieldChange change, CancellationToken ct);
    Task<MutationResult> ChangeStateAsync(int itemId, FieldChange stateChange, CancellationToken ct);
}
