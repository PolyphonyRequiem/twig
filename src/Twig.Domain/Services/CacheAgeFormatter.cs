namespace Twig.Domain.Services;

/// <summary>
/// Formats the age of cached data as a human-readable string.
/// Returns <c>null</c> when the cache is fresh (within the stale threshold)
/// or when no sync timestamp is available.
/// </summary>
public static class CacheAgeFormatter
{
    /// <summary>
    /// Formats the elapsed time since <paramref name="lastSyncedAt"/> as a compact
    /// age string such as <c>"(cached 3m ago)"</c>, <c>"(cached 2h ago)"</c>, or
    /// <c>"(cached 1d ago)"</c>.
    /// </summary>
    /// <param name="lastSyncedAt">The timestamp of the last successful sync, or <c>null</c>.</param>
    /// <param name="staleMinutes">Number of minutes before data is considered stale.</param>
    /// <returns>
    /// A formatted age string when data is stale; <c>null</c> when fresh or unknown.
    /// </returns>
    public static string? Format(DateTimeOffset? lastSyncedAt, int staleMinutes)
    {
        return Format(lastSyncedAt, staleMinutes, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Testable overload that accepts an explicit <paramref name="now"/> timestamp.
    /// </summary>
    internal static string? Format(DateTimeOffset? lastSyncedAt, int staleMinutes, DateTimeOffset now)
    {
        if (lastSyncedAt is null)
            return null;

        var elapsed = now - lastSyncedAt.Value;

        if (elapsed.TotalMinutes < staleMinutes)
            return null;

        var unit = elapsed.TotalDays >= 1 ? $"{(int)elapsed.TotalDays}d"
                 : elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h"
                 : $"{(int)elapsed.TotalMinutes}m";

        return $"(cached {unit} ago)";
    }
}
