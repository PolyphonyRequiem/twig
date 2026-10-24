namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Test seam: when registered, supplies the innermost GitHub transport beneath the read-only
/// auth handler. Production never registers it, so the default no-redirect socket handler is used.
/// </summary>
internal sealed class GitHubTransportOverride(Func<HttpMessageHandler> createHandler)
{
    public HttpMessageHandler CreateHandler() => createHandler();
}
