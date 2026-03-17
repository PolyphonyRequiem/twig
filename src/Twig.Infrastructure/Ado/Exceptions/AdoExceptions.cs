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

    public AdoRateLimitException(TimeSpan retryAfter)
        : base($"Rate limited. Retry after {retryAfter.TotalSeconds:F0}s.")
    {
        RetryAfter = retryAfter;
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
