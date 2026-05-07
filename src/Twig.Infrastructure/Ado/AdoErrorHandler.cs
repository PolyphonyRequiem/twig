using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Shared error-handling logic for ADO REST API responses.
/// Used by both <see cref="AdoRestClient"/> and <see cref="AdoIterationService"/>.
/// </summary>
internal static partial class AdoErrorHandler
{
    /// <summary>
    /// Inspects the HTTP response status code and throws typed ADO exceptions.
    /// </summary>
    /// <param name="response">The HTTP response to inspect.</param>
    /// <param name="requestUrl">The request URL (used to extract work item ID for 404).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ThrowOnErrorAsync(HttpResponseMessage response, string requestUrl, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;

            // Null (e.g. 204 No Content) or JSON — pass through
            if (mediaType is null || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
                return;

            // Non-JSON Content-Type — read body to decide
            var body = await response.Content.ReadAsStringAsync(ct);

            // Empty body is harmless regardless of Content-Type
            if (string.IsNullOrWhiteSpace(body))
                return;

            // Non-JSON, non-empty body (e.g. HTML auth challenge) — throw
            var snippet = body.Length > 500 ? body[..500] : body;
            throw new AdoUnexpectedResponseException(
                (int)response.StatusCode,
                mediaType,
                requestUrl,
                snippet);
        }

        var statusCode = (int)response.StatusCode;

        switch (response.StatusCode)
        {
            case HttpStatusCode.BadRequest:
                var badMsg = await TryReadErrorMessageAsync(response, ct);
                throw new AdoBadRequestException(badMsg ?? "Bad request.");

            case HttpStatusCode.Unauthorized:
                throw new AdoAuthenticationException();

            case HttpStatusCode.Forbidden:
                // 403 from ADO is most often one of:
                //   1. Wrong-audience access token (token was issued for a different
                //      resource — e.g. management.azure.com — and ADO rejects it).
                //   2. Identity not yet "materialized" in the org (user must do an
                //      interactive web sign-in once before API calls are honored).
                //   3. PAT missing required scopes (e.g. Work Items: Read & Write).
                // We surface this as an auth challenge so the caller invalidates the
                // cached token and the next call re-fetches a fresh one. If the issue
                // is identity materialization or scope, the retry will still 403 and
                // the user sees the guidance below.
                var forbiddenBody = await TryReadErrorMessageAsync(response, ct);
                throw new AdoAuthenticationException(
                    $"Azure DevOps returned HTTP 403 (Forbidden) for {requestUrl}. " +
                    "Common causes: (1) the cached access token has the wrong audience — " +
                    "run 'twig auth status' to verify; " +
                    "(2) your identity has not been materialized in this org — sign in to the org once at https://dev.azure.com/; " +
                    "(3) PAT is missing the required scopes (Work Items: Read & Write). " +
                    $"Server: {forbiddenBody ?? "(no body)"}");

            case HttpStatusCode.NotFound:
                var notFoundId = TryExtractWorkItemIdFromUrl(requestUrl);
                throw new AdoNotFoundException(notFoundId > 0 ? notFoundId : null);

            case HttpStatusCode.Conflict:
                var conflictMsg409 = await TryReadErrorMessageAsync(response, ct);
                throw new AdoDuplicateRelationException(conflictMsg409);

            case HttpStatusCode.PreconditionFailed:
                var conflictBody = await TryReadErrorMessageAsync(response, ct);
                var serverRev = TryParseRevisionFromError(conflictBody);
                throw new AdoConflictException(serverRev, conflictBody);

            case (HttpStatusCode)429:
                var retryAfter = TimeSpan.FromSeconds(10); // default
                if (response.Headers.RetryAfter?.Delta is { } delta)
                    retryAfter = delta;
                throw new AdoRateLimitException(retryAfter);

            default:
                if (statusCode >= 500)
                {
                    var serverMsg = await TryReadErrorMessageAsync(response, ct);
                    throw new AdoServerException(statusCode, serverMsg ?? string.Empty);
                }

                // Unknown status
                var msg = await TryReadErrorMessageAsync(response, ct);
                throw new AdoException($"Unexpected HTTP {statusCode}: {msg}");
        }
    }

    internal static async Task<string?> TryReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            var error = JsonSerializer.Deserialize(body, TwigJsonContext.Default.AdoErrorResponse);
            return error?.Message ?? body;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to extract a work item ID from a URL like
    /// <c>…/_apis/wit/workitems/{id}?…</c>.
    /// Returns 0 if extraction fails.
    /// </summary>
    internal static int TryExtractWorkItemIdFromUrl(string url)
    {
        var match = WorkItemIdRegex().Match(url);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var id))
            return id;
        return 0;
    }

    /// <summary>
    /// Attempts to parse a revision number from an ADO conflict error message.
    /// ADO 412 responses sometimes include revision info. Returns 0 if not parseable.
    /// </summary>
    internal static int TryParseRevisionFromError(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
            return 0;

        var match = RevisionRegex().Match(errorMessage);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var rev))
            return rev;

        return 0;
    }

    /// <summary>
    /// Applies the authentication header to the request.
    /// PAT tokens start with "Basic " and are added raw; all others are treated as Bearer tokens.
    /// </summary>
    internal static void ApplyAuthHeader(HttpRequestMessage request, string token)
    {
        if (token.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("Authorization", token);
        }
        else
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the exception indicates a stale/expired auth token
    /// that may succeed after <see cref="IAuthenticationProvider.InvalidateToken"/> + retry.
    /// Matches 401 responses and 2xx responses with HTML body (ADO sign-in challenge).
    /// </summary>
    internal static bool IsAuthChallenge(Exception ex)
        => ex is AdoAuthenticationException
            or AdoUnexpectedResponseException { StatusCode: 203 };

    [GeneratedRegex(@"/_apis/wit/workitems/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkItemIdRegex();

    [GeneratedRegex(@"revision[:\s]+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RevisionRegex();
}
