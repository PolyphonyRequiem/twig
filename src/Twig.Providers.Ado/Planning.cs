using System.Collections.Immutable;
using System.Text.Json;
using Twig.Core;

namespace Twig.Providers.Ado;

public sealed partial class AdoProvider
{
    public Task<Result<PlanningMetadata>> DiscoverPlanningAsync(Connection connection, string? team, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) =>
        {
            var areas = new List<string>();
            var iterations = new List<PlanningIteration>();
            await Walk("areas", node => areas.Add(PathOf(node)), token);
            await Walk("iterations", node =>
            {
                DateTimeOffset? Date(string name) => node.TryGetProperty("attributes", out var attributes)
                    && attributes.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetDateTimeOffset() : null;
                var start = Date("startDate");
                var finish = Date("finishDate");
                if (start > finish) throw Wire.Invalid("The native iteration calendar contains an inverted date range.");
                iterations.Add(new(PathOf(node), start, finish, SelectedTeam: false));
            }, token);
            string? canonicalTeam = null;
            var gaps = ImmutableArray<string>.Empty;
            if (!string.IsNullOrWhiteSpace(team))
            {
                var selected = await Get(bound, $"_apis/projects/{bound.Project}/teams/{Wire.Escape(team)}?api-version={Api}", token);
                canonicalTeam = Wire.GuidText(Wire.Text(selected, "id"));
                if (!team.Equals(canonicalTeam, StringComparison.OrdinalIgnoreCase)
                    && !team.Equals(Wire.Text(selected, "name"), StringComparison.OrdinalIgnoreCase))
                    throw Wire.Refuse(ErrorCode.Conflict, "The native team lookup resolved a different selection.");
                var rows = Wire.Rows(await Get(bound,
                    $"{bound.Project}/{canonicalTeam}/_apis/work/teamsettings/iterations?api-version={Api}", token));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                {
                    var path = Wire.Text(row, "path");
                    if (!seen.Add(path)) throw Wire.Invalid("The selected team returned a duplicate iteration path.");
                    var index = iterations.FindIndex(iteration => iteration.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) throw Wire.Refuse(ErrorCode.Conflict, "The selected team iteration is outside the acquired project classification.");
                    var attributes = row.GetProperty("attributes");
                    DateTimeOffset? Date(string name) => attributes.TryGetProperty(name, out var value)
                        && value.ValueKind != JsonValueKind.Null ? value.GetDateTimeOffset() : null;
                    var start = Date("startDate");
                    var finish = Date("finishDate");
                    if (start > finish) throw Wire.Invalid("The selected team iteration has an inverted date range.");
                    iterations[index] = new(path, start, finish, SelectedTeam: true);
                }
            }
            else gaps = ["No native team is selected. Absolute paths are available; relative iteration rules require an explicitly selected team calendar."];
            return new PlanningMetadata(DateTimeOffset.UtcNow, areas.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
                iterations.ToImmutableArray(), gaps, canonicalTeam);

            string PathOf(JsonElement node)
            {
                var path = Wire.Text(node, "path").TrimStart('\\');
                var separator = path.IndexOf('\\');
                var next = separator < 0 ? -1 : path.IndexOf('\\', separator + 1);
                // Classification paths include the native structural Areas/Iterations component, not a work-item path segment.
                if (separator < 0) throw Wire.Invalid("Native classification path is incomplete.");
                if (!path[..separator].Equals(bound.ProjectName, StringComparison.OrdinalIgnoreCase))
                    throw Wire.Refuse(ErrorCode.Conflict, "Native classification belongs to another project.");
                return path[..separator] + (next < 0 ? "" : path[next..]);
            }
            async Task Walk(string kind, Action<JsonElement> take, CancellationToken cancellation)
            {
                var root = await Get(bound, $"{bound.Project}/_apis/wit/classificationnodes/{kind}?$depth=14&api-version={Api}", cancellation);
                var pending = new Stack<JsonElement>(); pending.Push(root);
                var count = 0;
                while (pending.TryPop(out var node))
                {
                    if (++count > 4096) throw Wire.Refuse(ErrorCode.Partial, "Native planning metadata exceeds its declared node bound.");
                    take(node);
                    var hasChildren = node.TryGetProperty("hasChildren", out var flag) && flag.GetBoolean();
                    var children = node.TryGetProperty("children", out var values) ? values.EnumerateArray().ToArray() : [];
                    if (hasChildren && children.Length == 0)
                        throw Wire.Refuse(ErrorCode.Partial, "Native classification discovery was truncated; no complete calendar was saved.");
                    foreach (var child in children) pending.Push(child);
                }
            }
        }, ct));
}
