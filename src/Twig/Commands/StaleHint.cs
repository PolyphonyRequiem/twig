namespace Twig.Commands;

/// <summary>
/// Renders the human-facing hint for a <see cref="Twig.Domain.Services.Sync.Stale"/> read outcome.
/// </summary>
/// <remarks>
/// Wayfinder 0004 §3 removed the staleness-triggered fetch: a read reports how old the cache is
/// and each surface decides what that means. The rich CLI's decision is this hint — it tells the
/// user the age and names the explicit command that refreshes, rather than silently spending a
/// network round-trip on their behalf. Machine formats deliberately render nothing so a scripted
/// read keeps a stable, network-free contract.
/// </remarks>
internal static class StaleHint
{
    /// <summary>
    /// Builds the hint text for a cache entry last synced at <paramref name="lastSyncedAt"/>.
    /// A <c>null</c> timestamp means the cache never recorded a sync for the item.
    /// </summary>
    public static string Format(DateTimeOffset? lastSyncedAt)
    {
        if (lastSyncedAt is null)
            return "hint: showing cached data (never synced) — run 'twig refresh' or pass --refresh for fresh data.";

        var age = DateTimeOffset.UtcNow - lastSyncedAt.Value;
        return $"hint: showing cached data from {Describe(age)} — run 'twig refresh' or pass --refresh for fresh data.";
    }

    private static string Describe(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;

        if (age.TotalMinutes < 1) return "less than a minute ago";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
