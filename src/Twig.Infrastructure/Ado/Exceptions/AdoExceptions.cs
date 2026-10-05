namespace Twig.Infrastructure.Ado.Exceptions;

/// <summary>
/// Base exception for ADO REST API errors.
/// </summary>
public class AdoException : Exception
{
    public AdoException(string message) : base(message) { }
    public AdoException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Network unreachable — ADO cannot be contacted (FM-001).</summary>
public sealed class AdoOfflineException : AdoException
{
    public AdoOfflineException(Exception inner)
        : base("ADO unreachable. Operating in offline mode.", inner) { }
}

/// <summary>400 — Bad request from ADO REST API.</summary>
public sealed class AdoBadRequestException : AdoException
{
    public AdoBadRequestException(string message) : base(message) { }
}

/// <summary>401 — Authentication failure.</summary>
public sealed class AdoAuthenticationException : AdoException
{
    public AdoAuthenticationException()
        : base("Authentication failed. Check your credentials or run 'az login'.") { }

    public AdoAuthenticationException(string message) : base(message) { }
}

/// <summary>404 — Work item not found.</summary>
public sealed class AdoNotFoundException : AdoException
{
    /// <summary>
    /// The work item ID that was not found, or null if the 404 was for a non-work-item resource.
    /// </summary>
    public int? WorkItemId { get; }

    public AdoNotFoundException(int? id)
        : base(id is > 0 ? $"Work item {id} not found." : "Resource not found.")
    {
        WorkItemId = id is > 0 ? id : null;
    }
}

/// <summary>412 — Optimistic concurrency conflict.</summary>
public sealed class AdoConflictException : AdoException
{
    public int ServerRevision { get; }

    public AdoConflictException(int serverRev, string? detail = null)
        : base(string.IsNullOrEmpty(detail)
            ? $"Concurrency conflict. Server revision: {serverRev}."
            : $"Concurrency conflict. Server revision: {serverRev}. {detail}")
    {
        ServerRevision = serverRev;
    }
}

/// <summary>429 — Rate limited.</summary>
public sealed class AdoRateLimitException : AdoException
{
    public TimeSpan RetryAfter { get; }
    public string? ServerMessage { get; }
    public string? Resource { get; }
    public string? RetryAfterHeader { get; }
    public string? Authority { get; }
    public string? Principal { get; }
    public string? CorrelationId { get; }
    public string? ActivityId { get; }

    public AdoRateLimitException(TimeSpan retryAfter)
        : this(retryAfter, null, null, null, null, null, null, null) { }

    internal AdoRateLimitException(TimeSpan retryAfter, string? serverMessage, string? resource,
        string? retryAfterHeader, string? authority, string? principal, string? correlationId, string? activityId)
        : base($"Rate limited (HTTP 429). Retry after {retryAfter.TotalSeconds:F0}s."
            + (serverMessage is null ? string.Empty : $" Server: {serverMessage}")
            + (resource is null ? string.Empty : $" Resource: {resource}.")
            + (authority is null ? string.Empty : $" Authority: {authority}.")
            + (principal is null ? string.Empty : $" Bound principal: {principal}.")
            + (retryAfterHeader is null ? string.Empty : $" Retry-After: {retryAfterHeader}.")
            + (correlationId is null ? string.Empty : $" Correlation: {correlationId}.")
            + (activityId is null ? string.Empty : $" Activity: {activityId}."))
    {
        RetryAfter = retryAfter;
        ServerMessage = serverMessage;
        Resource = resource;
        RetryAfterHeader = retryAfterHeader;
        Authority = authority;
        Principal = principal;
        CorrelationId = correlationId;
        ActivityId = activityId;
    }
}

/// <summary>2xx with non-JSON Content-Type — unexpected response body (e.g. HTML auth challenge).</summary>
public sealed class AdoUnexpectedResponseException : AdoException
{
    public int StatusCode { get; }
    public string ContentType { get; }
    public string RequestUrl { get; }
    public string BodySnippet { get; }

    public AdoUnexpectedResponseException(int statusCode, string contentType, string requestUrl, string bodySnippet)
        : base($"ADO returned non-JSON response (HTTP {statusCode}, Content-Type: {contentType}). URL: {requestUrl}. Body: {bodySnippet}")
    {
        StatusCode = statusCode;
        ContentType = contentType;
        RequestUrl = requestUrl;
        BodySnippet = bodySnippet;
    }
}

/// <summary>409 — Duplicate relation already exists on the work item.</summary>
public sealed class AdoDuplicateRelationException : AdoException
{
    public AdoDuplicateRelationException(string? detail = null)
        : base(detail ?? "A relation with the same target already exists.") { }
}

/// <summary>
/// Strict-CAS relation not found — the item's relations set did not contain the exact
/// (relation type, target) the caller asked to remove at the expected revision.
/// Determinate: no retry or readback will change the answer, because the plan named the
/// exact edge and the fetched relations at that revision showed no such entry.
/// </summary>
public sealed class AdoRelationNotFoundException : AdoException
{
    public int SourceId { get; }
    public string RelationType { get; }
    public int TargetId { get; }
    public int ExpectedRevision { get; }

    public AdoRelationNotFoundException(int sourceId, string relationType, int targetId, int expectedRevision)
        : base($"Relation '{relationType}' -> {targetId} not present on work item #{sourceId} at revision {expectedRevision}.")
    {
        SourceId = sourceId;
        RelationType = relationType;
        TargetId = targetId;
        ExpectedRevision = expectedRevision;
    }
}

/// <summary>5xx — Transient server error.</summary>
public sealed class AdoServerException : AdoException
{
    public int StatusCode { get; }

    public AdoServerException(int statusCode)
        : base($"ADO server error: HTTP {statusCode}.")
    {
        StatusCode = statusCode;
    }

    public AdoServerException(int statusCode, string message)
        : base($"ADO server error: HTTP {statusCode}. {message}")
    {
        StatusCode = statusCode;
    }
}
