using System.Diagnostics;
using System.Text.Json;
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
    ReadTools readTools,
    MutationTools mutationTools,
    NavigationTools navigationTools,
    CreationTools creationTools,
    WorkspaceTools workspaceTools,
    TrackingTools trackingTools,
    AdminTools adminTools,
    ProcessTools processTools,
    SeedTools seedTools) : IToolDispatcher
{
    /// <summary>
    /// Dispatches a single tool call by name, extracting typed parameters from the args dictionary.
    /// </summary>
    /// <param name="toolName">The MCP tool name (e.g. <c>twig_show</c>).</param>
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
        if (!McpToolCatalog.BatchableToolNames.Contains(toolName))
        {
            return Task.FromResult(EnvelopeBuilder.Error(
                McpErrorCode.InvalidInput,
                $"Unknown tool '{toolName}' or tool is not batchable."));
        }

        var workspace = GetString(args, "workspace") ?? workspaceOverride;

        return toolName switch
        {
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
                GetRequiredInt(args, "id"),
                workspace, verbose: false, ct),

            "twig_update" => mutationTools.Update(
                GetRequiredString(args, "field"),
                GetRequiredString(args, "value"),
                GetRequiredInt(args, "id"),
                GetString(args, "format"),
                GetBool(args, "append"),
                workspace, verbose: false, ct),

            "twig_note" => mutationTools.Note(
                GetRequiredString(args, "text"),
                GetRequiredInt(args, "id"),
                workspace,
                GetString(args, "format"),
                verbose: false, ct),

            "twig_patch" => mutationTools.Patch(
                GetRequiredString(args, "fields"),
                GetRequiredInt(args, "id"),
                GetString(args, "format"),
                workspace, verbose: false, ct),

            "twig_delete" => mutationTools.Delete(
                GetRequiredInt(args, "id"),
                GetBool(args, "confirmed"),
                workspace, verbose: false, ct),

            "twig_discard" => mutationTools.Discard(
                GetRequiredInt(args, "id"),
                workspace, verbose: false, ct),

            "twig_sync"=> mutationTools.Sync(workspace, GetBool(args, "pull_only"), verbose: false, ct),

            "twig_refresh" => readTools.Refresh(GetNullableInt(args, "id"), workspace, verbose: false, ct),

            "twig_cache_status" => readTools.CacheStatus(workspace, verbose: false, ct),

            "twig_history" => readTools.History(
                GetRequiredInt(args, "id"),
                GetString(args, "detail"),
                GetString(args, "field"),
                workspace, verbose: false, ct),

            // Creation tools
            "twig_new" => creationTools.New(
                GetRequiredString(args, "type"),
                GetRequiredString(args, "title"),
                GetNullableInt(args, "parentId"),
                GetString(args, "description"),
                GetString(args, "assignedTo"),
                workspace,
                GetBool(args, "skipDuplicateCheck"),
                GetString(args, "format"),
                verbose: false,
                ct),

            "twig_find_or_create" => creationTools.FindOrCreate(
                GetRequiredString(args, "type"),
                GetRequiredString(args, "title"),
                GetRequiredInt(args, "parentId"),
                GetString(args, "description"),
                GetString(args, "assignedTo"),
                workspace,
                GetString(args, "format"),
                verbose: false, ct),

            "twig_link" => creationTools.Link(
                GetRequiredInt(args, "sourceId"),
                GetRequiredInt(args, "targetId"),
                GetRequiredString(args, "linkType"),
                workspace, verbose: false, ct),

            "twig_link_branch" => creationTools.LinkBranch(
                GetRequiredInt(args, "workItemId"),
                GetRequiredString(args, "branchName"),
                workspace, verbose: false, ct),

            "twig_link_artifact" => creationTools.LinkArtifact(
                GetRequiredInt(args, "workItemId"),
                GetRequiredString(args, "url"),
                GetString(args, "name"),
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

            "twig_verify_descendants" => navigationTools.VerifyDescendants(
                GetRequiredInt(args, "id"),
                GetInt(args, "maxDepth", defaultValue: 2),
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

            // Process tools
            "twig_process" => processTools.Process(
                GetString(args, "type"),
                workspace, verbose: false, ct),

            // 🔴 Type selection only (AB#241). There is deliberately no argument here naming
            // which PARTS of a type to describe — per-part selection is forbidden, so a batch
            // caller cannot reach one either.
            "twig_process_description" => processTools.ProcessDescription(
                GetStringArray(args, "types"),
                workspace, verbose: false, ct),

            // Seed tools
            "twig_seed_new" => seedTools.SeedNew(
                GetRequiredString(args, "title"),
                GetString(args, "type"),
                GetNullableInt(args, "parentId"),
                GetString(args, "description"),
                GetString(args, "assignedTo"),
                workspace,
                GetString(args, "format"),
                verbose: false, ct),

            "twig_seed_view" => seedTools.SeedView(workspace, verbose: false, ct),

            "twig_seed_publish" => seedTools.SeedPublish(
                GetNullableInt(args, "id"),
                GetBool(args, "all"),
                GetBool(args, "force"),
                GetBool(args, "dryRun"),
                workspace, verbose: false, ct),

            "twig_seed_validate" => seedTools.SeedValidate(
                GetNullableInt(args, "id"),
                GetBool(args, "all"),
                workspace, verbose: false, ct),

            "twig_seed_discard" => seedTools.SeedDiscard(
                GetRequiredInt(args, "id"),
                workspace, verbose: false, ct),

            "twig_seed_chain" => seedTools.SeedChain(
                GetRequiredInt(args, "parentId"),
                GetRequiredStringArray(args, "titles"),
                GetString(args, "type"),
                GetString(args, "assignedTo"),
                workspace, verbose: false, ct),

            "twig_seed_reconcile" => seedTools.SeedReconcile(
                workspace, verbose: false, ct),

            "twig_seed_edit" => seedTools.SeedEdit(
                GetRequiredInt(args, "id"),
                GetString(args, "title"),
                GetString(args, "description"),
                GetString(args, "type"),
                GetNullableInt(args, "parentId"),
                workspace,
                GetString(args, "format"),
                verbose: false, ct),

            "twig_seed_link" => seedTools.SeedLink(
                GetRequiredInt(args, "sourceId"),
                GetRequiredInt(args, "targetId"),
                GetString(args, "type"),
                workspace, verbose: false, ct),

            _ => throw new UnreachableException(
                $"Cataloged tool '{toolName}' has no batch dispatcher case.")
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

    internal static string[] GetRequiredStringArray(
        IReadOnlyDictionary<string, object?> args,
        string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            throw new ArgumentException($"Required argument '{key}' is missing.");

        if (value is string[] values) return values;
        if (value is IEnumerable<string> strings) return strings.ToArray();

        if (value is string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return document.RootElement.EnumerateArray()
                        .Select(element => element.ValueKind == JsonValueKind.String
                            ? element.GetString()!
                            : throw new ArgumentException(
                                $"Argument '{key}' must contain only strings."))
                        .ToArray();
                }
            }
            catch (JsonException)
            {
                // Fall through to the consistent argument error below.
            }
        }

        throw new ArgumentException($"Argument '{key}' must be an array of strings.");
    }

    /// <summary>
    /// Reads an OPTIONAL array-of-strings argument, returning <see langword="null"/> when it is
    /// absent.
    /// </summary>
    /// <remarks>
    /// 🔴 Delegates to <see cref="GetRequiredStringArray"/> for the parsing rather than
    /// reimplementing it, so the two cannot come to accept different inputs — the batch surface
    /// and the direct tool call must agree on what an argument means. Absence is the ONLY thing
    /// handled here; a present-but-malformed value still raises the same error the required
    /// variant does, because "you sent something I could not read" and "you sent nothing" are
    /// different facts and quietly turning the first into the second would describe the whole
    /// process when the caller asked for two types.
    /// </remarks>
    internal static string[]? GetStringArray(
        IReadOnlyDictionary<string, object?> args,
        string key)
        => !args.TryGetValue(key, out var value) || value is null
            ? null
            : GetRequiredStringArray(args, key);
}
