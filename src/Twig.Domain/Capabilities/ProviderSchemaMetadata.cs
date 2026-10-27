using Twig.Domain.Enums;

namespace Twig.Domain.Capabilities;

internal enum ProviderSchemaDiscoveryAvailability
{
    Available,
    Unavailable,
    Unknown,
}

internal enum ProviderSchemaElementSupport
{
    Supported,
    Unsupported,
    Unknown,
}

internal sealed record ProviderSchemaTypeMetadata(
    string Name,
    ProviderSchemaElementSupport Support,
    string? ReasonCode);

internal sealed record ProviderSchemaFieldMetadata(
    string Name,
    string? TypeName,
    ProviderSchemaElementSupport Support,
    string? ReasonCode);

internal sealed record ProviderSchemaStateMetadata(
    string Name,
    ProviderSchemaElementSupport Support,
    IReadOnlyList<string> Values,
    string? ReasonCode);

internal sealed record ProviderSchemaRelationshipMetadata(
    LinkKind Kind,
    ProviderSchemaElementSupport Support,
    IReadOnlyList<string> Fields,
    string? ReasonCode);

internal sealed record ProviderSchemaRuleSupport(
    ProviderSchemaElementSupport Process,
    string ProcessReasonCode,
    ProviderSchemaElementSupport Fields,
    string FieldsReasonCode);

/// <summary>
/// Runtime schema evidence kept separate from process policy. Unsupported means the provider
/// contract or an authoritatively discovered schema excludes the feature; Unknown means
/// discovery could not establish support.
/// </summary>
internal sealed record ProviderSchemaMetadata(
    string ProviderKind,
    ProviderSchemaDiscoveryAvailability Availability,
    string? ReasonCode,
    IReadOnlyList<ProviderSchemaTypeMetadata> Types,
    IReadOnlyList<ProviderSchemaFieldMetadata> Fields,
    ProviderSchemaStateMetadata States,
    IReadOnlyList<ProviderSchemaRelationshipMetadata> Relationships,
    ProviderSchemaRuleSupport Rules);
