using System.Collections.Immutable;
using System.Net.Http.Headers;
using Twig.Core;

namespace Twig.Providers.GitHub;

public enum GitHubCredentialSource { Gh, GitHubApp }
public enum GitHubValidationScope { Unspecified, NativeApiAndDeclaredTwigPolicies }

public abstract class GitHubAccess
{
    public abstract Task<Result<GitHubSession>> AcquireAsync(Connection connection, CancellationToken ct);
}

// The host holds the selected attachment, credential-acquisition context and actor lease.
// No ambient credential lookup, token-only admission or automatic grant is provided.
public abstract class GitHubSession : IDisposable
{
    public abstract Connection Connection { get; }
    public abstract Uri Endpoint { get; }
    public abstract string ActorId { get; }
    public abstract string AcquisitionContext { get; }
    public abstract GitHubCredentialSource CredentialSource { get; }
    public abstract AuthenticationHeaderValue Authorization { get; }
    public abstract Task ValidateAsync(CancellationToken ct);
    public abstract Task<Result<GitHubRuleProjection>> ReadRuleProjectionAsync(ProcessSchema nativeProjection, CancellationToken ct);
    public abstract Task<Result<GitHubWriteAdmission>> AdmitAsync(GitHubWriteIntent intent, CancellationToken ct);
    public abstract void Dispose();
}

// The scope is a product validation boundary, not authority over external repository automation.
// The host supplies actual configured Twig policy evidence; native protocol facts come from discovery.
public sealed record GitHubRuleProjection(Connection Connection, string ActorId, string NativeFingerprint,
    GitHubValidationScope Scope, string PolicyIdentity, string PolicyRevision,
    ImmutableDictionary<string, ImmutableArray<FieldRule>> Types);

public sealed record GitHubWriteIntent(Proposal Proposal, int Operation, Guid Attempt, Uri Target,
    string Method, string MediaType, string Payload);

public abstract class GitHubWriteAdmission : IDisposable
{
    public abstract Task ObserveAsync(int statusCode, string body, CancellationToken ct);
    public abstract void Dispose();
}
