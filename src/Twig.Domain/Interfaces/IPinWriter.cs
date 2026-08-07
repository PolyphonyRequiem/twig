using Twig.Domain.Enums;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Writes pins to the current Bench (ADO #146).
/// <para>
/// 🔴 Paired with <see cref="IPinReader"/> so reads and writes cannot drift onto different
/// stores. Splitting them was the defect this ticket removed: the tracking file was written by
/// one path and read by another, and the disagreement surfaced only as a pin that silently was
/// not there.
/// </para>
/// </summary>
public interface IPinWriter
{
    /// <summary>Adds a pin to the current Bench. Idempotent.</summary>
    Task AddPinAsync(int workItemId, TrackingMode mode, CancellationToken ct = default);

    /// <summary>
    /// Removes every pin naming this item from the current Bench — item AND subtree, since the
    /// caller asked to stop following the item and need not know which kind exists.
    /// Returns whether anything was actually removed.
    /// </summary>
    Task<bool> RemovePinAsync(int workItemId, CancellationToken ct = default);
}
