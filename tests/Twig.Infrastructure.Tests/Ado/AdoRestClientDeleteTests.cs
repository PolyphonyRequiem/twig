using System.Net;
using System.Text;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Unit tests for <see cref="AdoRestClient.DeleteAsync"/>.
/// Verifies HTTP DELETE request, 404 idempotent handling, and error mapping.
/// </summary>
public sealed class AdoRestClientDeleteTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "testproject";

    [Fact]
    public async Task DeleteAsync_SendsHttpDelete()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.DeleteAsync(42);

        handler.RequestCount.ShouldBe(1);
        handler.LastMethod.ShouldBe("DELETE");
    }

    [Fact]
    public async Task DeleteAsync_UrlContainsWorkItemId()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.DeleteAsync(1234);

        handler.LastUrl.ShouldNotBeNull();
        handler.LastUrl.ShouldContain("/workitems/1234");
    }

    [Fact]
    public async Task DeleteAsync_UrlContainsApiVersion()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.DeleteAsync(1);

        handler.LastUrl.ShouldNotBeNull();
        handler.LastUrl.ShouldContain("api-version=7.1");
    }

    [Fact]
    public async Task DeleteAsync_NoContentResponse_Succeeds()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.NoContent);
        var client = CreateClient(handler);

        // Should not throw
        await client.DeleteAsync(42);

        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteAsync_NotFound_TreatedAsIdempotentSuccess()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.NotFound);
        var client = CreateClient(handler);

        // 404 should NOT throw — item is already deleted
        await client.DeleteAsync(999);

        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteAsync_Unauthorized_ThrowsAdoAuthenticationException()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.Unauthorized);
        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoAuthenticationException>(
            () => client.DeleteAsync(42));
    }

    [Fact]
    public async Task DeleteAsync_Offline_ThrowsAdoOfflineException()
    {
        var handler = new ThrowingHandler(new HttpRequestException("Network error"));
        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoOfflineException>(
            () => client.DeleteAsync(42));
    }

    [Fact]
    public async Task DeleteAsync_Forbidden_ThrowsAdoException()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.Forbidden);
        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoException>(
            () => client.DeleteAsync(42));
    }

    [Fact]
    public async Task DeleteAsync_ServerError_ThrowsAdoServerException()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoServerException>(
            () => client.DeleteAsync(42));
    }

    [Fact]
    public async Task DeleteAsync_SendsNoRequestBody()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.DeleteAsync(42);

        handler.LastRequestBody.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_IncludesAuthHeader()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.DeleteAsync(42);

        handler.LastAuthHeader.ShouldNotBeNull();
        handler.LastAuthHeader.ShouldContain("Bearer");
    }

    // ── Strict CAS: DeleteAtRevisionAsync ──────────────────────────

    [Fact]
    public async Task DeleteAtRevisionAsync_SendsIfMatchWithExpectedRevision()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.NoContent);
        var client = CreateClient(handler);

        await client.DeleteAtRevisionAsync(id: 42, expectedRevision: 17);

        handler.RequestCount.ShouldBe(1);
        handler.LastMethod.ShouldBe("DELETE");
        handler.LastIfMatch.ShouldBe("17");
    }

    [Fact]
    public async Task DeleteAtRevisionAsync_UrlTargetsWorkItemById()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.NoContent);
        var client = CreateClient(handler);

        await client.DeleteAtRevisionAsync(id: 1234, expectedRevision: 1);

        handler.LastUrl.ShouldNotBeNull();
        handler.LastUrl.ShouldContain("/workitems/1234");
    }

    [Fact]
    public async Task DeleteAtRevisionAsync_SendsExactlyOneRequest_NoRefetchOrRetry()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.NoContent);
        var client = CreateClient(handler);

        await client.DeleteAtRevisionAsync(id: 42, expectedRevision: 5);

        // Strict CAS: exactly one HTTP call. No GET refetch, no ConflictRetryHelper.
        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteAtRevisionAsync_NotFound_TreatedAsIdempotentSuccess()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.NotFound);
        var client = CreateClient(handler);

        // 404 means the item is already gone — delete goal state achieved.
        await client.DeleteAtRevisionAsync(id: 999, expectedRevision: 3);

        handler.RequestCount.ShouldBe(1);
        handler.LastIfMatch.ShouldBe("3");
    }

    [Fact]
    public async Task DeleteAtRevisionAsync_PreconditionFailed_ThrowsAdoConflict()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.PreconditionFailed);
        var client = CreateClient(handler);

        var ex = await Should.ThrowAsync<AdoConflictException>(
            () => client.DeleteAtRevisionAsync(id: 42, expectedRevision: 5));

        // 412 must surface without retry.
        handler.RequestCount.ShouldBe(1);
        ex.ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteAtRevisionAsync_SendsNoRequestBody()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.NoContent);
        var client = CreateClient(handler);

        await client.DeleteAtRevisionAsync(id: 42, expectedRevision: 1);

        handler.LastRequestBody.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAtRevisionAsync_Unauthorized_SurfacesAuthFailureWithoutRetry()
    {
        var handler = new DeleteTrackingHandler(HttpStatusCode.Unauthorized);
        var client = CreateClient(handler);

        // Strict CAS: a 401 auth challenge is surfaced immediately — no InvalidateToken
        // + retry loop. Even though the DELETE has no body (which would ordinarily
        // permit a single auth retry in SendAsync), DeleteAtRevisionAsync opts out.
        await Should.ThrowAsync<AdoAuthenticationException>(
            () => client.DeleteAtRevisionAsync(id: 42, expectedRevision: 5));

        handler.RequestCount.ShouldBe(1);
        handler.LastIfMatch.ShouldBe("5");
    }

    [Fact]
    public async Task DeleteAtRevisionAsync_HtmlAuthChallenge_SurfacesAuthFailureWithoutRetry()
    {
        // ADO sign-in redirect: HTTP 203 Non-Authoritative Information with an HTML body
        // is the classic stale-token challenge. In strict CAS mode we must NOT retry.
        var handler = new HtmlAuthChallengeHandler();
        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoUnexpectedResponseException>(
            () => client.DeleteAtRevisionAsync(id: 42, expectedRevision: 5));

        handler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteAsync_Unauthorized_RetriesOnceWithFreshToken()
    {
        // Regression guard: non-strict DeleteAsync retains the historical
        // invalidate-and-retry-once behavior for empty-body requests. If this ever
        // changes, the strict/non-strict split is broken.
        var handler = new DeleteTrackingHandler(HttpStatusCode.Unauthorized);
        var client = CreateClient(handler);

        await Should.ThrowAsync<AdoAuthenticationException>(
            () => client.DeleteAsync(42));

        handler.RequestCount.ShouldBe(2);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AdoRestClient CreateClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var auth = new FakeAuthProvider();
        return new AdoRestClient(http, auth, OrgUrl, Project, new WorkItemMapper());
    }

    private sealed class FakeAuthProvider : IAuthenticationProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult("fake-bearer-token");

        public void InvalidateToken() { }
    }

    /// <summary>
    /// HttpMessageHandler that returns a fixed status code and captures request details.
    /// </summary>
    private sealed class DeleteTrackingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastUrl { get; private set; }
        public string? LastMethod { get; private set; }
        public string? LastRequestBody { get; private set; }
        public string? LastAuthHeader { get; private set; }
        public string? LastIfMatch { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastUrl = request.RequestUri?.ToString();
            LastMethod = request.Method.Method;
            LastAuthHeader = request.Headers.Authorization?.ToString();

            if (request.Headers.TryGetValues("If-Match", out var ifMatch))
                LastIfMatch = ifMatch.FirstOrDefault();

            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            // For success responses, return minimal JSON that the error handler accepts
            if (statusCode == HttpStatusCode.OK)
            {
                var json = """{"id":1,"rev":1,"fields":{"System.WorkItemType":"Task","System.Title":"Test","System.State":"New"}}""";
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("", Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// HttpMessageHandler that throws an exception to simulate offline/network errors.
    /// </summary>
    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw exception;
        }
    }

    /// <summary>
    /// HttpMessageHandler that returns HTTP 203 Non-Authoritative Information with an
    /// HTML body. Mirrors the ADO sign-in redirect the error handler classifies as an
    /// auth challenge (surfaced as <see cref="AdoUnexpectedResponseException"/>).
    /// </summary>
    private sealed class HtmlAuthChallengeHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage((HttpStatusCode)203)
            {
                Content = new StringContent(
                    "<html><body>Sign in</body></html>", Encoding.UTF8, "text/html"),
            };
            return Task.FromResult(response);
        }
    }
}
