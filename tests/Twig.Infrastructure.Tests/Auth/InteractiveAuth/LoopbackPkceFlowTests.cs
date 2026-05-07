using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using Shouldly;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth.InteractiveAuth;

/// <summary>
/// Tests for <see cref="LoopbackPkceFlow"/>. The flow opens a real <see cref="HttpListener"/>
/// on a loopback port, so we drive it by spinning up the flow on a background task and
/// firing a real HTTP GET to the loopback URL with our scripted query string. The token
/// endpoint is mocked via the injected <see cref="AuthCodeExchanger"/>.
/// </summary>
public class LoopbackPkceFlowTests
{
    private const string TokenSuccess = """
        {"access_token":"at","refresh_token":"rt","id_token":"","expires_in":3600}
        """;

    [Fact]
    public async Task RunAsync_HappyPath_RoundTripsThroughRealLoopbackListener()
    {
        var port = LoopbackPkceFlow.PickFreePort();
        var (handler, capturedUrl) = NewExchangerStub(TokenSuccess);
        var flow = new LoopbackPkceFlow(
            new AuthCodeExchanger(handler),
            portPicker: () => port,
            browserOpener: _ => true,
            listenTimeout: TimeSpan.FromSeconds(10));

        string? authorizeUrl = null;
        var flowTask = flow.RunAsync(
            clientId: "test-client",
            tenant: "organizations",
            launchBrowser: true,
            urlReporter: u => authorizeUrl = u);

        // Wait briefly for the listener to be bound, then parse the state from the
        // authorize URL the flow built and POST it back as the redirect.
        var state = await ExtractStateAsync(authorizeUrl, () => Volatile.Read(ref authorizeUrl));

        await SendRedirectAsync(port, $"?code=the-code&state={Uri.EscapeDataString(state)}");

        var result = await flowTask;

        result.Succeeded.ShouldBeTrue();
        result.Entry!.RefreshToken.ShouldBe("rt");
        result.Entry.Source.ShouldBe("login-pkce");
        result.Entry.ClientId.ShouldBe("test-client");
        capturedUrl().ShouldStartWith("https://login.microsoftonline.com/organizations/oauth2/v2.0/token");
    }

    [Fact]
    public async Task RunAsync_StateMismatch_ReturnsStateMismatchError()
    {
        var port = LoopbackPkceFlow.PickFreePort();
        var (handler, _) = NewExchangerStub(TokenSuccess);
        var flow = new LoopbackPkceFlow(
            new AuthCodeExchanger(handler),
            portPicker: () => port,
            browserOpener: _ => true,
            listenTimeout: TimeSpan.FromSeconds(10));

        string? authorizeUrl = null;
        var flowTask = flow.RunAsync("c", "organizations", true, u => authorizeUrl = u);

        await ExtractStateAsync(authorizeUrl, () => Volatile.Read(ref authorizeUrl));

        await SendRedirectAsync(port, "?code=the-code&state=tampered");

        var result = await flowTask;

        result.Succeeded.ShouldBeFalse();
        result.ErrorKind.ShouldBe(InteractiveAuthErrorKind.StateMismatch);
    }

    [Fact]
    public async Task RunAsync_AuthorizationServerError_ReturnsAuthServerError()
    {
        var port = LoopbackPkceFlow.PickFreePort();
        var (handler, _) = NewExchangerStub(TokenSuccess);
        var flow = new LoopbackPkceFlow(
            new AuthCodeExchanger(handler),
            portPicker: () => port,
            browserOpener: _ => true,
            listenTimeout: TimeSpan.FromSeconds(10));

        string? authorizeUrl = null;
        var flowTask = flow.RunAsync("c", "organizations", true, u => authorizeUrl = u);

        // Wait for listener to be ready.
        await ExtractStateAsync(authorizeUrl, () => Volatile.Read(ref authorizeUrl));

        await SendRedirectAsync(port, "?error=access_denied&error_description=User+declined");

        var result = await flowTask;

        result.Succeeded.ShouldBeFalse();
        result.ErrorKind.ShouldBe(InteractiveAuthErrorKind.AuthorizationServerError);
    }

    [Fact]
    public async Task RunAsync_BrowserOpenerInvokedWithBuiltAuthorizeUrl()
    {
        var port = LoopbackPkceFlow.PickFreePort();
        var (handler, _) = NewExchangerStub(TokenSuccess);
        string? openedUrl = null;
        var flow = new LoopbackPkceFlow(
            new AuthCodeExchanger(handler),
            portPicker: () => port,
            browserOpener: u => { openedUrl = u; return true; },
            listenTimeout: TimeSpan.FromSeconds(10));

        string? authorizeUrl = null;
        var flowTask = flow.RunAsync("the-client", "organizations", true, u => authorizeUrl = u);

        var state = await ExtractStateAsync(authorizeUrl, () => Volatile.Read(ref authorizeUrl));
        await SendRedirectAsync(port, $"?code=c&state={Uri.EscapeDataString(state)}");
        await flowTask;

        openedUrl.ShouldNotBeNull();
        openedUrl!.ShouldStartWith("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize");
        openedUrl.ShouldContain("client_id=the-client");
        openedUrl.ShouldContain("code_challenge_method=S256");
    }

