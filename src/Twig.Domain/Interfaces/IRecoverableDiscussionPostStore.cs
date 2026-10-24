using Twig.Domain.Services.Discussion;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Durable operation journal for explicit discussion posts. The caller must persist the
/// Dispatching generation before the remote effect and must reconcile unresolved operations
/// rather than dispatching them again.
/// </summary>
internal interface IRecoverableDiscussionPostStore
{
    Task<RecoverableDiscussionPost?> GetAsync(Guid operationId, CancellationToken ct = default);

    Task<IReadOnlyList<RecoverableDiscussionPost>> GetUnresolvedAsync(
        QualifiedWorkItemKey itemKey,
        CancellationToken ct = default);

    Task<IReadOnlyList<RecoverableDiscussionPost>> GetJournalAsync(
        Guid operationId,
        CancellationToken ct = default);

    /// <summary>Creates a generation-zero Prepared intent before any remote request.</summary>
    Task<bool> TryCreateAsync(RecoverableDiscussionPost post, CancellationToken ct = default);

    /// <summary>
    /// Atomically advances one legal state transition and appends its immutable journal entry.
    /// A false result means the caller lost the generation race and must not dispatch.
    /// </summary>
    Task<bool> TrySaveAsync(
        RecoverableDiscussionPost post,
        long expectedGeneration,
        CancellationToken ct = default);
}
