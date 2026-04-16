using Twig.Domain.Interfaces;

namespace Twig.Domain.Services;

/// <summary>
/// Holds two pre-built <see cref="SyncCoordinator"/> instances with different cache staleness
/// thresholds: <see cref="ReadOnly"/> for display commands (longer TTL) and <see cref="ReadWrite"/>
/// for mutating commands (shorter TTL). Preserves the <see cref="SyncCoordinator"/> constructor
/// signature (DD-13) while enabling tiered TTLs (Issue #1614).
/// </summary>
public sealed class SyncCoordinatorFactory
{
    public SyncCoordinatorFactory(
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        ProtectedCacheWriter protectedCacheWriter,
        IPendingChangeStore pendingChangeStore,
        IWorkItemLinkRepository? linkRepo,
        int readOnlyStaleMinutes,
        int readWriteStaleMinutes)
    {
        // Clamp: RO TTL must be >= RW TTL so display commands never refresh more aggressively than mutating ones.
        if (readOnlyStaleMinutes < readWriteStaleMinutes)
            readOnlyStaleMinutes = readWriteStaleMinutes;

        ReadOnly = new SyncCoordinator(workItemRepo, adoService, protectedCacheWriter,
            pendingChangeStore, linkRepo, readOnlyStaleMinutes);
        ReadWrite = new SyncCoordinator(workItemRepo, adoService, protectedCacheWriter,
            pendingChangeStore, linkRepo, readWriteStaleMinutes);
    }

    /// <summary>
    /// Coordinator with a longer cache TTL — for read-only display commands
    /// (<c>status</c>, <c>tree</c>, <c>show</c>).
    /// </summary>
    public SyncCoordinator ReadOnly { get; }

    /// <summary>
    /// Coordinator with the standard (shorter) cache TTL — for mutating commands
    /// (<c>set</c>, <c>link</c>, <c>refresh</c>).
    /// </summary>
    public SyncCoordinator ReadWrite { get; }
}
