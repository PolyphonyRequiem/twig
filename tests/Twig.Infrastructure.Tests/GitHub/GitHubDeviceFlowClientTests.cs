using System.Net;
using System.Text;
using Shouldly;
using Twig.Infrastructure.GitHub;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubDeviceFlowClientTests
{
    private const string DeviceSecret = "synthetic-device-secret";
    private const string TokenSecret = "synthetic-access-secret";
    private const string Code = """{"device_code":"synthetic-device-secret","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""";
    private const string Token = """{"access_token":"synthetic-access-secret","token_type":"bearer","scope":""}""";

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "repo")]
    public async Task ExplicitScope_UsesFixedPublicClientAndEndpoints(bool privateRepositories, string requested)
    {
        var clock = new Clock();
        using var wire = new Wire(Code, Token.Replace("\"scope\":\"\"", $"\"scope\":\"{requested}\""));
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        var displayed = new List<GitHubDeviceInstructions>();
        var grant = await client.RunAsync(privateRepositories ? GitHubDeviceScope.PrivateRepositories
            : GitHubDeviceScope.PublicRepositories, displayed.Add);
        grant.AccessToken.ShouldBe(TokenSecret);
        grant.Scope.ShouldBe(requested);
        grant.ExpiresIn.ShouldBeNull();
        grant.ToString().ShouldBe(nameof(GitHubDeviceGrant));
        displayed.ShouldHaveSingleItem();
        displayed[0].VerificationUri.ShouldBe(new Uri("https://github.com/login/device"));
        displayed[0].UserCode.ShouldBe("ABCD-1234");
        displayed[0].ToString().ShouldNotContain(DeviceSecret);
        wire.Requests.Select(r => r.Uri).ShouldBe(new[]
        {
            "https://github.com/login/device/code", "https://github.com/login/oauth/access_token",
        });
        wire.Requests[0].Form.ShouldBe($"client_id={GitHubDeviceFlowClient.ClientId}&scope={requested}");
        wire.Requests[1].Form.ShouldContain("device_code=" + DeviceSecret);
        wire.Requests[1].Form.ShouldContain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code");
        wire.Requests.ShouldAllBe(r => r.Authorization == null && r.Cookie == null && r.Accept == "application/json");
        clock.Delays.ShouldBe(new[] { TimeSpan.FromSeconds(5) });
    }

    [Fact]
    public async Task PendingAndSlowDown_WaitBeforeEveryPoll_AndAddFiveSeconds()
    {
        var clock = new Clock();
        using var wire = new Wire(Code, """{"error":"authorization_pending"}""",
            """{"error":"slow_down"}""", Token);
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        await client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { });
        clock.Delays.Select(d => d.TotalSeconds).ShouldBe(new[] { 5d, 5d, 10d });
    }

    [Theory]
    [InlineData("access_denied")]
    [InlineData("expired_token")]
    [InlineData("incorrect_client_credentials")]
    [InlineData("incorrect_device_code")]
    [InlineData("unsupported_grant_type")]
    [InlineData("device_flow_disabled")]
    [InlineData("unrecognized-synthetic-secret")]
    public async Task TerminalErrors_DoNotRepeatOrExposeServerBody(string error)
    {
        var clock = new Clock();
        using var wire = new Wire(Code, $$"""{"error":"{{error}}","error_description":"synthetic-access-secret"}""");
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        var exception = await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }));
        exception.ToString().ShouldNotContain(error);
        exception.ToString().ShouldNotContain(TokenSecret);
        exception.InnerException.ShouldBeNull();
        wire.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("""{"access_token":"synthetic-access-secret","error":"authorization_pending"}""")]
    [InlineData("""{"access_token":"synthetic-access-secret","token_type":"bearer","scope":"repo"}""")]
    [InlineData("""{"access_token":"synthetic-access-secret","token_type":"basic","scope":""}""")]
    [InlineData("""{"access_token":"two tokens","token_type":"bearer","scope":""}""")]
    [InlineData("""{"access_token":"synthetic-access-secret","access_token":"other","token_type":"bearer","scope":""}""")]
    [InlineData("""{"access_token":"synthetic-access-secret","token_type":"bearer","scope":"","expires_in":0}""")]
    [InlineData("""{"access_token":42,"token_type":"bearer","scope":""}""")]
    [InlineData("[]")]
    [InlineData("synthetic-access-secret")]
    public async Task InvalidTokenResponse_RefusesWithoutLeaking(string response)
    {
        var clock = new Clock();
        using var wire = new Wire(Code, response);
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        var exception = await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }));
        exception.ToString().ShouldNotContain(TokenSecret);
        wire.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("https://github.com/login/device", "https://evil.example/login/device")]
    [InlineData("https://github.com/login/device", "https://github.com/login/device?secret=value")]
    [InlineData("ABCD-1234", "ABCD/1234")]
    [InlineData("\"expires_in\":900", "\"expires_in\":0")]
    [InlineData("\"expires_in\":900", "\"expires_in\":901")]
    [InlineData("\"interval\":5", "\"interval\":0")]
    [InlineData("\"interval\":5", "\"interval\":null")]
    [InlineData("synthetic-device-secret", "bad secret")]
    public async Task InvalidDeviceResponse_DoesNotDisplayOrPoll(string oldValue, string newValue)
    {
        var clock = new Clock();
        using var wire = new Wire(Code.Replace(oldValue, newValue));
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        var displays = 0;
        await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => displays++));
        displays.ShouldBe(0);
        wire.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task MonotonicExpiry_IncludesInstructionsAndStopsBeforeNextPoll()
    {
        var clock = new Clock();
        using var wire = new Wire(Code);
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => clock.Advance(900)));
        wire.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExpiryDuringResponse_CannotReturnSuccessfulGrant()
    {
        var clock = new Clock();
        using var wire = new Wire(Code, Token) { BeforeResponse = call => { if (call == 2) clock.Advance(900); } };
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }));
    }

    [Fact]
    public async Task CancellationBeforeStart_DoesNotSend()
    {
        var clock = new Clock();
        using var wire = new Wire(Code);
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        await Should.ThrowAsync<OperationCanceledException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }, new CancellationToken(true)));
        wire.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpFailures_AreTerminalWithoutBodyLeak(HttpStatusCode status)
    {
        var clock = new Clock();
        using var wire = new Wire(TokenSecret) { Status = status };
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        var exception = await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }));
        exception.ToString().ShouldNotContain(TokenSecret);
        wire.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OversizeUnknownLengthResponse_IsBounded()
    {
        var clock = new Clock();
        using var wire = new Wire(new string('x', GitHubDeviceFlowClient.MaxResponseBytes + 1)) { UnknownLength = true };
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        var exception = await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }));
        exception.Message.ShouldContain("response size exceeded");
    }

    [Fact]
    public async Task OversizeDeclaredLengthResponse_IsRefused()
    {
        var clock = new Clock();
        using var wire = new Wire(new string('x', GitHubDeviceFlowClient.MaxResponseBytes + 1));
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        (await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }))).Message.ShouldContain("response size exceeded");
    }

    [Fact]
    public async Task DuplicateDeviceField_DoesNotDisplay()
    {
        var clock = new Clock();
        using var wire = new Wire(Code.Replace("\"interval\":5", "\"interval\":5,\"interval\":1"));
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        var shown = false;
        await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => shown = true));
        shown.ShouldBeFalse();
    }

    [Fact]
    public async Task WrongContentType_IsTerminal()
    {
        var clock = new Clock();
        using var wire = new Wire(Code) { ContentType = "text/html" };
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }));
        wire.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CancellationDuringDelay_DoesNotPoll()
    {
        var clock = new Clock();
        using var cancelled = new CancellationTokenSource();
        using var wire = new Wire(Code);
        using var client = new GitHubDeviceFlowClient(wire, clock, (_, ct) =>
        {
            cancelled.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await Should.ThrowAsync<OperationCanceledException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }, cancelled.Token));
        wire.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RequestBudget_CancelsTransport_AndCannotReturnLateSuccess()
    {
        var clock = new Clock();
        using var wire = new Wire(Code) { BeforeResponse = _ => clock.Advance(15) };
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        (await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }))).Message.ShouldContain("request deadline exceeded");
        wire.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PollRequestBudget_CannotReturnLateToken()
    {
        var clock = new Clock();
        using var wire = new Wire(Code, Token) { BeforeResponse = call => { if (call == 2) clock.Advance(15); } };
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        (await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }))).Message.ShouldContain("request deadline exceeded");
        wire.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CallerCancellationDuringTransport_RemainsCancellationWithoutSecret()
    {
        var clock = new Clock();
        using var cancelled = new CancellationTokenSource();
        using var wire = new Wire(Code) { BeforeResponse = _ =>
        {
            cancelled.Cancel();
            throw new OperationCanceledException(TokenSecret);
        } };
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        var error = await Should.ThrowAsync<OperationCanceledException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }, cancelled.Token));
        error.CancellationToken.ShouldBe(cancelled.Token);
        error.ToString().ShouldNotContain(TokenSecret);
    }

    [Fact]
    public async Task InvalidScope_DoesNotSendOrDisplay()
    {
        var clock = new Clock();
        using var wire = new Wire(Code);
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            client.RunAsync((GitHubDeviceScope)99, _ => throw new InvalidOperationException()));
        wire.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task TransportError_DoesNotExposeInnerSecret()
    {
        var clock = new Clock();
        using var wire = new Wire(Code) { BeforeResponse = _ => throw new HttpRequestException(TokenSecret) };
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        var error = await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }));
        error.ToString().ShouldNotContain(TokenSecret);
        error.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task ShortExpiry_DoesNotPollTooSoon()
    {
        var clock = new Clock();
        using var wire = new Wire(Code.Replace("\"expires_in\":900", "\"expires_in\":5"));
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        await Should.ThrowAsync<GitHubDeviceFlowException>(() =>
            client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { }));
        clock.Delays.ShouldBeEmpty();
        wire.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExpiringGrant_DoesNotRequireOrExposeRefreshToken()
    {
        var clock = new Clock();
        using var wire = new Wire(Code, Token.Replace("\"scope\":\"\"",
            "\"scope\":\"\",\"expires_in\":28800,\"refresh_token\":\"synthetic-refresh-secret\""));
        using var client = new GitHubDeviceFlowClient(wire, clock, clock.Delay);
        var grant = await client.RunAsync(GitHubDeviceScope.PublicRepositories, _ => { });
        grant.ExpiresIn.ShouldBe(28800);
        grant.ToString().ShouldNotContain("synthetic-refresh-secret");
    }

    [Fact]
    public void ProductionHandler_DisablesRedirectsCookiesAndAmbientCredentials()
    {
        using var handler = GitHubDeviceFlowClient.CreateHandler();
        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseCookies.ShouldBeFalse();
        handler.UseProxy.ShouldBeFalse();
        handler.Credentials.ShouldBeNull();
        new GitHubDeviceAuthorizationResponse { DeviceCode = DeviceSecret }.ToString().ShouldNotContain(DeviceSecret);
        new GitHubDeviceTokenResponse { AccessToken = TokenSecret, Error = DeviceSecret }.ToString().ShouldNotContain(TokenSecret);
    }

    private sealed class Clock : TimeProvider
    {
        private long _seconds;
        private readonly List<Timer> _timers = [];
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _seconds;
        internal List<TimeSpan> Delays { get; } = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        internal void Advance(long seconds)
        {
            _seconds += seconds;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }
        internal Task Delay(TimeSpan duration, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Delays.Add(duration);
            Advance((long)duration.TotalSeconds);
            return Task.CompletedTask;
        }
        private sealed class Timer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            private double _due;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? double.PositiveInfinity
                    : owner._seconds + dueTime.TotalSeconds;
                return true;
            }
            internal void FireIfDue()
            {
                if (_disposed || owner._seconds < _due) return;
                _due = double.PositiveInfinity;
                callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Wire(params string[] replies) : HttpMessageHandler
    {
        internal List<(string Uri, string Form, string? Authorization, string? Cookie, string Accept)> Requests { get; } = [];
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        internal bool UnknownLength { get; init; }
        internal string ContentType { get; init; } = "application/json";
        internal Action<int>? BeforeResponse { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add((request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(ct),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join(",", cookies) : null,
                request.Headers.Accept.ToString()));
            BeforeResponse?.Invoke(Requests.Count);
            var body = replies[Requests.Count - 1];
            HttpContent content = UnknownLength ? new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(body)))
                : new StringContent(body, Encoding.UTF8, "application/json");
            content.Headers.ContentType = new(ContentType);
            if (UnknownLength) content.Headers.ContentLength = null;
            return new(Status) { Content = content };
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
