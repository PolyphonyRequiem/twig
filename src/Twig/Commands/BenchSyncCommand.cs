using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Globalization;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>Pulls only the current Bench's selectors and permitted context relationships.</summary>
internal sealed class BenchSyncCommand(
    CommandContext ctx,
    CurrentBenchResolver currentBench,
    BenchEvaluator evaluator,
    IIterationCalendar calendar,
    IIterationService iterations,
    IAdoWorkItemService ado,
    IWorkItemRepository repository,
    ProtectedCacheWriter writer,
    IWorkItemLinkRepository links,
    IAuthenticationProvider authenticationProvider,
    IContextStore context,
    IConnectionBindingService bindings,
    TwigPaths paths,
    RendererFactory rendererFactory)
{
    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat,
        string? expectBench = null, string? expectBinding = null, string? expectIdentity = null,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var fmt = ctx.FormatterFactory.GetFormatter(outputFormat);
        try
        {
            using var admission = await ConnectionOperationAdmission.AcquireAsync(authenticationProvider, ct);
            if (admission is null)
                throw new InvalidOperationException("Native Bench sync requires an admitted connection; reconnect before syncing.");
            using var operation = repository.AcquireOperation();
            var binding = await bindings.ResolveAsync(ctx.Config, paths, ct);
            BrowserOriginGuard.EnsureExpected(binding, expectBinding, expectIdentity);
            var bench = await currentBench.ResolveAsync(ct, expectBench);

            var queries = bench.Selectors.Where(s => s.Kind == SelectorKind.Query).ToArray();
            IReadOnlyList<Twig.Domain.ValueObjects.IterationPath> currentIterations = [];
            if (queries.Length > 0)
            {
                var teamIterations = await iterations.GetTeamIterationsAsync(ct);
                await calendar.SaveAsync(teamIterations, ct);
                currentIterations = await calendar.GetCurrentIterationsAsync(ct);
            }
            var membership = await evaluator.EvaluateAsync(bench, currentIterations, ct);
            var ids = new HashSet<int>(membership.AllIds.Where(id => id > 0));
            foreach (var selector in queries)
            {
                if (selector.QueryRule != Twig.Domain.ValueObjects.BenchSelector.CurrentSprintRule)
                    throw new InvalidOperationException($"Unsupported Bench refresh rule '{selector.QueryRule}'.");
                // No current iteration means an empty rule, never an unbounded query.
                if (currentIterations.Count == 0)
                    continue;
                var iterationFilter = string.Join(" OR ", currentIterations.Select(p =>
                    $"[System.IterationPath] = '{Escape(p.Value)}'"));
                var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{Escape(ctx.Config.Project)}' AND ({iterationFilter})";
                var assignee = selector.QueryAssignedToUniqueName ?? selector.QueryAssignedTo;
                if (assignee is not null)
                    wiql += $" AND [System.AssignedTo] = '{Escape(assignee)}'";
                foreach (var id in await ado.QueryByWiqlAsync(wiql, ct))
                    if (id > 0) ids.Add(id);
            }
            var memberIds = new HashSet<int>(ids);
            var active = await context.GetActiveWorkItemIdAsync(ct);
            if (active is > 0) ids.Add(active.Value);

            var fetched = new Dictionary<int, WorkItem>();
            var protectedCount = 0;
            await FetchAsync(ids);
            var subtreeRoots = bench.Selectors.Where(s => s.Kind == SelectorKind.Subtree)
                .Select(s => s.AsWorkItemId()).Where(id => id > 0).Distinct().ToArray();
            await ExpandChildrenAsync(subtreeRoots, int.MaxValue, markAsMembers: true);
            if (active is > 0)
                await ExpandChildrenAsync([active.Value], Math.Max(0, ctx.Config.Display.TreeDepthDown));
            var relationshipRoots = fetched.Keys.ToArray();
            var parents = relationshipRoots.AsEnumerable();
            for (var depth = 0; depth < Math.Max(0, ctx.Config.Display.TreeDepthUp); depth++)
            {
                var next = parents.Select(id => fetched[id].ParentId).Where(id => id is > 0)
                    .Select(id => id!.Value).Distinct().ToArray();
                if (next.Length == 0) break;
                await FetchAsync(next);
                parents = next;
            }
            if (active is > 0 && ctx.Config.Display.TreeDepthSideways > 0
                && fetched.TryGetValue(active.Value, out var activeItem) && activeItem.ParentId is > 0)
            {
                await FetchAsync([activeItem.ParentId.Value]);
                await ExpandChildrenAsync([activeItem.ParentId.Value], ctx.Config.Display.TreeDepthSideways);
            }
            var memberCount = fetched.Keys.Count(memberIds.Contains);

            rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree([
                new RenderNode.Record("benchSync", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["kind"] = RenderCell.String("benchSync"),
                    ["benchId"] = RenderCell.String(bench.Id.ToString(CultureInfo.InvariantCulture)),
                    ["benchName"] = RenderCell.String(bench.Name),
                    ["itemCount"] = RenderCell.Integer(fetched.Count),
                    ["memberCount"] = RenderCell.Integer(memberCount),
                    ["relationshipCount"] = RenderCell.Integer(fetched.Count - memberCount),
                    ["protectedCount"] = RenderCell.Integer(protectedCount),
                    ["message"] = RenderCell.String($"Synced Bench '{bench.Name}': {fetched.Count} item(s), {protectedCount} protected; pull only."),
                })]));
            ReportTelemetry(0, fetched.Count);
            return 0;

            async Task FetchAsync(IEnumerable<int> requested)
            {
                var missing = requested.Where(id => id > 0 && !fetched.ContainsKey(id)).Distinct().ToArray();
                if (missing.Length == 0) return;
                var (items, edges) = await ado.FetchBatchWithLinksAsync(missing, ct);
                var requestedIds = missing.ToHashSet();
                if (items.Any(item => !requestedIds.Contains(item.Id)) || items.Select(item => item.Id).Distinct().Count() != missing.Length)
                    throw new InvalidOperationException("Bench sync received an incomplete or out-of-scope item batch.");
                protectedCount += (await writer.SaveBatchProtectedAsync(items, ct)).Count;
                var bySource = items.ToDictionary(item => item.Id,
                    _ => (IReadOnlyList<Twig.Domain.ValueObjects.WorkItemLink>)Array.Empty<Twig.Domain.ValueObjects.WorkItemLink>());
                foreach (var group in edges.Where(edge => requestedIds.Contains(edge.SourceId)).GroupBy(edge => edge.SourceId))
                    bySource[group.Key] = group.ToArray();
                await links.SaveLinksForSourcesAsync(bySource, ct);
                foreach (var item in items) fetched.Add(item.Id, item);
            }

            async Task ExpandChildrenAsync(IEnumerable<int> roots, int depthLimit, bool markAsMembers = false)
            {
                var visited = new HashSet<int>();
                var frontier = roots.Distinct().ToArray();
                for (var depth = 0; depth < depthLimit && frontier.Length > 0; depth++)
                {
                    var children = new HashSet<int>();
                    foreach (var id in frontier)
                    {
                        if (!visited.Add(id)) continue;
                        foreach (var child in await ado.FetchChildrenAsync(id, ct))
                            if (child.Id > 0 && !visited.Contains(child.Id)) children.Add(child.Id);
                    }
                    if (markAsMembers) memberIds.UnionWith(children);
                    await FetchAsync(children);
                    frontier = children.ToArray();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.StderrWriter.WriteLine(fmt.FormatError(ex.Message));
            ReportTelemetry(1, null);
            return 1;
        }

        void ReportTelemetry(int exitCode, int? itemCount)
        {
            if (ctx.TelemetryClient is null) return;
            var version = VersionHelper.GetVersion();
            var metadata = version.IndexOf('+');
            if (metadata >= 0) version = version[..metadata];
            var metrics = new Dictionary<string, double>
            { ["duration_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
            if (itemCount.HasValue) metrics["item_count"] = itemCount.Value;
            ctx.TelemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
            {
                ["command"] = "workspace-sync",
                ["output_format"] = outputFormat,
                ["exit_code"] = exitCode.ToString(CultureInfo.InvariantCulture),
                ["twig_version"] = version,
                ["os_platform"] = RuntimeInformation.OSDescription,
            }, metrics);
        }
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
