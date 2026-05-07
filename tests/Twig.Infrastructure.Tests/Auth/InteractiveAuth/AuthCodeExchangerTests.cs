using System.Net;
using System.Net.Http;
using System.Text;
using Shouldly;
using Twig.Infrastructure.Auth.InteractiveAuth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth.InteractiveAuth;

/// <summary>
/// Tests for <see cref="AuthCodeExchanger"/> — POSTs to AAD's /token endpoint for both
/// authorization-code and device-code grants.
/// </summary>
public class AuthCodeExchangerTests
{
    private const string SuccessBody = """
        {"access_token":"at","refresh_token":"rt","id_token":"it","token_type":"Bearer","expires_in":3600,"scope":"x/.default offline_access"}
        """;

    [Fact]
    public async Task ExchangeCodeAsync_SendsAuthCodeFormEncodedToCorrectEndpoint()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);
        var exchanger = new AuthCodeExchanger(handler);

        await exchanger.ExchangeCodeAsync(
            code: "the-code",
            codeVerifier: "the-verifier",
            redirectUri: "http://localhost:1234/",
            clientId: "the-client",
            tenant: "organizations");

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest.RequestUri!.ToString().ShouldBe(
            "https://login.microsoftonline.com/organizations/oauth2/v2.0/token");
        handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
        handler.LastBody.ShouldContain("grant_type=authorization_code");
        handler.LastBody.ShouldContain("code=the-code");
        handler.LastBody.ShouldContain("code_verifier=the-verifier");
        handler.LastBody.ShouldContain("client_id=the-client");
        // URL encoded redirect_uri
        handler.LastBody.ShouldContain("redirect_uri=http%3A%2F%2Flocalhost%3A1234%2F");
    }

    [Fact]
    public async Task ExchangeCodeAsync_ReturnsParsedTokensOnSuccess()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);
        var exchanger = new AuthCodeExchanger(handler);

        var result = await exchanger.ExchangeCodeAsync("c", "v", "http://localhost/", "id", "t");

        result.IsSuccess.ShouldBeTrue();
        result.Tokens.ShouldNotBeNull();
        result.Tokens!.AccessToken.ShouldBe("at");
        result.Tokens.RefreshToken.ShouldBe("rt");
        result.Tokens.IdToken.ShouldBe("it");
        result.Tokens.ExpiresIn.ShouldBe(3600);
    }

    [Fact]
    public async Task ExchangeCodeAsync_ReturnsErrorWhenAadReturnsErrorPayload()
    {
        var errorBody = """{"error":"invalid_grant","error_description":"AADSTS70008: ..."}""";
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, errorBody);
        var exchanger = new AuthCodeExchanger(handler);

        var result = await exchanger.ExchangeCodeAsync("c", "v", "http://localhost/", "id", "t");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("invalid_grant");
        result.ErrorDescription!.ShouldContain("AADSTS70008");
    }

    [Fact]
    public async Task ExchangeCodeAsync_HandlesEmptyResponseBody()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "");
        var exchanger = new AuthCodeExchanger(handler);

        var result = await exchanger.ExchangeCodeAsync("c", "v", "http://localhost/", "id", "t");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldNotBeNull();
    }

    [Fact]
    public async Task ExchangeDeviceCodeAsync_SendsDeviceCodeGrantType()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);
        var exchanger = new AuthCodeExchanger(handler);

        await exchanger.ExchangeDeviceCodeAsync("the-device-code", "client-id", "organizations");

        handler.LastBody.ShouldContain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code");
        handler.LastBody.ShouldContain("device_code=the-device-code");
        handler.LastBody.ShouldContain("client_id=client-id");
    }

    [Fact]
    public async Task ExchangeDeviceCodeAsync_PreservesAuthorizationPendingErrorForPollingLoop()
    {
        var pendingBody = """{"error":"authorization_pending","error_description":"User has not completed sign-in."}""";
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, pendingBody);
        var exchanger = new AuthCodeExchanger(handler);

        var result = await exchanger.ExchangeDeviceCodeAsync("dc", "id", "t");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("authorization_pending");
    }

    [Fact]
    public async Task ExchangeCodeAsync_HandlesNetworkExceptionGracefully()
    {
        var handler = new ThrowingHandler();
        var exchanger = new AuthCodeExchanger(handler);

        var result = await exchanger.ExchangeCodeAsync("c", "v", "http://localhost/", "id", "t");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("network_error");
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody) };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("simulated network failure");
    }
}
