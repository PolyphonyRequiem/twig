namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Safety limit constants for batch execution.
/// </summary>
internal static class BatchConstants
{
    /// <summary>Maximum nesting depth for batch graph nodes.</summary>
    public const int MaxDepth = 3;

    /// <summary>Maximum total step nodes across the entire batch graph.</summary>
    public const int MaxOperations = 50;

    /// <summary>Default per-batch timeout in seconds.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>Tool name banned from batch step nodes (recursive batch prevention).</summary>
    public const string BatchToolName = "twig_batch";
}
