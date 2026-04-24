using ModelContextProtocol.Protocol;
using Twig.Mcp.Services.Batch;

namespace Twig.Mcp.Tests.Services.Batch;

/// <summary>
/// Shared test dispatcher and factory helpers for batch engine and batch tools tests.
/// </summary>
internal sealed class TestToolDispatcher(
    Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<CallToolResult>> handler)
    : IToolDispatcher
{
    public string? LastWorkspaceOverride { get; private set; }

    public Task<CallToolResult> DispatchAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        string? workspaceOverride,
        CancellationToken ct)
    {
        LastWorkspaceOverride = workspaceOverride;
        return handler(toolName, args, ct);
    }
}

internal static class BatchTestHelpers
{
    public static TestToolDispatcher CreateDispatcher(
        Func<string, IReadOnlyDictionary<string, object?>, CallToolResult>? handler = null) =>
        new((tool, args, _) =>
            Task.FromResult(handler is not null
                ? handler(tool, args)
                : SuccessResult($"{{\"tool\":\"{tool}\",\"ok\":true}}")));

    public static TestToolDispatcher CreateDelayedDispatcher(TimeSpan delay) =>
        new(async (tool, _, ct) =>
        {
            await Task.Delay(delay, ct);
            return SuccessResult($"{{\"tool\":\"{tool}\",\"ok\":true}}");
        });

    public static TestToolDispatcher CreateThrowingDispatcher(Exception ex) =>
        new((_, _, _) => throw ex);

    public static CallToolResult SuccessResult(string json) =>
        new() { Content = [new TextContentBlock { Text = json }] };

    public static CallToolResult ErrorResult(string message) =>
        new() { Content = [new TextContentBlock { Text = message }], IsError = true };
}
