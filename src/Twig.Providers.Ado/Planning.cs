using System.Collections.Immutable;
using System.Text.Json;
using Twig.Core;

namespace Twig.Providers.Ado;

public sealed partial class AdoProvider
{
    public Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(Connection connection, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) =>
        {
            const int pageSize = 100;
            const int limit = 4096;
            var teams = ImmutableArray.CreateBuilder<PlanningTeam>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var skip = 0; ; skip += pageSize)
            {
                var rows = Wire.Rows(await Get(bound,
                    $"_apis/projects/{bound.Project}/teams?$top={pageSize}&$skip={skip}&api-version={Api}", token));
                if (rows.Length > pageSize || teams.Count + rows.Length > limit)
                    throw Wire.Refuse(ErrorCode.Partial, "The native project team roster exceeds its bounded discovery limit.");
                foreach (var row in rows)
                {
                    var id = Wire.GuidText(Wire.Text(row, "id"));
                    var name = Wire.Text(row, "name");
                    if (!seen.Add(id) || name.Length > 256 || name.Any(char.IsControl))
                        throw Wire.Invalid("The native team roster repeats an identity or has an invalid display name.");
                    teams.Add(new(id, name));
                }
                if (rows.Length < pageSize) return teams.ToImmutable();
            }
        }, ct));

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
            var canonicalTeam = await ResolvePlanningTeam(bound, team, token);
            var gaps = ImmutableArray<string>.Empty;
            if (canonicalTeam is not null)
            {
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

    private async Task<string?> ResolvePlanningTeam(Bound bound, string? team, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(team)) return null;
        var selected = await Get(bound, $"_apis/projects/{bound.Project}/teams/{Wire.Escape(team)}?api-version={Api}", ct);
        var canonical = Wire.GuidText(Wire.Text(selected, "id"));
        if (!team.Equals(canonical, StringComparison.OrdinalIgnoreCase)
            && !team.Equals(Wire.Text(selected, "name"), StringComparison.OrdinalIgnoreCase))
            throw Wire.Refuse(ErrorCode.Conflict, "The native team lookup resolved a different selection.");
        return canonical;
    }
}
