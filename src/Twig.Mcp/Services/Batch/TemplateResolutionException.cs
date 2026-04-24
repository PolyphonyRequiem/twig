namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Thrown when a <c>{{steps.N.path}}</c> template expression cannot be resolved
/// at batch execution time. Carries the offending placeholder for diagnostic use.
/// </summary>
internal sealed class TemplateResolutionException(string placeholder, string reason)
    : InvalidOperationException($"Template resolution failed for '{placeholder}': {reason}")
{
    public string Placeholder { get; } = placeholder;
}
