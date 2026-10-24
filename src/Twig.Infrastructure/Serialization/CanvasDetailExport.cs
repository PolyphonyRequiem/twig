using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Serialization;

/// <summary>
/// Read-only semantic detail, not a mutation capture. Layout flags convey server
/// facts, never permission to save. Related targets and asset authority are not exported.
/// </summary>
internal sealed record CanvasDetailExport(
    int Version,
    QualifiedWorkItemKey ItemKey,
    string BenchId,
    string BenchName,
    string IdentityId,
    long BindingRevision,
    long SelectionRevision,
    string? StorageGeneration,
    WorkItemSnapshot Item,
    bool HasLocalChanges,
    DateTimeOffset? LinksVerifiedAt,
    string LayoutStatus,
    string LayoutSource,
    string? LockedTypeReferenceName,
    WorkItemDetailDocument Document,
    WorkItemTypeAppearance? Appearance)
{
    public string Editing => "unavailable";
    public string RelatedItemsCoverage => "not-exported";
    public string AppearanceSource => Appearance is null ? "unavailable" : "cached-process-metadata";
    public string IconAssetAuthority => "unavailable";
}
