using System.Net;
using Shouldly;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoErrorHandler"/>.
/// Covers URL parsing, revision extraction, auth header application, and status-code dispatch.
/// </summary>
public class AdoErrorHandlerTests
{
    // ── TryExtractWorkItemIdFromUrl ─────────────────────────────────

    [Theory]
    [InlineData("https://dev.azure.com/org/proj/_apis/wit/workitems/42", 42)]
    [InlineData("https://dev.azure.com/org/proj/_apis/wit/workitems/1?$expand=relations&api-version=7.1", 1)]
    [InlineData("https://dev.azure.com/myorg/_apis/wit/workitems/99999", 99999)]
    [InlineData("https://dev.azure.com/org/proj/_apis/wit/workitems/42/comments?api-version=7.1-preview.4", 42)]
    public void TryExtractWorkItemIdFromUrl_ValidWorkItemUrl_ReturnsId(string url, int expected)
    {
        var result = AdoErrorHandler.TryExtractWorkItemIdFromUrl(url);
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://dev.azure.com/org/proj/_apis/work/teamsettings/iterations")]
    [InlineData("https://dev.azure.com/org/proj/_apis/wit/wiql")]
    [InlineData("https://dev.azure.com/org/proj/_apis/wit/workitemtypes")]
    [InlineData("not-a-url-at-all")]
    [InlineData("")]
    public void TryExtractWorkItemIdFromUrl_NonWorkItemUrl_ReturnsZero(string url)
    {
        var result = AdoErrorHandler.TryExtractWorkItemIdFromUrl(url);
        result.ShouldBe(0);
    }

    // ── TryParseRevisionFromError ───────────────────────────────────

    [Theory]
    [InlineData("revision: 5", 5)]
    [InlineData("Revision: 12", 12)]
    [InlineData("The revision 42 does not match.", 42)]
    [InlineData("revision:100", 100)]
    public void TryParseRevisionFromError_ValidRevision_ReturnsRevision(string message, int expected)
    {
        var result = AdoErrorHandler.TryParseRevisionFromError(message);
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No revision info here.")]
    [InlineData("Some other error message")]
    public void TryParseRevisionFromError_NoRevision_ReturnsZero(string? message)
    {
        var result = AdoErrorHandler.TryParseRevisionFromError(message);
        result.ShouldBe(0);
    }

    // ── ApplyAuthHeader ─────────────────────────────────────────────

    [Fact]
    public void ApplyAuthHeader_BearerToken_SetsAuthorizationBearer()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

        AdoErrorHandler.ApplyAuthHeader(request, "eyJ0eXAi.bearer.token");

        request.Headers.Authorization.ShouldNotBeNull();
        request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        request.Headers.Authorization.Parameter.ShouldBe("eyJ0eXAi.bearer.token");
    }

    [Fact]
    public void ApplyAuthHeader_BasicToken_SetsRawAuthorizationHeader()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

        AdoErrorHandler.ApplyAuthHeader(request, "Basic dXNlcjpwYXNz");

        // Should be set as raw header, not parsed into AuthenticationHeaderValue
        request.Headers.TryGetValues("Authorization", out var values).ShouldBeTrue();
        values!.ShouldContain("Basic dXNlcjpwYXNz");
    }

    // ── ThrowOnErrorAsync — status code dispatch ────────────────────

    [Fact]
    public async Task ThrowOnErrorAsync_201_DoesNotThrow()
    {
        var response = CreateResponse(HttpStatusCode.Created);
        await AdoErrorHandler.ThrowOnErrorAsync(response, "https://example.com", CancellationToken.None);
    }

    [Fact]
    public async Task ThrowOnErrorAsync_400_ThrowsBadRequest()
    {
        var response = CreateResponse(HttpStatusCode.BadRequest, "{\"message\": \"Invalid field\"}");
        var url = "https://dev.azure.com/org/proj/_apis/wit/workitems/1";

        var ex = await Should.ThrowAsync<AdoBadRequestException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.Message.ShouldContain("Invalid field");
    }

