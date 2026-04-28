using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Abstracts the pending-change flush loop so callers (SyncCommand)
/// can be tested with a mock rather than coupling to ADO service internals.
/// </summary>
public interface IPendingChangeFlusher
{
    /// <summary>
    /// Flushes pending field changes and notes for the specified item IDs to Azure DevOps.
    /// </summary>
    /// <remarks>
    /// Note batch flush is the fallback path for residual staged notes — those created by
    /// <c>NoteCommand</c>'s offline fallback (<c>StageLocallyAsync</c>) or accumulated during
    /// seed authoring before publish. Under normal online operation, <c>NoteCommand</c> pushes
    /// notes immediately (push-on-write) and <see cref="Twig.Infrastructure.Ado.AutoPushNotesHelper"/>
    /// flushes any stragglers during <c>update</c>/<c>state</c>/<c>edit</c>.
    /// </remarks>
    Task<FlushResult> FlushAsync(
        IReadOnlyList<int> itemIds,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default);

    /// <summary>Flushes pending changes for all dirty items.</summary>
    Task<FlushResult> FlushAllAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default);
}
