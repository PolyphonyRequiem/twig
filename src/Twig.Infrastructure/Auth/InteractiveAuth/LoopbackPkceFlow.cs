using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Twig.Infrastructure.Auth.InteractiveAuth;

/// <summary>
/// OAuth 2.0 authorization-code-with-PKCE flow over a transient loopback HTTP listener.
/// The flow:
/// <list type="number">
///   <item>Generate PKCE verifier+challenge and a CSRF state token.</item>
///   <item>Bind <see cref="HttpListener"/> to <c>http://127.0.0.1:&lt;random-port&gt;/</c>.</item>
///   <item>Build the authorize URL and launch the user's browser at it (or print it).</item>
///   <item>Wait for AAD to redirect the browser back to the loopback URL with a code.</item>
///   <item>Validate state, return a friendly HTML page, then POST the code+verifier to
///         the token endpoint and persist the resulting refresh token.</item>
/// </list>
/// AAD allows native/public clients to use <c>http://localhost</c> with any port without
/// pre-registration, but we bind to <c>127.0.0.1</c> for IPv6/hosts-file robustness and
/// rewrite to <c>localhost</c> only in the redirect_uri to satisfy AAD.
/// </summary>
internal sealed class LoopbackPkceFlow
{
    private static readonly TimeSpan DefaultListenTimeout = TimeSpan.FromMinutes(3);

    private readonly AuthCodeExchanger _exchanger;
    private readonly Func<int> _portPicker;
    private readonly Func<string, bool> _browserOpener;
    private readonly TimeSpan _listenTimeout;

    public LoopbackPkceFlow() : this(new AuthCodeExchanger(), PickFreePort, BrowserLauncher.TryOpen, null) { }

    internal LoopbackPkceFlow(
        AuthCodeExchanger exchanger,
        Func<int> portPicker,
        Func<string, bool> browserOpener,
        TimeSpan? listenTimeout)
    {
        _exchanger = exchanger;
        _portPicker = portPicker;
        _browserOpener = browserOpener;
        _listenTimeout = listenTimeout ?? DefaultListenTimeout;
    }

    /// <summary>
    /// Runs the full PKCE flow. Caller-supplied <paramref name="urlReporter"/> is invoked
    /// with the authorize URL once it's built — useful when <c>--no-browser</c> is set, or
    /// when the browser launch fails.
    /// </summary>
    public async Task<InteractiveAuthResult> RunAsync(
        string clientId,
        string tenant,
        bool launchBrowser,
        Action<string>? urlReporter = null,
        CancellationToken ct = default)
    {
        var pkce = PkceCodes.Generate();
        var state = AuthorizeRequestBuilder.GenerateState();

        int port;
        HttpListener listener;
        try
        {
            port = _portPicker();
            listener = new HttpListener();
            listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
            // On Linux/macOS, "localhost" typically resolves to ::1 first. AAD's redirect
            // sends the browser to the literal "localhost:<port>", so we must accept on
            // both loopback families. IPv6 may be unavailable on some hosts — best-effort.
            try
            {
                listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://[::1]:{port}/"));
            }
            catch
            {
                // IPv6 loopback not available; IPv4-only is fine on those hosts.
            }
            listener.Start();
        }
        catch (Exception ex)
        {
            return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.LoopbackUnavailable,
                $"Could not bind a loopback listener: {ex.Message}");
        }

        try
        {
            // AAD requires the redirect_uri to use the literal "localhost" for native
            // clients (or be explicitly registered). The listener accepts both since
            // 127.0.0.1 and localhost resolve to the same loopback interface.
            var redirectUri = string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}/");
            var authorizeUrl = AuthorizeRequestBuilder.BuildAuthorizeUrl(
                clientId, redirectUri, pkce.Challenge, state, tenant);

            urlReporter?.Invoke(authorizeUrl);

            var browserOpened = false;
            if (launchBrowser)
            {
                browserOpened = _browserOpener(authorizeUrl);
            }

