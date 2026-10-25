using System.Globalization;
using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Serialization;
using Twig.RenderTree;

namespace Twig.Commands;

public sealed partial class WorkspaceCommand
{
    private async Task<int> ExecuteExplicitBrowserAsync(string target, string? expectedBench,
        string? expectedBinding, string? expectedIdentity, CancellationToken ct)
    {
        try
        {
            if (ResolveBrowserBindingAsync is null || currentBench is null || benchEvaluator is null)
                throw new InvalidOperationException("bench-read-unavailable: native connection and Bench services are required.");
            using var admission = authenticationProvider is null ? null
                : await ConnectionOperationAdmission.AcquireAsync(authenticationProvider, ct);
            using var operation = workItemRepo.AcquireOperation();
            var origin = await ResolveBrowserBindingAsync(ct);
            BrowserOriginGuard.EnsureExpected(origin, expectedBinding, expectedIdentity);
            var (effective, stored) = await currentBench.ResolveExplicitAsync(target, expectedBench, origin.Identity.AccountName, ct);
            var membership = await benchEvaluator.EvaluateAsync(effective, ct: ct);
            var ids = membership.QueryMatches.Select(item => item.Id)
                .Concat(membership.PinnedIds).Concat(membership.SeedIds)
                .Concat(membership.DirtyItemIds.Order()).Distinct().ToArray();
            var loaded = await LoadItemsInOrderAsync(ids, ct);
            var loadedById = loaded.ToDictionary(item => item.Id);
            var seeds = loaded.Where(item => item.IsSeed).ToArray();
            var sections = WorkspaceSections.Build(membership.QueryMatches,
                manualItems: loaded.Where(item => !item.IsSeed
                    && !membership.QueryMatches.Any(query => query.Id == item.Id)).ToArray(),
                excludedIds: []);
            var workspace = Workspace.Build(null, membership.QueryMatches, seeds, sections: sections);
            var process = await processTypeStore.GetProcessConfigurationDataAsync();
            var levels = process is null ? null : BacklogHierarchyService.GetTypeLevelMap(process);
            var depthOmitted = new HashSet<int>();
            var projection = await BuildBrowserDocumentAsync(workspace, effective, stored, membership, origin, levels, ct, depthOmitted);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            new JsonRenderer(writer).Render(new Twig.RenderTree.RenderTree([projection]));
            using var native = JsonDocument.Parse(writer.ToString());
            var roots = native.RootElement.GetProperty("roots").Clone();
            var renderedIds = new HashSet<int>();
            static void Collect(JsonElement nodes, HashSet<int> result)
            {
                foreach (var node in nodes.EnumerateArray())
                {
                    result.Add(node.GetProperty("id").GetInt32());
                    Collect(node.GetProperty("children"), result);
                }
            }
            Collect(roots, renderedIds);
            var missingAncestors = new HashSet<int>();
            var cachedAncestors = new HashSet<int>();
            var parentCycle = false;
            foreach (var item in loaded)
            {
                var parent = item.ParentId;
                var visited = new HashSet<int> { item.Id };
                while (parent is { } parentId)
                {
                    if (!visited.Add(parentId)) { parentCycle = true; break; }
                    var ancestor = await workItemRepo.GetByIdAsync(parentId, ct);
                    if (ancestor is null) { missingAncestors.Add(parentId); break; }
                    cachedAncestors.Add(parentId);
                    parent = ancestor.ParentId;
                }
            }
            var omitted = ids.Where(id => loadedById.ContainsKey(id) && !renderedIds.Contains(id)).ToArray();
            var reasons = new List<string> { "source-total-unknown", "cache-only" };
            if (loaded.Count != ids.Length) reasons.Add("known-members-not-cached");
            if (missingAncestors.Count > 0) reasons.Add("ancestors-not-cached");
            var prunedAncestors = cachedAncestors.Except(renderedIds).Except(depthOmitted).Order().ToArray();
            if (prunedAncestors.Length > 0) reasons.Add("ancestor-pruning");
            if (depthOmitted.Count > 0) reasons.Add("depth-limit");
            if (parentCycle) reasons.Add("cached-parent-cycle");
            if (omitted.Length > 0) reasons.Add("hierarchy-working-level-or-depth-omission");
            // Even an unpruned mirror is not evidence of a complete source query or subtree.
            if (effective.Selectors.Any(selector => selector.Kind is Domain.Enums.SelectorKind.Query or Domain.Enums.SelectorKind.Subtree))
                reasons.Add("selector-source-coverage-unknown");
            var members = ids.Select(id =>
            {
                loadedById.TryGetValue(id, out var item);
                return new BenchReadMember(id, item is not null, membership.SelectedIds.Contains(id),
                    membership.SeedIds.Contains(id), membership.DirtyItemIds.Contains(id),
                    item?.Title, item?.Type.Value, item?.State, item?.ParentId);
            }).ToArray();
            var document = new BenchReadDocument(2, target, stored.Id.ToString(CultureInfo.InvariantCulture), stored.Name,
                BenchReadOrigin.From(origin),
                "raw-mirror", members,
                new BenchReadCompleteness(ids.Length, loaded.Count, ids.Length - loaded.Count, null, null, false,
                    omitted.Length == 0 && missingAncestors.Count == 0 && prunedAncestors.Length == 0
                        && depthOmitted.Count == 0 && !parentCycle && loaded.Count == ids.Length,
                    renderedIds.Count, omitted, missingAncestors.Order().ToArray(),
                    prunedAncestors, depthOmitted.Order().ToArray(),
                    ctx.Config.Display.TreeDepthDown, ctx.Config.Display.TreeDepthUp,
                    ctx.Config.Workspace.WorkingLevel, reasons),
                native.RootElement.GetProperty("configuration").Clone(), roots);

            if (origin != await ResolveBrowserBindingAsync(ct))
                throw new InvalidOperationException("bench-origin-changed: checkout, connection, binding or identity changed during the read.");
            var (finalEffective, finalStored) = await currentBench.ResolveExplicitAsync(target,
                stored.Id.ToString(CultureInfo.InvariantCulture), origin.Identity.AccountName, ct);
            if (stored.Name != finalStored.Name || stored.IsDefault != finalStored.IsDefault
                || !stored.Selectors.ToHashSet().SetEquals(finalStored.Selectors)
                || !effective.Selectors.ToHashSet().SetEquals(finalEffective.Selectors))
                throw new InvalidOperationException("bench-target-changed: the requested Bench changed during the read.");
            if (origin != await ResolveBrowserBindingAsync(ct))
                throw new InvalidOperationException("bench-origin-changed: checkout, connection, binding or identity changed during the read.");
            var json = JsonSerializer.Serialize(new BenchReadEnvelope(document), TwigJsonContext.Default.BenchReadEnvelope);
            Console.WriteLine(json);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(FormatBenchReadError("json", ex.Message));
            return 1;
        }
    }

    private string FormatBenchReadError(string format, string message)
        => NormalizeOutputFormat(format) == "json"
            ? JsonSerializer.Serialize(new BenchReadError(message), TwigJsonContext.Default.BenchReadError)
            : ctx.FormatterFactory.GetFormatter(format).FormatError(message);
}
