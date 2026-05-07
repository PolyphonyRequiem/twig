using System.Net;
using System.Net.Http;
using Shouldly;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth.InteractiveAuth;

/// <summary>
/// Tests for <see cref="DeviceCodeFlow"/> — RFC 8628 device authorization grant.
/// Covers the polling state machine: pending, slow_down, success, expired, denied,
/// policy-blocked.
/// </summary>
public class DeviceCodeFlowTests
{
    private const string DeviceCodeOk = """
        {
          "device_code": "the-device-code",
          "user_code": "ABCD1234",
          "verification_uri": "https://microsoft.com/devicelogin",
          "expires_in": 900,
          "interval": 5,
          "message": "go to the URL and enter the code"
        }
        """;

    private const string TokenSuccess = """
        {"access_token":"at","refresh_token":"rt","id_token":"it","expires_in":3600}
        """;

    [Fact]
    public async Task RunAsync_HappyPath_ReturnsSuccessWithLoginDeviceSource()
    {
        var handler = new ScriptedHandler(new[]
        {
            (HttpStatusCode.OK, DeviceCodeOk),  // /devicecode
            (HttpStatusCode.OK, TokenSuccess),  // first poll succeeds
        });
        var flow = new DeviceCodeFlow(
            new AuthCodeExchanger(handler),
            handler,
            delay: (_, _) => Task.CompletedTask,
            requestTimeout: null);

        DeviceCodeInstructions? reported = null;
        var result = await flow.RunAsync("client-id", "organizations", inst => reported = inst);

        result.Succeeded.ShouldBeTrue();
        result.Entry!.RefreshToken.ShouldBe("rt");
        result.Entry.Source.ShouldBe("login-device");
        reported.ShouldNotBeNull();
        reported!.UserCode.ShouldBe("ABCD1234");
        reported.VerificationUri.ShouldBe("https://microsoft.com/devicelogin");
    }

    [Fact]
    public async Task RunAsync_PollsThroughAuthorizationPendingThenSucceeds()
    {
        var handler = new ScriptedHandler(new[]
        {
            (HttpStatusCode.OK, DeviceCodeOk),
            (HttpStatusCode.BadRequest, """{"error":"authorization_pending","error_description":"waiting"}"""),
            (HttpStatusCode.BadRequest, """{"error":"authorization_pending","error_description":"still waiting"}"""),
            (HttpStatusCode.OK, TokenSuccess),
        });

        var delayCalls = 0;
        var flow = new DeviceCodeFlow(
            new AuthCodeExchanger(handler),
            handler,
            delay: (_, _) => { delayCalls++; return Task.CompletedTask; },
            requestTimeout: null);

        var result = await flow.RunAsync("c", "t", _ => { });

        result.Succeeded.ShouldBeTrue();
        // 3 polls = 3 delay calls.
        delayCalls.ShouldBe(3);
    }

