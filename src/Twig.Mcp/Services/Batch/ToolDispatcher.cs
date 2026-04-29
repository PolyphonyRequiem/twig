using ModelContextProtocol.Protocol;
using Twig.Mcp.Tools;

namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Routes a tool name + args dictionary to the corresponding MCP tool method.
/// Uses an AOT-safe switch expression over known tool names — no reflection.
/// The <paramref name="workspaceOverride"/> from the batch-level <c>workspace</c>
/// parameter is injected into each call unless the step has its own <c>workspace</c> arg.
/// </summary>
internal sealed class ToolDispatcher(
    ContextTools contextTools,
    ReadTools readTools,
    MutationTools mutationTools,
    NavigationTools navigationTools,
    CreationTools creationTools,
    WorkspaceTools workspaceTools,
    TrackingTools trackingTools,
    AdminTools adminTools) : IToolDispatcher
{
    /// <summary>
    /// Dispatches a single tool call by name, extracting typed parameters from the args dictionary.
    /// </summary>
    /// <param name="toolName">The MCP tool name (e.g. <c>twig_set</c>).</param>
    /// <param name="args">Argument dictionary with scalar values parsed from JSON.</param>
    /// <param name="workspaceOverride">Batch-level workspace override; used when the step has no explicit <c>workspace</c> arg.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="CallToolResult"/> from the invoked tool method.</returns>
    public Task<CallToolResult> DispatchAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        string? workspaceOverride,
        CancellationToken ct)
    {
        var workspace = GetString(args, "workspace") ?? workspaceOverride;

        return toolName switch
        {
            // Context tools
            "twig_set" => contextTools.Set(
                GetRequiredString(args, "idOrPattern"),
                workspace, verbose: false, ct),

            // Read tools
            "twig_tree" => readTools.Tree(
                GetNullableInt(args, "id"),
                GetNullableInt(args, "depth"),
                workspace, verbose: false, ct),

            "twig_workspace" => readTools.Workspace(
                GetBool(args, "all"),
                GetBool(args, "tree"),
                workspace, verbose: false, ct),

            // Mutation tools
            "twig_state" => mutationTools.State(
                GetRequiredString(args, "stateName"),
                GetNullableInt(args, "id"),
                workspace, verbose: false, ct),

            "twig_update" => mutationTools.Update(
                GetRequiredString(args, "field"),
                GetRequiredString(args, "value"),
                GetString(args, "format"),
                GetBool(args, "append"),
                GetNullableInt(args, "id"),
                workspace, verbose: false, ct),

            "twig_note" => mutationTools.Note(
                GetRequiredString(args, "text"),
                GetNullableInt(args, "id"),
                workspace, verbose: false, ct),

            "twig_sync"=> mutationTools.Sync(workspace, GetBool(args, "pull_only"), verbose: false, ct),

            "twig_refresh" => readTools.Refresh(GetNullableInt(args, "id"), workspace, verbose: false, ct),

            "twig_cache_status" => readTools.CacheStatus(workspace, verbose: false, ct),

            // Creation tools
            "twig_new" => creationTools.New(
                GetRequiredString(args, "type"),
                GetRequiredString(args, "title"),
                GetNullableInt(args, "parentId"),
                GetString(args, "description"),
                GetString(args, "assignedTo"),
                workspace,
                GetBool(args, "skipDuplicateCheck"),
                verbose: false,
                ct),

            "twig_find_or_create" => creationTools.FindOrCreate(
                GetRequiredString(args, "type"),
                GetRequiredString(args, "title"),
                GetRequiredInt(args, "parentId"),
                GetString(args, "description"),
                GetString(args, "assignedTo"),
                workspace, verbose: false, ct),

            "twig_link" => creationTools.Link(
                GetRequiredInt(args, "sourceId"),
                GetRequiredInt(args, "targetId"),
                GetRequiredString(args, "linkType"),
                workspace, verbose: false, ct),

            // Navigation tools
            "twig_show" => navigationTools.Show(
                GetRequiredInt(args, "id"),
                GetBool(args, "tree"),
                GetNullableInt(args, "depth"),
                workspace, verbose: false, ct),

            "twig_query" => navigationTools.Query(
                searchText: GetString(args, "searchText"),
                type: GetString(args, "type"),
                state: GetString(args, "state"),
                title: GetString(args, "title"),
                assignedTo: GetString(args, "assignedTo"),
                areaPath: GetString(args, "areaPath"),
                iterationPath: GetString(args, "iterationPath"),
                createdSince: GetNullableInt(args, "createdSince"),
                changedSince: GetNullableInt(args, "changedSince"),
                top: GetInt(args, "top", defaultValue: 25),
                workspace: workspace, verbose: false, ct: ct),

            "twig_children" => navigationTools.Children(
                GetRequiredInt(args, "id"),
                workspace, verbose: false, ct),

            "twig_parent" => navigationTools.Parent(
                GetRequiredInt(args, "id"),
                workspace, verbose: false, ct),

            "twig_sprint" => navigationTools.Sprint(
                GetBool(args, "items"),
                workspace, verbose: false, ct),

            // Workspace tools
            "twig_list_workspaces" => workspaceTools.ListWorkspaces(verbose: false, ct),

            // Tracking tools
            "twig_track" => trackingTools.Track(
                GetRequiredString(args, "id"),
                GetBool(args, "recursive"),
                workspace, verbose: false, ct),

            "twig_untrack" => trackingTools.Untrack(
                GetRequiredString(args, "id"),
                workspace, verbose: false, ct),

            "twig_tracking_status" => trackingTools.TrackingStatus(
                workspace, verbose: false, ct),

            // Admin tools
            "twig_config" => adminTools.Config(GetString(args, "key"), workspace, verbose: false, ct),

            "twig_area" => adminTools.Area(workspace, verbose: false, ct),

            _ => Task.FromResult(EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"Unknown tool '{toolName}'."))
        };
    }

    // ── Arg extraction helpers ──────────────────────────────────────

    internal static string GetRequiredString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            throw new ArgumentException($"Required argument '{key}' is missing.");

        return value.ToString()!;
    }

    internal static string? GetString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value.ToString();
    }

    internal static bool GetBool(IReadOnlyDictionary<string, object?> args, string key, bool defaultValue = false)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return defaultValue;

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var b) => b,
            // JSON numbers: 0 = false, non-zero = true
            int i => i != 0,
            long l => l != 0,
            _ => defaultValue
        };
    }

    internal static int GetInt(IReadOnlyDictionary<string, object?> args, string key, int defaultValue = 0)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return defaultValue;

        return value switch
        {
            int i => i,
            long l => checked((int)l),
            string s when int.TryParse(s, out var i) => i,
            _ => defaultValue
        };
    }

    internal static int GetRequiredInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            throw new ArgumentException($"Required argument '{key}' is missing.");

        return value switch
        {
            int i => i,
            long l => checked((int)l),
            string s when int.TryParse(s, out var i) => i,
            _ => throw new ArgumentException($"Argument '{key}' must be an integer, got '{value}'.")
        };
    }

    internal static int? GetNullableInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int i => i,
            long l => checked((int)l),
            string s when int.TryParse(s, out var i) => i,
            _ => null
        };
    }
}