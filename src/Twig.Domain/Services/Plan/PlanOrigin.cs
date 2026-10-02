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
    string? AdoPrincipalId);
