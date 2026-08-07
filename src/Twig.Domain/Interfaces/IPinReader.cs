using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Reads the pins on the current Bench, for callers that need them as pins rather than as
/// evaluated membership — chiefly the sync path, which refreshes each tree pin's subtree.
/// <para>
/// 🔴 This exists so the sync path stops reading the tracking FILE. The file was the source of
/// truth before ADO #145; keeping a second reader of it after the Bench became the source is how
/// the two silently disagree, and the disagreement only shows up as a tracked tree that quietly
/// stopped refreshing.
/// </para>
/// </summary>
public interface IPinReader
{
    /// <summary>
    /// The pins on the current Bench, as item/subtree pairs. Derived from the Bench's item and
    /// subtree selectors; query selectors are not pins and are not returned.
    /// </summary>
    Task<IReadOnlyList<TrackedItem>> GetPinsAsync(CancellationToken ct = default);
}