    [Fact]
    public async Task RunAsync_LaunchBrowserFalse_DoesNotInvokeBrowserOpener()
    {
        var port = LoopbackPkceFlow.PickFreePort();
        var (handler, _) = NewExchangerStub(TokenSuccess);
        var browserOpenerCalled = false;
        var flow = new LoopbackPkceFlow(
            new AuthCodeExchanger(handler),
            portPicker: () => port,
            browserOpener: _ => { browserOpenerCalled = true; return true; },
            listenTimeout: TimeSpan.FromSeconds(10));

        string? authorizeUrl = null;
        var flowTask = flow.RunAsync("c", "organizations", launchBrowser: false, urlReporter: u => authorizeUrl = u);

        var state = await ExtractStateAsync(authorizeUrl, () => Volatile.Read(ref authorizeUrl));
        await SendRedirectAsync(port, $"?code=c&state={Uri.EscapeDataString(state)}");
        await flowTask;

        browserOpenerCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_TimeoutExpiresWithNoRedirect_ReturnsTimeoutError()
    {
        var port = LoopbackPkceFlow.PickFreePort();
        var (handler, _) = NewExchangerStub(TokenSuccess);
        var flow = new LoopbackPkceFlow(
            new AuthCodeExchanger(handler),
            portPicker: () => port,
            browserOpener: _ => true,
            // Tight timeout so the test runs quickly; production default is 3 minutes.
            listenTimeout: TimeSpan.FromMilliseconds(500));

        var result = await flow.RunAsync("c", "organizations", true);

        result.Succeeded.ShouldBeFalse();
        result.ErrorKind.ShouldBe(InteractiveAuthErrorKind.Timeout);
    }

    [Fact]
    public async Task RunAsync_TokenExchangeFailure_ReturnsTokenExchangeFailedError()
    {
        var port = LoopbackPkceFlow.PickFreePort();
        var errorBody = """{"error":"invalid_grant","error_description":"AADSTS70008"}""";
        var (handler, _) = NewExchangerStub(errorBody, HttpStatusCode.BadRequest);
        var flow = new LoopbackPkceFlow(
            new AuthCodeExchanger(handler),
            portPicker: () => port,
            browserOpener: _ => true,
            listenTimeout: TimeSpan.FromSeconds(10));

        string? authorizeUrl = null;
        var flowTask = flow.RunAsync("c", "organizations", true, u => authorizeUrl = u);

        var state = await ExtractStateAsync(authorizeUrl, () => Volatile.Read(ref authorizeUrl));
        await SendRedirectAsync(port, $"?code=c&state={Uri.EscapeDataString(state)}");

        var result = await flowTask;
        result.Succeeded.ShouldBeFalse();
        result.ErrorKind.ShouldBe(InteractiveAuthErrorKind.TokenExchangeFailed);
    }

    [Fact]
    public void PickFreePort_ReturnsBindablePort()
    {
        var port = LoopbackPkceFlow.PickFreePort();
        port.ShouldBeGreaterThan(0);
        // Bindable confirmation: open a listener on the picked port and verify it works.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try { ((IPEndPoint)listener.LocalEndpoint).Port.ShouldBe(port); }
        finally { listener.Stop(); }
    }

    /// <summary>
    /// Polls the captured authorize URL (set by the running flow's url reporter callback)
    /// until non-null, then extracts the state parameter. Bounded wait — fails fast if the
    /// flow doesn't report within 5 seconds.
    /// </summary>
    private static async Task<string> ExtractStateAsync(string? snapshot, Func<string?> reader)
    {
        var url = snapshot ?? reader();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (url is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
            url = reader();
        }
        url.ShouldNotBeNull("flow never reported the authorize URL");

        var query = new Uri(url!).Query.TrimStart('?');
        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (pair[..eq] == "state") return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        throw new InvalidOperationException($"state not found in URL: {url}");
    }

    /// <summary>
    /// Sends a real HTTP GET to the loopback port to simulate the browser following the
    /// AAD redirect back to <c>http://localhost:{port}/{query}</c>.
    /// </summary>
    private static async Task SendRedirectAsync(int port, string queryString)
    {
        // The HttpListener takes a moment to start accepting; retry briefly on connect failure.
        var url = string.Create(CultureInfo.InvariantCulture, $"http://localhost:{port}/{queryString}");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Exception? lastEx = null;
        for (var attempt = 0; attempt < 25; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url);
                _ = await response.Content.ReadAsStringAsync();
                return;
            }
            catch (Exception ex) { lastEx = ex; await Task.Delay(40); }
        }
        throw new InvalidOperationException($"Could not reach loopback listener on port {port}", lastEx);
    }

    private static (HttpMessageHandler Handler, Func<string> CapturedUrl) NewExchangerStub(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(status, body);
        return (handler, () => handler.LastUrl ?? string.Empty);
    }

    private sealed class CapturingHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
        }
    }
}