            // Even if the browser didn't open, we still wait — the user may copy the URL
            // and complete the flow manually within the timeout.
            _ = browserOpened;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_listenTimeout);

            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.Timeout,
                    "Timed out waiting for the browser to complete the sign-in. Re-run 'twig login' to try again.");
            }
            catch (OperationCanceledException)
            {
                return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.Cancelled,
                    "Sign-in cancelled.");
            }

            var query = context.Request.Url?.Query ?? string.Empty;
            var parsed = ParseQuery(query);

            if (parsed.TryGetValue("error", out var error))
            {
                var description = parsed.TryGetValue("error_description", out var d) ? d : "(no description)";
                await WriteResponseAsync(context, BuildErrorPage(error, description), ct);
                return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.AuthorizationServerError,
                    $"Authorization server returned '{error}': {description}");
            }

            if (!parsed.TryGetValue("state", out var returnedState) ||
                !AuthorizeRequestBuilder.ValidateState(state, returnedState))
            {
                await WriteResponseAsync(context, BuildErrorPage("state_mismatch",
                    "The 'state' parameter did not match — possible CSRF or tampering."), ct);
                return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.StateMismatch,
                    "OAuth state parameter did not round-trip — aborting to prevent CSRF.");
            }

            if (!parsed.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                await WriteResponseAsync(context, BuildErrorPage("missing_code",
                    "Authorization server redirect did not include an authorization code."), ct);
                return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.AuthorizationServerError,
                    "Authorization server redirect did not include an authorization code.");
            }

            await WriteResponseAsync(context, BuildSuccessPage(), ct);

            var exchange = await _exchanger.ExchangeCodeAsync(
                code, pkce.Verifier, redirectUri, clientId, tenant, ct: ct);

            if (!exchange.IsSuccess || exchange.Tokens is null)
            {
                return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.TokenExchangeFailed,
                    $"Token exchange failed ({exchange.ErrorCode}): {exchange.ErrorDescription}");
            }

            if (string.IsNullOrWhiteSpace(exchange.Tokens.RefreshToken))
            {
                return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.TokenExchangeFailed,
                    "Token exchange succeeded but no refresh_token was returned (scope likely missing offline_access).");
            }

            var claims = IdTokenDecoder.Decode(exchange.Tokens.IdToken);
            var entry = new TwigRefreshTokenStoreEntry
            {
                RefreshToken = exchange.Tokens.RefreshToken,
                ClientId = clientId,
                TenantId = claims.TenantId ?? tenant,
                AuthorityHost = AuthorizeRequestBuilder.DefaultAuthorityHost,
                UserPrincipalName = claims.UserPrincipalName,
                ObjectId = claims.ObjectId,
                BootstrappedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Source = "login-pkce",
            };

            return InteractiveAuthResult.Success(entry);
        }
        finally
        {
            try { listener.Stop(); } catch { /* best-effort */ }
            try { listener.Close(); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Asks the OS for a free TCP port by binding to port 0, reading the assigned port,
    /// then releasing immediately. Standard "ephemeral port" pattern. Brief race window
    /// between release and our HttpListener bind, but acceptable for an interactive flow.
    /// </summary>
    internal static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return result;
        var trimmed = query.TrimStart('?');
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                result[WebUtility.UrlDecode(pair)] = string.Empty;
            }
            else
            {
                var key = WebUtility.UrlDecode(pair[..eq]);
                var value = WebUtility.UrlDecode(pair[(eq + 1)..]);
                result[key] = value;
            }
        }
        return result;
    }

    private static async Task WriteResponseAsync(HttpListenerContext context, string html, CancellationToken ct)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.StatusCode = 200;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.OutputStream.Close();
        }
        catch
        {
            // Browser may have already disconnected — ignore.
        }
    }

    private static string BuildSuccessPage() =>
        """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>twig — signed in</title>
        <style>
          body { font-family: -apple-system, Segoe UI, Roboto, sans-serif; background: #0d1117; color: #c9d1d9; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }
          .card { background: #161b22; border: 1px solid #30363d; border-radius: 8px; padding: 32px 40px; max-width: 480px; text-align: center; }
          h1 { color: #2ea043; margin: 0 0 12px; font-size: 22px; }
          p { margin: 8px 0; color: #8b949e; }
          code { background: #0d1117; padding: 2px 6px; border-radius: 4px; color: #c9d1d9; }
        </style></head>
        <body><div class="card">
          <h1>✓ twig is signed in</h1>
          <p>You can close this tab and return to your terminal.</p>
          <p>Run <code>twig auth status</code> to verify.</p>
        </div></body></html>
        """;

    private static string BuildErrorPage(string code, string description) =>
        $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>twig — sign-in failed</title>
        <style>
          body { font-family: -apple-system, Segoe UI, Roboto, sans-serif; background: #0d1117; color: #c9d1d9; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }
          .card { background: #161b22; border: 1px solid #30363d; border-radius: 8px; padding: 32px 40px; max-width: 560px; }
          h1 { color: #f85149; margin: 0 0 12px; font-size: 22px; }
          p { margin: 8px 0; color: #c9d1d9; }
          code { background: #0d1117; padding: 2px 6px; border-radius: 4px; color: #ffa657; }
        </style></head>
        <body><div class="card">
          <h1>✗ sign-in failed</h1>
          <p><strong>{{WebUtility.HtmlEncode(code)}}</strong></p>
          <p>{{WebUtility.HtmlEncode(description)}}</p>
          <p>Return to your terminal — twig will print details and exit.</p>
        </div></body></html>
        """;
}
