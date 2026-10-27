namespace Twig.Infrastructure.GitHub;

/// <summary>Explicit app-user enrollment/rotation, never repository admission or write authority.</summary>
internal sealed class GitHubAppAuthentication(
    GitHubAppCredentialStore store, Func<GitHubDeviceFlowClient> createClient, TimeProvider clock) : IGitHubTokenSource
{
    private string? _attestedToken;
    private string? _attestedActor;

    internal static GitHubAppAuthentication CreateDefault(string clientId, string identity) => new(
        new(GitHubAppCredentialStore.DefaultDirectory, clientId, identity),
        () => new GitHubDeviceFlowClient(), TimeProvider.System);

    internal async Task<GitHubAppAuthStatus> LoginAsync(
        Action<GitHubDeviceInstructions> report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var lease = store.Acquire();
        var existing = store.Read();
        using var client = createClient();
        var grant = await client.RunAppAsync(store.ClientId, report, ct).ConfigureAwait(false);
        var actor = await client.ObserveAppActorAsync(grant.AccessToken, ct).ConfigureAwait(false);
        if (existing is not null && actor.NodeId != existing.ActorId)
            throw new GitHubDeviceFlowException("authenticated actor differs from this identity; use another alias");
        ct.ThrowIfCancellationRequested();
        var entry = Entry(grant, actor.NodeId!, actor.Login!);
        store.Write(entry);
        Attest(entry);
        return Status(entry);
    }

    internal GitHubAppAuthStatus Inspect() => Status(store.Read());

    internal void Logout()
    {
        using var lease = store.Acquire();
        store.Delete();
        _attestedToken = null;
        _attestedActor = null;
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var lease = store.Acquire();
        var entry = store.Read() ?? throw new GitHubDeviceFlowException("selected identity is not signed in; no alternate source was used");
        if (entry.RefreshPending || entry.RefreshExpiresAt <= clock.GetUtcNow())
            throw new GitHubDeviceFlowException("selected identity requires sign-in; rotation will not be replayed");
        if (entry.AccessExpiresAt > clock.GetUtcNow().AddMinutes(1))
        {
            if (_attestedToken != entry.AccessToken || _attestedActor != entry.ActorId)
            {
                using var observer = createClient();
                var observed = await observer.ObserveAppActorAsync(entry.AccessToken, ct).ConfigureAwait(false);
                if (observed.NodeId != entry.ActorId)
                    throw new GitHubDeviceFlowException("stored credential actor differs; sign in again");
                Attest(entry);
            }
            return entry.AccessToken;
        }

        // Persist uncertainty before a one-use refresh can leave this process.
        store.Write(entry with { RefreshPending = true });
        using var client = createClient();
        var grant = await client.RefreshAppAsync(store.ClientId, entry.RefreshToken, ct).ConfigureAwait(false);
        var actor = await client.ObserveAppActorAsync(grant.AccessToken, ct).ConfigureAwait(false);
        if (actor.NodeId != entry.ActorId)
            throw new GitHubDeviceFlowException("refreshed actor differs; sign in again");
        ct.ThrowIfCancellationRequested();
        var next = Entry(grant, actor.NodeId!, actor.Login!);
        store.Write(next);
        Attest(next);
        return next.AccessToken;
    }

    private void Attest(GitHubAppCredential entry)
    {
        _attestedToken = entry.AccessToken;
        _attestedActor = entry.ActorId;
    }

    private GitHubAppCredential Entry(GitHubDeviceGrant grant, string actor, string login) => new()
    {
        ClientId = store.ClientId, Alias = store.Alias, ActorId = actor, Login = login,
        AccessToken = grant.AccessToken, RefreshToken = grant.RefreshToken!,
        AccessExpiresAt = grant.RequestedAt!.Value.AddSeconds(grant.ExpiresIn!.Value),
        RefreshExpiresAt = grant.RequestedAt!.Value.AddSeconds(grant.RefreshTokenExpiresIn!.Value),
    };

    private GitHubAppAuthStatus Status(GitHubAppCredential? entry) => new(
        "github", "github.com", store.ClientId, store.Alias,
        entry?.ActorId, entry?.Login, entry?.AccessExpiresAt, entry?.RefreshExpiresAt,
        entry is null ? "signed-out"
            : entry.RefreshPending || entry.RefreshExpiresAt <= clock.GetUtcNow() ? "needs-login"
            : entry.AccessExpiresAt <= clock.GetUtcNow().AddMinutes(1) ? "refresh-required" : "signed-in");
}

internal sealed record GitHubAppAuthStatus(
    string Provider, string Host, string ClientId, string Identity,
    string? ActorId, string? Login, DateTimeOffset? AccessExpiresAt, DateTimeOffset? RefreshExpiresAt,
    string State);
