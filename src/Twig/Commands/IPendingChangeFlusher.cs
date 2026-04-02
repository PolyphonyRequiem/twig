using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Abstracts the pending-change flush loop so callers (SaveCommand, SyncCommand, FlowDoneCommand)
/// can be tested with a mock rather than coupling to ADO service internals.
/// </summary>
public interface IPendingChangeFlusher
{
    /// <summary>Flushes pending changes for the specified item IDs.</summary>
    Task<FlushResult> FlushAsync(
        IReadOnlyList<int> itemIds,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default);

    /// <summary>Flushes pending changes for all dirty items.</summary>
    Task<FlushResult> FlushAllAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default);
}
