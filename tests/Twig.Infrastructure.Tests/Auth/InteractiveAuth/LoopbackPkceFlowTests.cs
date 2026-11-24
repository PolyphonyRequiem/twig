using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using Shouldly;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Xunit;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using NSubstitute;
using System.Net.Sockets;

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
    public async Task NativeSetupPkceCancelReleasesRealListenersWithoutResolvingBindings()
    {
        var port = LoopbackPkceFlow.PickFreePort();
        var (handler, _) = NewExchangerStub(TokenSuccess);
        var opened = false;
        var bindingCalls = 0;
        var flow = new LoopbackPkceFlow(new AuthCodeExchanger(handler), () => port,
            _ => { opened = true; return true; }, TimeSpan.FromSeconds(10));
        var setup = new NativeConnectionSetup(_ => { bindingCalls++; throw new InvalidOperationException("must not resolve"); },
            () => flow);
        using var cancellation = new CancellationTokenSource();
        string? url = null;
        var pending = setup.EnrollAadAsync(new("explicit"), value => url = value.AbsoluteUri, cancellation.Token);
        await ExtractStateAsync(url, () => Volatile.Read(ref url));
        cancellation.Cancel();
        var result = await pending;

        result.IsSuccess.ShouldBeFalse();
        var report = NativeConnectionSetup.ReadFailure(result.Error).Value;
        report.Cancelled.ShouldBeTrue();
        report.Effects.ShouldBe([new NativeSetupEffect(NativeSetupEffectKind.InteractiveSignIn, NativeSetupEffectState.Possible)]);
        bindingCalls.ShouldBe(0);
        opened.ShouldBeFalse();
        AssertPortReleased(port);
    }

    [Fact]
    public async Task NativeSetupCallbackFailureIsARealFailureAndTearsDownListeners()
    {
        var port = LoopbackPkceFlow.PickFreePort();
        var (handler, _) = NewExchangerStub(TokenSuccess);
        var flow = new LoopbackPkceFlow(new AuthCodeExchanger(handler), () => port, _ => false, TimeSpan.FromSeconds(10));
        var setup = new NativeConnectionSetup(_ => throw new InvalidOperationException("must not resolve"), () => flow);
        var result = await setup.EnrollAadAsync(new("explicit"),
            _ => throw new InvalidOperationException("callback-private-token https://private.test/"));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.Unavailable);
        result.Error.Message.ShouldContain("callback failed");
        result.Error.Message.ShouldNotContain("private");
        var report = NativeConnectionSetup.ReadFailure(result.Error).Value;
        report.Cancelled.ShouldBeFalse();
        report.Phase.ShouldBe(NativeSetupPhase.Enrollment);
        report.Effects.ShouldBe([new NativeSetupEffect(NativeSetupEffectKind.InteractiveSignIn, NativeSetupEffectState.Possible)]);
        AssertPortReleased(port);
    }

    [Fact]
    public async Task NativeSetupPkceEnrollmentUsesActualNativeClaimAndSameAliasGuards()
    {
        var home = Path.Combine(Path.GetTempPath(), "twig-native-pkce-" + Guid.NewGuid().ToString("N"));
        var refresher = Substitute.For<ITokenRefresher>();
        var principal = "22222222-2222-2222-2222-222222222222";
        refresher.TryRefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
                (AadAccountClaimTests.Token("{}", principal), "synthetic-rotated", false));
        try
        {
            var first = await Enroll();
            first.IsSuccess.ShouldBeTrue(first.IsSuccess ? "" : first.Error.Message);
            first.Value.Alias.ShouldBe("explicit");
            using var registry = new ConnectionBindingService(home, refresher);
            var binding = await registry.CreateBindingAsync("fixture", "Work", "explicit", true);
            var renewed = await Enroll();
            renewed.IsSuccess.ShouldBeTrue();
            renewed.Value.IdentityId.ShouldBe(first.Value.IdentityId);
            (await registry.CreateBindingAsync("fixture", "Work", "explicit", true)).ShouldBe(binding);
            principal = "33333333-3333-3333-3333-333333333333";
            var refused = await Enroll();
            refused.IsSuccess.ShouldBeFalse();
            refused.Error.Code.ShouldBe(ErrorCode.Conflict);
            NativeConnectionSetup.ReadFailure(refused.Error).Value.Effects
                .ShouldNotContain(effect => effect.Kind == NativeSetupEffectKind.CredentialWrite);
            (await registry.ListIdentitiesAsync()).Single().ObjectId.ShouldBe(first.Value.ObjectId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }

        async Task<Result<NativeAadEnrollment>> Enroll()
        {
            var port = LoopbackPkceFlow.PickFreePort();
            var (handler, _) = NewExchangerStub(TokenSuccess);
            var flow = new LoopbackPkceFlow(new AuthCodeExchanger(handler), () => port,
                _ => throw new InvalidOperationException("public enrollment must not launch a browser"), TimeSpan.FromSeconds(10));
            var setup = new NativeConnectionSetup(effects => new ConnectionBindingService(home, refresher, setupEffects: effects),
                () => flow, ownsBindings: true);
            string? url = null;
            var pending = setup.EnrollAadAsync(new("explicit"), value => url = value.AbsoluteUri);
            var state = await ExtractStateAsync(url, () => Volatile.Read(ref url));
            url.ShouldNotBeNull().ShouldContain("code_challenge_method=S256");
            await SendRedirectAsync(port, "?code=synthetic&state=" + Uri.EscapeDataString(state));
            var result = await pending;
            AssertPortReleased(port);
            return result;
        }
    }

    private static void AssertPortReleased(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try { ((IPEndPoint)listener.LocalEndpoint).Port.ShouldBe(port); }
        finally { listener.Stop(); }
        if (!Socket.OSSupportsIPv6) return;
        var v6 = new TcpListener(IPAddress.IPv6Loopback, port);
        v6.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
        v6.Start();
        try { ((IPEndPoint)v6.LocalEndpoint).Port.ShouldBe(port); }
        finally { v6.Stop(); }
    }

    [Theory]
    [InlineData("microsoft.com", true)]
    [InlineData("MICROSOFT.COM", true)]
    [InlineData("11111111-1111-1111-1111-111111111111", true)]
    [InlineData("44444444-4444-4444-4444-444444444444", false)]
    [InlineData("microsoft.com", false, "44444444-4444-4444-4444-444444444444")]
    [InlineData("microsoft.com", false, null, "33333333-3333-3333-3333-333333333333")]
    public async Task NativeSetupWithoutIdTokenResolvesTenantDomainButRejectsDifferentTenantId(
        string tenant, bool succeeds, string? refreshedTenant = null, string? refreshedObject = null)
    {
        const string tokenTenant = "11111111-1111-1111-1111-111111111111";
        var home = Path.Combine(Path.GetTempPath(), "twig-domain-pkce-" + Guid.NewGuid().ToString("N"));
        var accessToken = AadAccountClaimTests.Token("{}");
        var refresher = Substitute.For<ITokenRefresher>();
        refresher.TryRefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AadAccountClaimTests.Token("{}",
                    principal: refreshedObject ?? "22222222-2222-2222-2222-222222222222",
                    tenant: refreshedTenant ?? tokenTenant),
                (string?)null, false));
        var port = LoopbackPkceFlow.PickFreePort();
        var (handler, capturedUrl) = NewExchangerStub(
            TokenSuccess.Replace("\"at\"", "\"" + accessToken + "\"", StringComparison.Ordinal));
        var flow = new LoopbackPkceFlow(new AuthCodeExchanger(handler), () => port,
            _ => false, TimeSpan.FromSeconds(10));
        var setup = new NativeConnectionSetup(effects => new ConnectionBindingService(home, refresher, setupEffects: effects),
            () => flow, ownsBindings: true);
        try
        {
            string? url = null;
            var pending = setup.EnrollAadAsync(new("explicit", tenant), value => url = value.AbsoluteUri);
            var state = await ExtractStateAsync(url, () => Volatile.Read(ref url));
            await SendRedirectAsync(port, "?code=synthetic&state=" + Uri.EscapeDataString(state));
            var result = await pending;

            result.IsSuccess.ShouldBe(succeeds, result.IsSuccess ? null : result.Error.Message);
            capturedUrl().ShouldStartWith("https://login.microsoftonline.com/" + tenant + "/");
            if (succeeds)
            {
                result.Value.TenantId.ShouldBe(tokenTenant);
                await refresher.Received(1).TryRefreshAsync("rt", NativeSetupEnrollment.ClientId, tokenTenant,
                    AuthorizeRequestBuilder.DefaultAuthorityHost, Arg.Any<CancellationToken>());
                using var reopened = new ConnectionBindingService(home, refresher);
                (await reopened.ListIdentitiesAsync()).Single().TenantId.ShouldBe(tokenTenant);
            }
            else
            {
                result.Error.Message.ShouldBe(refreshedObject is null
                    ? "The enrollment credential and access token have different tenant claims."
                    : "The enrollment credential and access token have different oid claims.");
                NativeConnectionSetup.ReadFailure(result.Error).Value.Effects
                    .ShouldNotContain(effect => effect.Kind == NativeSetupEffectKind.CredentialWrite);
                Directory.GetFiles(Path.Combine(home, "credentials")).ShouldBeEmpty();
            }
            AssertPortReleased(port);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("wrong-audience")]
    [InlineData("missing-tenant")]
    [InlineData("invalid-tenant")]
    [InlineData("missing-object")]
    [InlineData("id-token-mismatch")]
    public async Task NativeSetupRejectsUnresolvedOrContradictoryTenantBeforeOpeningBindings(string scenario)
    {
        const string tenantId = "11111111-1111-1111-1111-111111111111";
        var tenant = scenario == "id-token-mismatch" ? tenantId : "microsoft.com";
        var accessToken = scenario switch
        {
            "wrong-audience" => JwtTestFactory.BuildWrongAudience(),
            "missing-tenant" => JwtTestFactory.Build(tenantId: null),
            "invalid-tenant" => JwtTestFactory.Build(tenantId: "private-tenant"),
            "missing-object" => JwtTestFactory.Build(tenantId: tenantId),
            "id-token-mismatch" => AadAccountClaimTests.Token("{}"),
            _ => "private-opaque-token",
        };
        var response = TokenSuccess.Replace("\"at\"", "\"" + accessToken + "\"", StringComparison.Ordinal);
        if (scenario == "id-token-mismatch")
            response = response.Replace("\"id_token\":\"\"", "\"id_token\":\""
                + AadAccountClaimTests.Token("{}", tenant: "44444444-4444-4444-4444-444444444444") + "\"",
                StringComparison.Ordinal);
        var port = LoopbackPkceFlow.PickFreePort();
        var (handler, _) = NewExchangerStub(response);
        var flow = new LoopbackPkceFlow(new AuthCodeExchanger(handler), () => port, _ => false, TimeSpan.FromSeconds(10));
        var bindingCalls = 0;
        var setup = new NativeConnectionSetup(_ => { bindingCalls++; throw new InvalidOperationException("must not resolve"); },
            () => flow);
        string? url = null;
        var pending = setup.EnrollAadAsync(new("explicit", tenant), value => url = value.AbsoluteUri);
        var state = await ExtractStateAsync(url, () => Volatile.Read(ref url));
        await SendRedirectAsync(port, "?code=synthetic&state=" + Uri.EscapeDataString(state));
        var result = await pending;

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.Unavailable);
        result.Error.Message.ShouldContain(scenario == "id-token-mismatch"
            ? "requested tenant ID and sign-in token have different tenant claims"
            : "did not supply an Azure DevOps access token with tenant ID and oid claims");
        result.Error.Message.ShouldNotContain("private");
        bindingCalls.ShouldBe(0);
        NativeConnectionSetup.ReadFailure(result.Error).Value.Effects
            .ShouldNotContain(effect => effect.Kind == NativeSetupEffectKind.CredentialWrite);
        AssertPortReleased(port);
    }

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
    /// AAD redirect. We hit <c>127.0.0.1</c> directly (rather than <c>localhost</c>) to
    /// avoid the OS DNS resolution preferring IPv6 on Linux/macOS while the listener may
    /// have only bound IPv4.
    /// </summary>
    private static async Task SendRedirectAsync(int port, string queryString)
    {
        // The HttpListener takes a moment to start accepting; retry briefly on connect failure.
        var url = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/{queryString}");
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
