using Twig.Infrastructure.Auth;

namespace Twig.Infrastructure.Tests.Auth;

/// <summary>
/// Test double for <see cref="ITokenRefresher"/>. Configurable per-call response queue plus
/// invocation log so tests can assert ordering, payloads, and "called once" semantics.
/// </summary>
internal sealed class FakeTokenRefresher : ITokenRefresher
{
    private readonly Queue<(string? Token, bool InvalidGrant)> _responses = new();

    public List<(string RefreshToken, string ClientId, string TenantId, string AuthorityHost)> Calls { get; } = new();

    public FakeTokenRefresher EnqueueSuccess(string token)
    {
        _responses.Enqueue((token, false));
        return this;
    }

    public FakeTokenRefresher EnqueueInvalidGrant()
    {
        _responses.Enqueue((null, true));
        return this;
    }

    public FakeTokenRefresher EnqueueFailure()
    {
        _responses.Enqueue((null, false));
        return this;
    }

    public Task<(string? AccessToken, bool IsInvalidGrant)> TryRefreshAsync(
        string refreshToken,
        string clientId,
        string tenantId,
        string authorityHost,
        CancellationToken ct = default)
    {
        Calls.Add((refreshToken, clientId, tenantId, authorityHost));
        var response = _responses.Count > 0 ? _responses.Dequeue() : (null, false);
        return Task.FromResult<(string?, bool)>(response);
    }
}
