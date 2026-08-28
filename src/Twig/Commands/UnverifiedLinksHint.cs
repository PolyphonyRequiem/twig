namespace Twig.Commands;

/// <summary>
/// Renders the human-facing hint for an edge set this cache has never fetched (AB#831).
/// </summary>
/// <remarks>
/// The sibling of <see cref="StaleHint"/>, and deliberately a separate signal. Staleness answers
/// "how old is this item?"; this answers "has anyone ever asked ADO what this item is blocked by?"
/// — and before AB#831 the answer to the second was silently rendered as "nothing blocks it".
/// <para>
/// Human surfaces only, for the same reason <see cref="StaleHint"/> is: a machine read keeps its
/// quiet, network-free contract and receives the identical signal structurally, as the
/// <c>linksVerifiedAt</c> key being <c>null</c>.
/// </para>
/// </remarks>
internal static class UnverifiedLinksHint
{
    /// <summary>
    /// Builds the hint text for a work item whose edge set has never been read from ADO.
    /// </summary>
    public static string Format(int workItemId) =>
        $"hint: this cache has never fetched #{workItemId}'s links — an empty relations list here " +
        "means UNKNOWN, not none. Run 'twig refresh', or pass --refresh, before trusting it.";
}
