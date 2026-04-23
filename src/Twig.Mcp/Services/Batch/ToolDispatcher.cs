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
    WorkspaceTools workspaceTools) : IToolDispatcher
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
        var workspace = GetStringOrDefault(args, "workspace", workspaceOverride);

        return toolName switch
        {
            // Context tools
            "twig_set" => contextTools.Set(
                GetRequiredString(args, "idOrPattern"),
                workspace, ct),

            "twig_status" => contextTools.Status(workspace, ct),

            // Read tools
            "twig_tree" => readTools.Tree(
                GetNullableInt(args, "depth"),
                workspace, ct),

            "twig_workspace" => readTools.Workspace(
                GetBool(args, "all"),
                workspace, ct),

            // Mutation tools
            "twig_state" => mutationTools.State(
                GetRequiredString(args, "stateName"),
                GetBool(args, "force"),
                workspace, ct),

            "twig_update" => mutationTools.Update(
                GetRequiredString(args, "field"),
                GetRequiredString(args, "value"),
                GetString(args, "format"),
                workspace, ct),

            "twig_note" => mutationTools.Note(
                GetRequiredString(args, "text"),
                workspace, ct),

            "twig_discard" => mutationTools.Discard(
                GetNullableInt(args, "id"),
                workspace, ct),

            "twig_sync" => mutationTools.Sync(workspace, ct),

            // Creation tools
            "twig_new" => creationTools.New(
                GetRequiredString(args, "type"),
                GetRequiredString(args, "title"),
                GetNullableInt(args, "parentId"),
                GetString(args, "description"),
                GetString(args, "assignedTo"),
                workspace,
                GetBool(args, "skipDuplicateCheck"),
                ct),

            "twig_find_or_create" => creationTools.FindOrCreate(
                GetRequiredString(args, "type"),
                GetRequiredString(args, "title"),
                GetRequiredInt(args, "parentId"),
                GetString(args, "description"),
                GetString(args, "assignedTo"),
                workspace, ct),

            "twig_link" => creationTools.Link(
                GetRequiredInt(args, "sourceId"),
                GetRequiredInt(args, "targetId"),
                GetRequiredString(args, "linkType"),
                workspace, ct),

            // Navigation tools
            "twig_show" => navigationTools.Show(
                GetRequiredInt(args, "id"),
                workspace, ct),

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
                workspace: workspace, ct: ct),

            "twig_children" => navigationTools.Children(
                GetRequiredInt(args, "id"),
                workspace, ct),

            "twig_parent" => navigationTools.Parent(
                GetRequiredInt(args, "id"),
                workspace, ct),

            "twig_sprint" => navigationTools.Sprint(
                GetBool(args, "items"),
                workspace, ct),

            // Workspace tools
            "twig_list_workspaces" => Task.FromResult(workspaceTools.ListWorkspaces()),

            _ => Task.FromResult(McpResultBuilder.ToError($"Unknown tool '{toolName}'."))
        };
    }

    /// <summary>
    /// Returns the set of tool names recognized by this dispatcher.
    /// </summary>
    internal static IReadOnlySet<string> KnownToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "twig_set", "twig_status",
        "twig_tree", "twig_workspace",
        "twig_state", "twig_update", "twig_note", "twig_discard", "twig_sync",
        "twig_new", "twig_find_or_create", "twig_link",
        "twig_show", "twig_query", "twig_children", "twig_parent", "twig_sprint",
        "twig_list_workspaces"
    };

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

    private static string? GetStringOrDefault(IReadOnlyDictionary<string, object?> args, string key, string? fallback)
    {
        var value = GetString(args, key);
        return value ?? fallback;
    }
}
