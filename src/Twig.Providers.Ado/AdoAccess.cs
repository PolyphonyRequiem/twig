using System.Net.Http.Headers;
using Twig.Core;

namespace Twig.Providers.Ado;

// Host seam: a lease must hold the native connection/actor admission for the entire operation.
// There is deliberately no token-only overload or permissive default write admission.
public abstract class AdoAccess
{
    public abstract Task<Result<AdoSession>> AcquireAsync(Connection connection, CancellationToken ct);
}

public abstract class AdoSession : IDisposable
{
    public abstract Connection Connection { get; }
    public abstract Uri OrganizationUrl { get; }
    public abstract string PrincipalId { get; }
    public abstract AuthenticationHeaderValue Authorization { get; }
    public abstract Task<Result<AdoWriteAdmission>> AdmitAsync(AdoWriteIntent intent, CancellationToken ct);
    public abstract void Dispose();
}

public sealed record AdoWriteIntent(Proposal Proposal, int Operation, Guid Attempt, Uri Target,
    string Method, string MediaType, string Payload);

public abstract class AdoWriteAdmission : IDisposable
{
    // Must durably retain the native response under this exact request/binding, not settle an unknown by retrying.
    public abstract Task ObserveAsync(int statusCode, string body, CancellationToken ct);
    public abstract void Dispose();
}
