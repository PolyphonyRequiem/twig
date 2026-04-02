using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Pushes pending field changes and notes for a set of work items to Azure DevOps.
/// </summary>
public interface IPendingChangeFlusher
{
    /// <summary>
    /// Flushes pending changes for the specified item IDs.
    /// </summary>
    Task<FlushResult> FlushAsync(
        IReadOnlyList<int> itemIds,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default);

    /// <summary>
    /// Flushes pending changes for all dirty items.
    /// </summary>
    Task<FlushResult> FlushAllAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default);
}

/// <summary>Structured result of a flush operation.</summary>
public sealed record FlushResult(
    int ItemsFlushed,
    int FieldChangesPushed,
    int NotesPushed,
    IReadOnlyList<FlushItemFailure> Failures);

/// <summary>Per-item failure detail for callers to render.</summary>
public sealed record FlushItemFailure(int ItemId, string Error);
