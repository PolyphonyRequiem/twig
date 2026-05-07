namespace Twig.Infrastructure.Auth.InteractiveAuth;

/// <summary>
/// Outcome of an interactive auth flow. Either a populated <see cref="TwigRefreshTokenStoreEntry"/>
/// ready to be persisted, or a structured error explaining what went wrong (so the command
/// surface can render specific guidance — "policy blocks device code", "browser launch
/// failed, copy this URL", etc.).
/// </summary>
internal sealed record InteractiveAuthResult
{
    public TwigRefreshTokenStoreEntry? Entry { get; init; }
    public InteractiveAuthErrorKind ErrorKind { get; init; } = InteractiveAuthErrorKind.None;
    public string? ErrorMessage { get; init; }

    public bool Succeeded => Entry is not null;

    public static InteractiveAuthResult Success(TwigRefreshTokenStoreEntry entry)
        => new() { Entry = entry };

    public static InteractiveAuthResult Failure(InteractiveAuthErrorKind kind, string message)
        => new() { ErrorKind = kind, ErrorMessage = message };
}

internal enum InteractiveAuthErrorKind
{
    None = 0,

    /// <summary>The local HttpListener could not bind any port.</summary>
    LoopbackUnavailable,

    /// <summary>The OS browser could not be launched.</summary>
    BrowserLaunchFailed,

    /// <summary>The user closed the browser without completing the flow, or our timeout fired.</summary>
    Timeout,

    /// <summary>The state parameter did not round-trip — possible CSRF or tampered redirect.</summary>
    StateMismatch,

    /// <summary>AAD returned an error in the redirect (consent denied, policy block, etc.).</summary>
    AuthorizationServerError,

    /// <summary>The token endpoint POST failed (network, server error, malformed response).</summary>
    TokenExchangeFailed,

    /// <summary>The flow is blocked by tenant policy (e.g. device code disabled).</summary>
    PolicyBlocked,

    /// <summary>Caller cancelled before the flow completed.</summary>
    Cancelled,
}
