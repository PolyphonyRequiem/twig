using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// On-disk shape for one system-local claim row (AB#737 §Record shape). The
/// document is stored verbatim in <c>system.db.claims.record_json</c> and
/// round-trips through the source-generated context
/// (<see cref="Twig.Infrastructure.Serialization.TwigJsonContext"/>) so the
/// AOT contract (<c>PublishAot=true</c>,
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c>) is preserved.
/// <para>
/// Every field mirrors AB#737 §Record shape 1:1. <c>schemaVersion</c> is the
/// on-disk shape version — readers refuse a higher version rather than
/// interpret unknown fields (AB#737 §JSON encoding). The property naming
/// policy is <see cref="System.Text.Json.JsonNamingPolicy.CamelCase"/> so
/// the wire keys match the design spec's <c>lowerCamelCase</c> field names.
/// </para>
/// <para>
/// <see cref="System.Text.Json.Serialization.JsonUnmappedMemberHandlingAttribute"/>
/// with <see cref="System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow"/>
/// realizes the "unknown fields on read are rejected" rule at the
/// serializer boundary — the deserialize call throws a
/// <see cref="System.Text.Json.JsonException"/> and the claim service
/// translates that to <c>SchemaDrift</c>. AB#737 §JSON encoding forbids
/// silently ignoring unknown fields.
/// </para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ClaimRecordDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("claimId")] string ClaimId,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("connectionRef")] string ConnectionRef,
    [property: JsonPropertyName("primaryScopeId")] string PrimaryScopeId,
    [property: JsonPropertyName("primaryScopeKind")] string PrimaryScopeKind,
    [property: JsonPropertyName("holderIdentity")] string HolderIdentity,
    [property: JsonPropertyName("holderDisplay")] string? HolderDisplay,
    [property: JsonPropertyName("worktreeFingerprint")] string WorktreeFingerprint,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("leaseGeneration")] int LeaseGeneration,
    [property: JsonPropertyName("expiresAt")] string? ExpiresAt,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("activatedAt")] string? ActivatedAt,
    [property: JsonPropertyName("releasedAt")] string? ReleasedAt,
    [property: JsonPropertyName("supersededByClaimId")] string? SupersededByClaimId,
    [property: JsonPropertyName("releaseReason")] string? ReleaseReason,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("casToken")] string CasToken)
{
    /// <summary>Current on-disk schema version this reader/writer understands.</summary>
    public const int CurrentSchemaVersion = 1;
}