    [Fact]
    public async Task RunAsync_HonoursSlowDown_IncreasesPollInterval()
    {
        var handler = new ScriptedHandler(new[]
        {
            (HttpStatusCode.OK, DeviceCodeOk),
            (HttpStatusCode.BadRequest, """{"error":"slow_down","error_description":"too fast"}"""),
            (HttpStatusCode.OK, TokenSuccess),
        });

        var delays = new List<TimeSpan>();
        var flow = new DeviceCodeFlow(
            new AuthCodeExchanger(handler),
            handler,
            delay: (d, _) => { delays.Add(d); return Task.CompletedTask; },
            requestTimeout: null);

        var result = await flow.RunAsync("c", "t", _ => { });

        result.Succeeded.ShouldBeTrue();
        delays.Count.ShouldBe(2);
        // Initial interval was 5 seconds; after slow_down it should be ≥10 seconds.
        delays[1].ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RunAsync_ExpiredToken_ReturnsTimeoutError()
    {
        var handler = new ScriptedHandler(new[]
        {
            (HttpStatusCode.OK, DeviceCodeOk),
            (HttpStatusCode.BadRequest, """{"error":"expired_token","error_description":"expired"}"""),
        });
        var flow = new DeviceCodeFlow(
            new AuthCodeExchanger(handler),
            handler,
            delay: (_, _) => Task.CompletedTask,
            requestTimeout: null);

        var result = await flow.RunAsync("c", "t", _ => { });

        result.Succeeded.ShouldBeFalse();
        result.ErrorKind.ShouldBe(InteractiveAuthErrorKind.Timeout);
    }

    [Fact]
    public async Task RunAsync_AccessDenied_ReturnsAuthorizationServerError()
    {
        var handler = new ScriptedHandler(new[]
        {
            (HttpStatusCode.OK, DeviceCodeOk),
            (HttpStatusCode.BadRequest, """{"error":"authorization_declined","error_description":"declined"}"""),
        });
        var flow = new DeviceCodeFlow(
            new AuthCodeExchanger(handler),
            handler,
            delay: (_, _) => Task.CompletedTask,
            requestTimeout: null);

        var result = await flow.RunAsync("c", "t", _ => { });

        result.Succeeded.ShouldBeFalse();
        result.ErrorKind.ShouldBe(InteractiveAuthErrorKind.AuthorizationServerError);
    }

    [Fact]
    public async Task RunAsync_DeviceCodeBlockedByPolicy_ReturnsPolicyBlockedAtInitiation()
    {
        // Tenant policy can block the /devicecode endpoint outright with an AAD error.
        var handler = new ScriptedHandler(new[]
        {
            (HttpStatusCode.BadRequest, """{"error":"AADSTS530032","error_description":"Conditional Access policy blocks the device code grant"}"""),
        });
        var flow = new DeviceCodeFlow(
            new AuthCodeExchanger(handler),
            handler,
            delay: (_, _) => Task.CompletedTask,
            requestTimeout: null);

        var result = await flow.RunAsync("c", "t", _ => { });

        result.Succeeded.ShouldBeFalse();
        result.ErrorKind.ShouldBe(InteractiveAuthErrorKind.PolicyBlocked);
    }

    [Fact]
    public async Task RunAsync_UnauthorizedClientDuringPoll_ReturnsPolicyBlocked()
    {
        var handler = new ScriptedHandler(new[]
        {
            (HttpStatusCode.OK, DeviceCodeOk),
            (HttpStatusCode.BadRequest, """{"error":"unauthorized_client","error_description":"policy"}"""),
        });
        var flow = new DeviceCodeFlow(
            new AuthCodeExchanger(handler),
            handler,
            delay: (_, _) => Task.CompletedTask,
            requestTimeout: null);

        var result = await flow.RunAsync("c", "t", _ => { });

        result.Succeeded.ShouldBeFalse();
        result.ErrorKind.ShouldBe(InteractiveAuthErrorKind.PolicyBlocked);
    }

    [Fact]
    public async Task RunAsync_TokenSucceedsWithoutRefreshToken_ReturnsTokenExchangeFailed()
    {
        // If AAD ever returns a token without offline_access (shouldn't happen because we
        // request it, but defend in depth), treat as failure since we can't bootstrap.
        var noRefresh = """{"access_token":"at","expires_in":3600}""";
        var handler = new ScriptedHandler(new[]
        {
            (HttpStatusCode.OK, DeviceCodeOk),
            (HttpStatusCode.OK, noRefresh),
        });
        var flow = new DeviceCodeFlow(
            new AuthCodeExchanger(handler),
            handler,
            delay: (_, _) => Task.CompletedTask,
            requestTimeout: null);

        var result = await flow.RunAsync("c", "t", _ => { });

        result.Succeeded.ShouldBeFalse();
        result.ErrorKind.ShouldBe(InteractiveAuthErrorKind.TokenExchangeFailed);
    }

    [Fact]
    public async Task RunAsync_DeviceCodeRequestSentToCorrectEndpoint()
    {
        var handler = new ScriptedHandler(new[]
        {
            (HttpStatusCode.OK, DeviceCodeOk),
            (HttpStatusCode.OK, TokenSuccess),
        });
        var flow = new DeviceCodeFlow(
            new AuthCodeExchanger(handler),
            handler,
            delay: (_, _) => Task.CompletedTask,
            requestTimeout: null);

        await flow.RunAsync("client-id", "organizations", _ => { });

        handler.Requests[0].RequestUri!.ToString().ShouldBe(
            "https://login.microsoftonline.com/organizations/oauth2/v2.0/devicecode");
        handler.Bodies[0].ShouldContain("client_id=client-id");
        handler.Bodies[0].ShouldContain("offline_access");
    }

    /// <summary>
    /// Replays a queued list of (status, body) responses in order. Captures every request.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public ScriptedHandler(IEnumerable<(HttpStatusCode, string)> responses)
        {
            _responses = new Queue<(HttpStatusCode, string)>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
            if (_responses.Count == 0)
                throw new InvalidOperationException("ScriptedHandler exhausted — test sent more requests than scripted.");
            var (status, body) = _responses.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
