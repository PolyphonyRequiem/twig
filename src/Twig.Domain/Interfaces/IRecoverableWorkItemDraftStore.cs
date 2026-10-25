using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Shared durable owner for one recoverable draft per qualified item identity.
/// </summary>
internal interface IRecoverableWorkItemDraftStore
{
    Task<RecoverableWorkItemDraft?> GetAsync(QualifiedWorkItemKey itemKey, CancellationToken ct = default);

    /// <summary>
    /// Atomically stores a newer draft only when the persisted generation still equals
    /// <paramref name="expectedGeneration"/>. A null expectation means the key is absent.
    /// </summary>
    Task<bool> TrySaveAsync(
        RecoverableWorkItemDraft draft,
        long? expectedGeneration,
        CancellationToken ct = default);
}
