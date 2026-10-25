using Twig.Domain.ValueObjects.Fields;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// A provider observation, independent of local IDs, local CAS revisions and the ADO mirror.
/// Relationships are native observations; origin is derived from the adapter's capability descriptor.
/// </summary>
internal sealed record ProviderWorkItemSnapshot(
    ExternalWorkItemIdentity Identity,
    ProviderRevisionToken? RevisionToken,
    FieldValues Fields,
    IReadOnlyList<ProviderWorkItemRelationship> Relationships,
    bool RelationshipsComplete);
