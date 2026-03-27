namespace Twig.Domain.ValueObjects;

/// <summary>
/// Metadata for a global process profile stored at
/// <c>~/.twig/profiles/{org}/{process}/profile.json</c>.
/// </summary>
public sealed record ProfileMetadata(
    string Organization,
    string ProcessTemplate,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSyncedAt,
    string FieldDefinitionHash,
    int FieldCount);
