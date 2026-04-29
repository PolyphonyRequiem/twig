namespace Twig.Mcp.Services;

/// <summary>
/// Well-known error codes for MCP tool responses.
/// Tools map exceptions to these codes; unhandled exceptions get <see cref="InternalError"/>.
/// </summary>
public static class McpErrorCode
{
    /// <summary>Work item not found in cache or ADO.</summary>
    public const string ItemNotFound = "ITEM_NOT_FOUND";

    /// <summary>Caller provided invalid input (missing field, bad format, etc.).</summary>
    public const string InvalidInput = "INVALID_INPUT";

    /// <summary>No active work item set — caller must use <c>twig_set</c> first.</summary>
    public const string NoContext = "NO_CONTEXT";

    /// <summary>ADO API is unreachable (network error, auth failure, timeout).</summary>
    public const string AdoUnreachable = "ADO_UNREACHABLE";

    /// <summary>The local cache is stale and a sync is required.</summary>
    public const string CacheStale = "CACHE_STALE";

    /// <summary>The requested state transition is not valid.</summary>
    public const string InvalidStateTransition = "INVALID_STATE_TRANSITION";

    /// <summary>The workspace could not be resolved (ambiguous, unknown, etc.).</summary>
    public const string WorkspaceNotFound = "WORKSPACE_NOT_FOUND";

    /// <summary>An unexpected internal error occurred.</summary>
    public const string InternalError = "INTERNAL_ERROR";

    /// <summary>The caller lacks permission for the requested operation.</summary>
    public const string PermissionDenied = "PERMISSION_DENIED";

    /// <summary>The operation requires confirmation that was not provided.</summary>
    public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";
}
