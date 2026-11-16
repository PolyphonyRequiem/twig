using System.Net;
using System.Text.Json;
using Shouldly;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

/// <summary>
/// Tests for <see cref="MsalTokenRefresher"/> — direct HTTP refresh of selected identity credentials.
/// </summary>
public class MsalTokenRefresherTests
{
    #region TryRefreshAsync tests

    [Fact]
    public async Task TryRefreshAsync_SuccessfulRefresh_ReturnsAccessToken()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK,
            """{"access_token":"new-access-token","expires_in":3600,"token_type":"Bearer"}""");
        var refresher = new MsalTokenRefresher(handler);

        var (token, rotatedRt, isInvalidGrant) = await refresher.TryRefreshAsync(
            "refresh-token", "client-id", "tenant-id", "login.microsoftonline.com");

        token.ShouldBe("new-access-token");
        rotatedRt.ShouldBeNull(); // server didn't rotate
        isInvalidGrant.ShouldBeFalse();
    }

    [Fact]
    public async Task TryRefreshAsync_ResponseIncludesRotatedRefreshToken_CapturesIt()
    {
        // AAD typically rotates refresh tokens on every successful exchange. Capturing the
        // rotated RT is what keeps the 90-day inactivity window sliding.
        var handler = new FakeHttpHandler(HttpStatusCode.OK,
            """{"access_token":"new-access-token","refresh_token":"rotated-rt-xyz","expires_in":3600}""");
        var refresher = new MsalTokenRefresher(handler);

        var (token, rotatedRt, isInvalidGrant) = await refresher.TryRefreshAsync(
            "old-rt", "client", "tenant", "login.microsoftonline.com");

        token.ShouldBe("new-access-token");
        rotatedRt.ShouldBe("rotated-rt-xyz");
        isInvalidGrant.ShouldBeFalse();
    }

    [Fact]
    public async Task TryRefreshAsync_RequestsOfflineAccessScope()
    {
        // offline_access is required for AAD to return refresh_token in the response —
        // without it the rotation never happens.
        var handler = new FakeHttpHandler(HttpStatusCode.OK,
            """{"access_token":"tok","expires_in":3600}""");
        var refresher = new MsalTokenRefresher(handler);

        await refresher.TryRefreshAsync("rt", "client", "tenant", "login.microsoftonline.com");

        var body = await handler.LastRequest!.Content!.ReadAsStringAsync();
        body.ShouldContain("offline_access");
    }

    [Fact]
    public async Task TryRefreshAsync_SendsCorrectRequest()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK,
            """{"access_token":"tok","expires_in":3600}""");
        var refresher = new MsalTokenRefresher(handler);

        await refresher.TryRefreshAsync(
            "my-refresh-token", "my-client", "my-tenant", "login.microsoftonline.us");

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .ShouldBe("https://login.microsoftonline.us/my-tenant/oauth2/v2.0/token");
        handler.LastRequest.Method.ShouldBe(HttpMethod.Post);

        var body = await handler.LastRequest.Content!.ReadAsStringAsync();
        body.ShouldContain("client_id=my-client");
        body.ShouldContain("grant_type=refresh_token");
        body.ShouldContain("refresh_token=my-refresh-token");
        body.ShouldContain("scope=499b84ac-1321-427f-aa17-267ca6975798");
    }

    [Fact]
    public async Task TryRefreshAsync_InvalidGrant_ReturnsNullWithFlag()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"AADSTS700082: The refresh token has expired."}""");
        var refresher = new MsalTokenRefresher(handler);

        var (token, rotatedRt, isInvalidGrant) = await refresher.TryRefreshAsync(
            "expired-rt", "client", "tenant", "login.microsoftonline.com");

        token.ShouldBeNull();
        rotatedRt.ShouldBeNull();
        isInvalidGrant.ShouldBeTrue();
    }

    [Fact]
    public async Task TryRefreshAsync_ServerError_ReturnsNullNoFlag()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.InternalServerError, "Server Error");
        var refresher = new MsalTokenRefresher(handler);

        var (token, rotatedRt, isInvalidGrant) = await refresher.TryRefreshAsync(
            "rt", "client", "tenant", "login.microsoftonline.com");

        token.ShouldBeNull();
        rotatedRt.ShouldBeNull();
        isInvalidGrant.ShouldBeFalse();
    }

    [Fact]
    public async Task TryRefreshAsync_Timeout_ReturnsNullNoFlag()
    {
        var handler = new SlowHttpHandler(delay: TimeSpan.FromSeconds(10));
        var refresher = new MsalTokenRefresher(handler, timeout: TimeSpan.FromMilliseconds(50));

        var (token, rotatedRt, isInvalidGrant) = await refresher.TryRefreshAsync(
            "rt", "client", "tenant", "login.microsoftonline.com");

        token.ShouldBeNull();
        rotatedRt.ShouldBeNull();
        isInvalidGrant.ShouldBeFalse();
    }

    [Fact]
    public async Task TryRefreshAsync_NetworkError_ReturnsNullNoFlag()
    {
        var handler = new ThrowingHttpHandler(new HttpRequestException("DNS failure"));
        var refresher = new MsalTokenRefresher(handler);

        var (token, rotatedRt, isInvalidGrant) = await refresher.TryRefreshAsync(
            "rt", "client", "tenant", "login.microsoftonline.com");

        token.ShouldBeNull();
        rotatedRt.ShouldBeNull();
        isInvalidGrant.ShouldBeFalse();
    }

    [Fact]
    public async Task TryRefreshAsync_EmptyAccessToken_ReturnsNull()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK,
            """{"access_token":"","expires_in":3600}""");
        var refresher = new MsalTokenRefresher(handler);

        var (token, rotatedRt, isInvalidGrant) = await refresher.TryRefreshAsync(
            "rt", "client", "tenant", "login.microsoftonline.com");

        token.ShouldBeNull();
        rotatedRt.ShouldBeNull();
        isInvalidGrant.ShouldBeFalse();
    }

    [Fact]
    public async Task TryRefreshAsync_CallerCancellation_ThrowsOperationCanceled()
    {
        var handler = new SlowHttpHandler(delay: TimeSpan.FromSeconds(10));
        var refresher = new MsalTokenRefresher(handler, timeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Caller cancellation should propagate (not be swallowed)
        await Should.ThrowAsync<OperationCanceledException>(
            () => refresher.TryRefreshAsync("rt", "client", "tenant", "host", cts.Token));
    }

    #endregion

    #region Test helpers

    private sealed class FakeHttpHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class SlowHttpHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"late"}""")
            };
        }
    }

    private sealed class ThrowingHttpHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            throw exception;
        }
    }

    #endregion
}
