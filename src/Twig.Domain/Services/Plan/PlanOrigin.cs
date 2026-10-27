namespace Twig.Domain.Services.Plan;

/// <summary>Native authority captured at preview, never supplied in Plan JSON or adopted on resume.</summary>
public sealed record PlanOrigin(
    string WorktreeRoot,
    string WorktreeFingerprint,
    long AttachmentRevision,
    string ConnectionRef,
    string BindingId,
    long BindingRevision,
    string SelectionSource,
    long SelectionRevision,
    string IdentityId,
    string Method,
    string CredentialRef,
    string TenantId,
    string ObjectId,
    string Issuer,
    string Authority,
    string? AdoPrincipalId)
{
    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    internal ProviderPlanAuthority? ProviderAuthority { get; init; }
}

/// <summary>Provider-neutral target and authenticated subject captured for native relationship proposals.</summary>
internal sealed record ProviderPlanAuthority(
    Twig.Domain.ValueObjects.ProviderTargetIdentity Target, string ActorId, string CredentialAccount, string Endpoint);
