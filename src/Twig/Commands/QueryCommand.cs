using System.Diagnostics;
using System.Text.RegularExpressions;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig query</c>: ad-hoc work item search and filtering via WIQL.
/// Builds a WIQL query from CLI flags, executes against ADO, caches results locally,
/// and renders output in the requested format (human table, JSON, or bare IDs).
/// </summary>
public sealed partial class QueryCommand(
    IAdoWorkItemService adoService,
    IWorkItemRepository workItemRepo,
    TwigConfiguration config,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    ITelemetryClient? telemetryClient = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>Execute the query command.</summary>
    public async Task<int> ExecuteAsync(
        string? searchText = null,
        string? type = null,
        string? state = null,
        string? assignedTo = null,
        string? areaPath = null,
        string? iterationPath = null,
        string? createdSince = null,
        string? changedSince = null,
        int top = 25,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var (exitCode, resultCount) = await ExecuteCoreAsync(
            searchText, type, state, assignedTo, areaPath, iterationPath,
            createdSince, changedSince, top, outputFormat, ct);

        telemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
        {
            ["command"] = "query",
            ["exit_code"] = exitCode.ToString(),
            ["output_format"] = outputFormat,
            ["twig_version"] = VersionHelper.GetVersion(),
            ["os_platform"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription
        }, new Dictionary<string, double>
        {
            ["duration_ms"] = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            ["result_count"] = resultCount
        });

        return exitCode;
    }

    private async Task<(int ExitCode, int ResultCount)> ExecuteCoreAsync(
        string? searchText,
        string? type,
        string? state,
        string? assignedTo,
        string? areaPath,
        string? iterationPath,
        string? createdSince,
        string? changedSince,
        int top,
        string outputFormat,
        CancellationToken ct)
    {
        // 1. Parse time filters — return exit code 1 on invalid input (FR-20)
        if (!TryParseDuration(createdSince, out var createdSinceDays)) return (1, 0);
        if (!TryParseDuration(changedSince, out var changedSinceDays)) return (1, 0);

        // 2. Build QueryParameters from CLI args + config defaults (FR-17)
        IReadOnlyList<(string Path, bool IncludeChildren)>? defaultAreaPaths = null;
        if (string.IsNullOrWhiteSpace(areaPath))
        {
            defaultAreaPaths = ResolveDefaultAreaPaths();
        }

        var parameters = new QueryParameters
        {
            SearchText = searchText,
            TypeFilter = type,
            StateFilter = state,
            AssignedToFilter = assignedTo,
            AreaPathFilter = areaPath,
            IterationPathFilter = iterationPath,
            CreatedSinceDays = createdSinceDays,
            ChangedSinceDays = changedSinceDays,
            Top = top,
            DefaultAreaPaths = defaultAreaPaths
        };

        // 3. Generate WIQL
        var wiql = WiqlQueryBuilder.Build(parameters);

        // 4. Execute WIQL with $top (DD-01)
        var ids = await adoService.QueryByWiqlAsync(wiql, top, ct);

        // 5. Fetch full work items
        var items = ids.Count > 0
            ? await adoService.FetchBatchAsync(ids, ct)
            : Array.Empty<WorkItem>();

        // 6. Cache results (FR-16)
        if (items.Count > 0)
            await workItemRepo.SaveBatchAsync(items, ct);

        // 7. Build result model (DD-09: truncation heuristic)
        var queryDescription = BuildQueryDescription(parameters);
        var result = new QueryResult(items, items.Count >= top, queryDescription);

        // 8. Branch on output format (DD-04, FR-19)
        if (string.Equals(outputFormat, "ids", StringComparison.OrdinalIgnoreCase))
        {
            // IDs output: one per line, skip formatter and hints entirely
            if (items.Count > 0)
                Console.WriteLine(string.Join("\n", items.Select(i => i.Id)));
            return (0, items.Count);
        }

        // Non-IDs output: resolve formatter and render
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (items.Count == 0)
        {
            Console.WriteLine(fmt.FormatInfo("No items found."));
        }
        else
        {
            var output = fmt.FormatQueryResults(result);
            if (!string.IsNullOrEmpty(output))
                Console.WriteLine(output);
        }

        // 9. Emit hints (FR-19: suppressed for json/minimal by HintEngine)
        var hints = hintEngine.GetHints("query", outputFormat: outputFormat);
        foreach (var hint in hints)
            Console.WriteLine(fmt.FormatHint(hint));

        return (0, items.Count);
    }

    /// <summary>
    /// Resolves default area paths from config, matching RefreshCommand's pattern (L77–105).
    /// Maps <see cref="AreaPathEntry"/> values into (Path, IncludeChildren) tuples.
    /// </summary>
    private IReadOnlyList<(string Path, bool IncludeChildren)>? ResolveDefaultAreaPaths()
    {
        var entries = config.Defaults?.AreaPathEntries;
        if (entries is { Count: > 0 })
            return entries.Select(e => (e.Path, e.IncludeChildren)).ToList();

        var areaPaths = config.Defaults?.AreaPaths;
        if (areaPaths is null || areaPaths.Count == 0)
        {
            var singlePath = config.Defaults?.AreaPath;
            if (!string.IsNullOrWhiteSpace(singlePath))
                areaPaths = [singlePath];
        }

        if (areaPaths is { Count: > 0 })
            return areaPaths.Select(p => (p, true)).ToList();

        return null;
    }

    private bool TryParseDuration(string? input, out int? days)
    {
        days = null;
        if (input is null) return true;
        var match = DurationPattern().Match(input);
        if (match.Success)
        {
            var n = int.Parse(match.Groups[1].Value);
            days = match.Groups[2].Value switch { "d" => n, "w" => n * 7, "m" => n * 30, _ => null };
            if (days is not null) return true;
        }
        _stderr.WriteLine($"error: Invalid duration '{input}'. Use format: Nd, Nw, Nm (e.g., 7d, 2w, 1m).");
        return false;
    }

    [GeneratedRegex(@"^(\d+)([dwm])$", RegexOptions.None)]
    private static partial Regex DurationPattern();

    /// <summary>
    /// Builds a human-readable description of the active query filters
    /// (e.g. "title contains 'keyword' AND state = 'Doing'").
    /// Used as the <c>query</c> field in JSON output.
    /// </summary>
    private static string BuildQueryDescription(QueryParameters parameters)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(parameters.SearchText))
            parts.Add($"title contains '{parameters.SearchText}'");
        if (!string.IsNullOrEmpty(parameters.TypeFilter))
            parts.Add($"type = '{parameters.TypeFilter}'");
        if (!string.IsNullOrEmpty(parameters.StateFilter))
            parts.Add($"state = '{parameters.StateFilter}'");
        if (!string.IsNullOrEmpty(parameters.AssignedToFilter))
            parts.Add($"assignedTo = '{parameters.AssignedToFilter}'");
        if (!string.IsNullOrEmpty(parameters.AreaPathFilter))
            parts.Add($"areaPath under '{parameters.AreaPathFilter}'");
        if (!string.IsNullOrEmpty(parameters.IterationPathFilter))
            parts.Add($"iterationPath under '{parameters.IterationPathFilter}'");
        if (parameters.CreatedSinceDays.HasValue)
            parts.Add($"created within {parameters.CreatedSinceDays.Value}d");
        if (parameters.ChangedSinceDays.HasValue)
            parts.Add($"changed within {parameters.ChangedSinceDays.Value}d");

        return parts.Count > 0 ? string.Join(" AND ", parts) : "all items";
    }
}
