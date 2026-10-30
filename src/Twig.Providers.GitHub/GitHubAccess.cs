using System.Collections.Immutable;
using System.Net.Http.Headers;
using Twig.Core;

namespace Twig.Providers.GitHub;

public enum GitHubCredentialSource { Gh, GitHubApp }

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
    public abstract Task<Result<GitHubGovernedRules>> ReadGovernedRulesAsync(ProcessSchema nativeProjection, CancellationToken ct);
    public abstract Task<Result<GitHubWriteAdmission>> AdmitAsync(GitHubWriteIntent intent, CancellationToken ct);
    public abstract void Dispose();
}

// An explicit governed host authority, not a claim that GitHub exposes process rules.
// Failure/unsupported evidence becomes a schema gap; an empty dictionary is never complete.
public sealed record GitHubGovernedRules(Connection Connection, string ActorId, string NativeFingerprint,
    string PolicyIdentity, string PolicyRevision,
    ImmutableDictionary<string, ImmutableArray<FieldRule>> Types);

public sealed record GitHubWriteIntent(Proposal Proposal, int Operation, Guid Attempt, Uri Target,
    string Method, string MediaType, string Payload);

public abstract class GitHubWriteAdmission : IDisposable
{
    public abstract Task ObserveAsync(int statusCode, string body, CancellationToken ct);
    public abstract void Dispose();
}