    [Fact]
    public async Task ThrowOnErrorAsync_401_ThrowsAuthentication()
    {
        var response = CreateResponse(HttpStatusCode.Unauthorized);
        var url = "https://dev.azure.com/org/proj/_apis/wit/workitems/1";

        await Should.ThrowAsync<AdoAuthenticationException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));
    }

    [Fact]
    public async Task ThrowOnErrorAsync_404WorkItemUrl_ThrowsNotFoundWithId()
    {
        var response = CreateResponse(HttpStatusCode.NotFound);
        var url = "https://dev.azure.com/org/proj/_apis/wit/workitems/42?api-version=7.1";

        var ex = await Should.ThrowAsync<AdoNotFoundException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.WorkItemId.ShouldBe(42);
    }

    [Fact]
    public async Task ThrowOnErrorAsync_404NonWorkItemUrl_ThrowsNotFoundWithNullId()
    {
        var response = CreateResponse(HttpStatusCode.NotFound);
        var url = "https://dev.azure.com/org/proj/_apis/work/teamsettings/iterations";

        var ex = await Should.ThrowAsync<AdoNotFoundException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.WorkItemId.ShouldBeNull();
    }

    [Fact]
    public async Task ThrowOnErrorAsync_412_ThrowsConflict()
    {
        var response = CreateResponse(HttpStatusCode.PreconditionFailed, "{\"message\": \"revision: 7\"}");
        var url = "https://dev.azure.com/org/proj/_apis/wit/workitems/1";

        var ex = await Should.ThrowAsync<AdoConflictException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.ServerRevision.ShouldBe(7);
    }

    [Fact]
    public async Task ThrowOnErrorAsync_429_ThrowsRateLimit()
    {
        var response = CreateResponse((HttpStatusCode)429);
        var url = "https://dev.azure.com/org/proj/_apis/wit/workitems/1";

        var ex = await Should.ThrowAsync<AdoRateLimitException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.RetryAfter.TotalSeconds.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ThrowOnErrorAsync_429_WithRetryAfterHeader_CarriesRetryAfterValue()
    {
        var response = CreateResponse((HttpStatusCode)429);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
        var url = "https://dev.azure.com/org/proj/_apis/wit/workitems/1";

        var ex = await Should.ThrowAsync<AdoRateLimitException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(2));
        ex.Message.ShouldContain("2");
    }

    [Fact]
    public async Task ThrowOnErrorAsync_429_WithoutRetryAfterHeader_DefaultsTo10Seconds()
    {
        var response = CreateResponse((HttpStatusCode)429);
        var url = "https://dev.azure.com/org/proj/_apis/wit/workitems/1";

        var ex = await Should.ThrowAsync<AdoRateLimitException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ThrowOnErrorAsync_500_ThrowsServerException()
    {
        var response = CreateResponse(HttpStatusCode.InternalServerError, "{\"message\": \"Oops\"}");
        var url = "https://dev.azure.com/org/proj/_apis/wit/workitems/1";

        var ex = await Should.ThrowAsync<AdoServerException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.StatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task ThrowOnErrorAsync_503_ThrowsServerException()
    {
        var response = CreateResponse(HttpStatusCode.ServiceUnavailable);
        var url = "https://dev.azure.com/org/proj/_apis/wit/workitems/1";

        var ex = await Should.ThrowAsync<AdoServerException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.StatusCode.ShouldBe(503);
    }

    [Fact]
    public async Task ThrowOnErrorAsync_503_WithBody_IncludesServerMessage()
    {
        var response = CreateResponse(HttpStatusCode.ServiceUnavailable, """{"message": "Service temporarily unavailable"}""");
        var url = "https://dev.azure.com/org/proj/_apis/wit/workitems/1";

        var ex = await Should.ThrowAsync<AdoServerException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.StatusCode.ShouldBe(503);
        ex.Message.ShouldContain("503");
        ex.Message.ShouldContain("Service temporarily unavailable");
    }

    // ── ThrowOnErrorAsync — Content-Type validation (2xx) ──────────

    [Fact]
    public async Task ThrowOnErrorAsync_200_HtmlContentType_ThrowsUnexpectedResponse()
    {
        var response = CreateResponse(HttpStatusCode.OK, "<html><body>Sign in</body></html>");
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
        var url = "https://dev.azure.com/org/proj/_apis/wit/workitems/42";

        var ex = await Should.ThrowAsync<AdoUnexpectedResponseException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.StatusCode.ShouldBe(200);
        ex.ContentType.ShouldBe("text/html");
        ex.RequestUrl.ShouldBe(url);
        ex.BodySnippet.ShouldBe("<html><body>Sign in</body></html>");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("Application/JSON")]
    public async Task ThrowOnErrorAsync_200_JsonContentType_DoesNotThrow(string contentType)
    {
        var response = CreateResponse(HttpStatusCode.OK, """{"id": 1}""");
        response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

        await AdoErrorHandler.ThrowOnErrorAsync(response, "https://example.com", CancellationToken.None);
    }

    [Fact]
    public async Task ThrowOnErrorAsync_200_MissingContentType_DoesNotThrow()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new ByteArrayContent([]);
        response.Content.Headers.ContentType = null;

        await AdoErrorHandler.ThrowOnErrorAsync(response, "https://example.com", CancellationToken.None);
    }

    [Theory]
    [InlineData("", "text/html")]
    [InlineData("   ", "text/plain")]
    public async Task ThrowOnErrorAsync_200_NonJsonEmptyOrWhitespaceBody_DoesNotThrow(string body, string contentType)
    {
        var response = CreateResponse(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        await AdoErrorHandler.ThrowOnErrorAsync(response, "https://example.com", CancellationToken.None);
    }

    [Fact]
    public async Task ThrowOnErrorAsync_200_LargeHtmlBody_SnippetTruncatedTo500Chars()
    {
        var largeBody = new string('X', 1000);
        var response = CreateResponse(HttpStatusCode.OK, largeBody);
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
        var url = "https://dev.azure.com/org/proj/_apis/wit/workitems/1";

        var ex = await Should.ThrowAsync<AdoUnexpectedResponseException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.BodySnippet.Length.ShouldBe(500);
    }

    [Fact]
    public async Task ThrowOnErrorAsync_200_TextPlainWithBody_ThrowsUnexpectedResponse()
    {
        var response = CreateResponse(HttpStatusCode.OK, "Not JSON");
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        var url = "https://example.com/api";

        var ex = await Should.ThrowAsync<AdoUnexpectedResponseException>(
            () => AdoErrorHandler.ThrowOnErrorAsync(response, url, CancellationToken.None));

        ex.ContentType.ShouldBe("text/plain");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static HttpResponseMessage CreateResponse(
        HttpStatusCode statusCode,
        string? content = null,
        string mediaType = "application/json")
    {
        var response = new HttpResponseMessage(statusCode);
        response.Content = new StringContent(content ?? string.Empty, System.Text.Encoding.UTF8, mediaType);
        return response;
    }
}
