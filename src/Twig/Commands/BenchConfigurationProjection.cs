using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.RenderTree;

namespace Twig.Commands;

internal static class BenchConfigurationProjection
{
    internal static async Task<RenderCell> BuildAsync(Bench effective, Bench stored,
        IWorkItemRepository repository, CancellationToken ct)
    {
        var rules = effective.Selectors.Where(s => s.Kind == SelectorKind.Query).Select(BenchQueryRule.Parse).ToArray();
        var areas = rules.SelectMany(rule => rule.Areas).Distinct().Select(area => Object(new()
        {
            ["path"] = RenderCell.String(area.Path),
            ["includeChildren"] = RenderCell.Boolean(area.IncludeChildren),
        })).ToArray();
        var sprints = rules.SelectMany(rule => rule.Sprints).Select(sprint => sprint.Raw)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(expression => Object(new()
            { ["expression"] = RenderCell.String(expression) })).ToArray();
        var pins = new List<RenderCell>();
        foreach (var selector in stored.Selectors.Where(s => s.Kind is SelectorKind.Item or SelectorKind.Subtree)
            .OrderBy(s => s.AsWorkItemId()).ThenBy(s => s.Kind))
        {
            var id = selector.AsWorkItemId();
            var item = await repository.GetByIdAsync(id, ct);
            var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["id"] = RenderCell.Integer(id),
                ["mode"] = RenderCell.String(selector.Kind == SelectorKind.Item ? "single" : "tree"),
                ["cached"] = RenderCell.Boolean(item is not null),
            };
            if (item is not null)
            {
                fields["title"] = RenderCell.String(item.Title);
                fields["type"] = RenderCell.String(item.Type.Value);
                fields["state"] = RenderCell.String(item.State);
            }
            pins.Add(Object(fields));
        }
        var owners = rules.Select(rule => rule.UniqueName is not null ? "Canonical principal: " + rule.UniqueName
            : rule.AssignedTo is not null ? "Saved assignee: " + rule.AssignedTo : "All assignees")
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return Object(new()
        {
            ["version"] = RenderCell.Integer(1),
            ["settingsDigest"] = RenderCell.String(BenchQueryRule.SettingsDigest(stored.Selectors)),
            ["areas"] = Array(areas),
            ["sprints"] = Array(sprints),
            ["automaticEnabled"] = RenderCell.Boolean(rules.Any(rule => rule.Sprints.Count > 0)),
            ["assigneeSummary"] = RenderCell.String(rules.Length == 0 ? "No automatic ownership rule" : string.Join("; ", owners)),
            ["pins"] = Array(pins),
        });
    }

    private static RenderCell Object(Dictionary<string, RenderCell> fields)
        => new(string.Empty, new RenderValue.Object(fields));
    private static RenderCell Array(IReadOnlyList<RenderCell> values)
        => new(string.Empty, new RenderValue.Array(values));
}
