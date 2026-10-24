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

    Invalid,
    Conflict,
}

internal sealed record LocalRelationshipResult(
    LocalRelationshipStatus Status,
    LocalRelationship? Relationship,
    RelationshipRoute Route,
    string? ReasonCode = null);

/// <summary>
/// Writes Twig-local relationships, and only for kinds the provider declares as emulated.
/// It never acts as a fallback for a native kind.
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
        if (source == target)
            return new(LocalRelationshipStatus.Invalid, null, route, SelfEdgeReason);

        (source, target) = LocalRelationship.Normalize(kind, source, target);

        var existing = await repository.GetAsync(source, target, kind, ct).ConfigureAwait(false);
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
            route.Storage == EmulationStorage.LocalWithProviderProjection ? PublicationState.Pending : PublicationState.NotApplicable,
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

        (source, target) = LocalRelationship.Normalize(kind, source, target);

        var existing = await repository.GetAsync(source, target, kind, ct).ConfigureAwait(false);
        if (existing is null or { IsDeleted: true })
            return new(LocalRelationshipStatus.NotPresent, existing, route);

        var write = await repository.TombstoneAsync(source, target, kind, existing.Revision, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        return write.Status == LocalRelationshipWriteStatus.Written
            ? new(LocalRelationshipStatus.Removed, write.Current, route)
            : new(LocalRelationshipStatus.Conflict, write.Current, route);
    }
}
