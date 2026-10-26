using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.GitHub;

/// <summary>Captures one credential for a point-in-time actor observation and read-only session, not write authority.</summary>
internal sealed class GitHubCapturedReadSessionFactory(
    IGitHubTokenSource selectedCredential,
    GitHubBoundTargetResolver targets,
    GitHubWorkspaceScope scope,
    string? selectedAccount,
    GitHubTransportOverride? transport = null)
{
    public async Task<GitHubCapturedReadSession> CaptureAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = await targets.ResolveAsync(ct).ConfigureAwait(false);
        var token = await selectedCredential.GetTokenAsync(ct).ConfigureAwait(false);
        var http = GitHubReadOnlyAuthHandler.CreateClient(
            new CapturedCredential(token), scope.GraphQlEndpoint, transport?.CreateHandler());
        var actor = new GitHubAuthenticatedActorReader(http, scope);
        try
        {
            var observation = await actor.ObserveAsync(ct).ConfigureAwait(false);
            await targets.ValidateCapturedSessionAsync(target, ct).ConfigureAwait(false);
            var provider = new GitHubWorkItemProvider(http, scope.GraphQlEndpoint,
                resolveTarget: async cancellation =>
                {
                    await targets.ValidateCapturedSessionAsync(target, cancellation).ConfigureAwait(false);
                    return target;
                },
                validateTarget: (_, cancellation) => targets.ValidateCapturedSessionAsync(target, cancellation));
            return new(actor, provider, target, observation, scope.GraphQlEndpoint, selectedAccount);
        }
        catch
        {
            actor.Dispose();
            throw;
        }
    }

    private sealed class CapturedCredential(string token) : IGitHubTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(token);
        }
    }
}

/// <summary>
/// In-memory reads use the credential that produced <see cref="Actor"/>. Selection checks
/// bracket reads; they are not atomic provider concurrency, generation admission or future authorization.
/// </summary>
internal sealed class GitHubCapturedReadSession(
    GitHubAuthenticatedActorReader actorReader,
    GitHubWorkItemProvider provider,
    GitHubBoundReadContext target,
    GitHubAuthenticatedActorObservation actor,
    Uri graphQlEndpoint,
    string? selectedAccount) : IDisposable
{
    public GitHubBoundReadContext Target { get; } = target;
    public GitHubAuthenticatedActorObservation Actor { get; } = actor;
    public Uri GraphQlEndpoint { get; } = graphQlEndpoint;
    /// <summary>The selected credential's account locator, not an authenticated subject or authority.</summary>
    public string? SelectedAccount { get; } = selectedAccount;

    public Task<ProviderWorkItemSnapshot> FetchSnapshotAsync(
        ExternalWorkItemIdentity identity, CancellationToken ct = default)
        => provider.FetchSnapshotAsync(identity, ct);

    public void Dispose() => actorReader.Dispose();
}
