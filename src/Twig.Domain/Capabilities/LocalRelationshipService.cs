using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Capabilities;

internal enum LocalRelationshipStatus
{
    Added,
    Removed,
    AlreadyPresent,
    NotPresent,

    /// <summary>The kind is not emulated for this provider; route it natively or refuse it.</summary>
    NotEmulated,

    /// <summary>The explicit local-only route is only available for an unsupported kind.</summary>
    NotLocalOnly,

    /// <summary>An edge exists, but it is stored under a different relationship route.</summary>
    StorageMismatch,

    Invalid,
    Conflict,
}

internal sealed record LocalRelationshipResult(
    LocalRelationshipStatus Status,
    LocalRelationship? Relationship,
    RelationshipRoute Route,
    string? ReasonCode = null);

/// <summary>
/// Writes Twig-local relationships. Emulated writes require an emulated declaration; explicit
/// local-only writes require an unsupported declaration. Neither route is a fallback for native
/// or unavailable provider capabilities.
/// </summary>
internal sealed class LocalRelationshipService(ILocalRelationshipRepository repository, TimeProvider? clock = null)
{
    public const string SelfEdgeReason = "relationship-self-edge";
    public const string SecondParentReason = "relationship-second-parent";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<LocalRelationshipResult> AddAsync(
        ProviderCapabilityDescriptor descriptor, LocalWorkItemId source, LocalWorkItemId target, LinkKind kind,
        CancellationToken ct = default)
    {
        var route = RelationshipRouting.Decide(descriptor, kind);
        if (route.Kind != RelationshipRouteKind.Emulated)
            return new(LocalRelationshipStatus.NotEmulated, null, route, route.ReasonCode);
        return await AddWithRouteAsync(source, target, kind, route, requireMatchingStorage: false, ct: ct)
            .ConfigureAwait(false);
    }

    public async Task<LocalRelationshipResult> AddLocalOnlyAsync(
        ProviderCapabilityDescriptor descriptor, LocalWorkItemId source, LocalWorkItemId target, LinkKind kind,
        CancellationToken ct = default)
    {
        var route = RelationshipRouting.DecideExplicitLocalOnly(descriptor, kind);
        if (route.Kind != RelationshipRouteKind.LocalOnly)
            return new(LocalRelationshipStatus.NotLocalOnly, null, route, route.ReasonCode);
        return await AddWithRouteAsync(source, target, kind, route, requireMatchingStorage: true, ct: ct)
            .ConfigureAwait(false);
    }

    private async Task<LocalRelationshipResult> AddWithRouteAsync(
        LocalWorkItemId source,
        LocalWorkItemId target,
        LinkKind kind,
        RelationshipRoute route,
        bool requireMatchingStorage,
        CancellationToken ct)
    {
        if (source == target)
            return new(LocalRelationshipStatus.Invalid, null, route, SelfEdgeReason);

        (source, target) = LocalRelationship.Normalize(kind, source, target);

        var existing = await repository.GetAsync(source, target, kind, ct).ConfigureAwait(false);
        if (requireMatchingStorage
            && existing is not null
            && (existing.Storage != route.Storage
                || route.Storage == EmulationStorage.LocalOnly
                    && existing.Publication != PublicationState.NotApplicable))
            return new(LocalRelationshipStatus.StorageMismatch, existing, route);
        if (existing is { IsDeleted: false })
            return new(LocalRelationshipStatus.AlreadyPresent, existing, route);

        if (kind == LinkKind.ParentChild)
        {
            var edges = await repository.GetForItemsAsync([target], includeDeleted: false, ct).ConfigureAwait(false);
            if (edges.Any(e => e.Kind == LinkKind.ParentChild && e.Target == target))
                return new(LocalRelationshipStatus.Invalid, null, route, SecondParentReason);
        }

        var now = _clock.GetUtcNow();
        var candidate = new LocalRelationship(
            source,
            target,
            kind,
            route.Storage,
            route.Storage == EmulationStorage.LocalWithProviderProjection
                ? PublicationState.Pending
                : PublicationState.NotApplicable,
            Revision: 0,
            CreatedAt: now,
            UpdatedAt: now);

        var write = await repository.UpsertAsync(candidate, existing?.Revision, ct).ConfigureAwait(false);
        return write.Status == LocalRelationshipWriteStatus.Written
            ? new(LocalRelationshipStatus.Added, write.Current, route)
            : new(LocalRelationshipStatus.Conflict, write.Current, route);
    }

    public async Task<LocalRelationshipResult> RemoveAsync(
        ProviderCapabilityDescriptor descriptor, LocalWorkItemId source, LocalWorkItemId target, LinkKind kind,
        CancellationToken ct = default)
    {
        var route = RelationshipRouting.Decide(descriptor, kind);
        if (route.Kind != RelationshipRouteKind.Emulated)
            return new(LocalRelationshipStatus.NotEmulated, null, route, route.ReasonCode);
        return await RemoveByRouteAsync(source, target, kind, route, requireMatchingStorage: false, ct: ct)
            .ConfigureAwait(false);
    }

    public Task<LocalRelationshipResult> RemoveLocalOnlyAsync(
        LocalWorkItemId source, LocalWorkItemId target, LinkKind kind, CancellationToken ct = default)
    {
        var route = new RelationshipRoute(RelationshipRouteKind.LocalOnly, EmulationStorage.LocalOnly, null);
        return RemoveByRouteAsync(source, target, kind, route, requireMatchingStorage: true, ct: ct);
    }

    private async Task<LocalRelationshipResult> RemoveByRouteAsync(
        LocalWorkItemId source,
        LocalWorkItemId target,
        LinkKind kind,
        RelationshipRoute route,
        bool requireMatchingStorage,
        CancellationToken ct)
    {
        (source, target) = LocalRelationship.Normalize(kind, source, target);

        var existing = await repository.GetAsync(source, target, kind, ct).ConfigureAwait(false);
        if (requireMatchingStorage
            && existing is not null
            && (existing.Storage != route.Storage
                || route.Storage == EmulationStorage.LocalOnly
                    && existing.Publication != PublicationState.NotApplicable))
            return new(LocalRelationshipStatus.StorageMismatch, existing, route);
        if (existing is null or { IsDeleted: true })
            return new(LocalRelationshipStatus.NotPresent, existing, route);

        var write = await repository.TombstoneAsync(source, target, kind, existing.Revision, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        return write.Status == LocalRelationshipWriteStatus.Written
            ? new(LocalRelationshipStatus.Removed, write.Current, route)
            : new(LocalRelationshipStatus.Conflict, write.Current, route);
    }

    public async Task<IReadOnlyList<LocalRelationship>> GetLocalOnlyRelatedAsync(
        LocalWorkItemId itemId, CancellationToken ct = default)
    {
        var edges = await repository.GetForItemsAsync([itemId], includeDeleted: false, ct).ConfigureAwait(false);
        return edges
            .Where(edge => edge.Kind == LinkKind.Related
                && edge.Storage == EmulationStorage.LocalOnly
                && edge.Publication == PublicationState.NotApplicable)
            .OrderBy(edge => edge.Source.ToString(), StringComparer.Ordinal)
            .ThenBy(edge => edge.Target.ToString(), StringComparer.Ordinal)
            .ToArray();
    }
}
