namespace Twig.Domain.ReadModels;

/// <summary>
/// Aggregate cache statistics from the local SQLite store.
/// Used by the <c>twig_cache_status</c> MCP tool to report cache freshness without network calls.
/// </summary>
public sealed record CacheStatistics(
    int TrackedItemCount,
    DateTimeOffset? NewestSyncUtc,
    DateTimeOffset? OldestSyncUtc);
