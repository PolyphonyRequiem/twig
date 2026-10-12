using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;

namespace Twig.Commands;

/// <summary>One automatic-selector mutation policy for CLI and native browser, using the store's settings CAS.</summary>
internal static class BenchSettingsEditor
{
    internal static async Task<Bench> ApplyAsync(Bench effective, Bench stored, DefaultBenchSelectors defaults,
        IBenchRepository benches, string section, string action, string? value, bool exact, CancellationToken ct)
    {
        if (section is not ("area" or "sprint") || action is not ("add" or "remove"))
            throw new ArgumentException("Expected area or sprint add/remove.");
        AreaPathFilter? area = null;
        IterationExpression? sprint = null;
        if (section == "area")
        {
            var parsed = AreaPath.Parse(value);
            if (!parsed.IsSuccess) throw new ArgumentException(parsed.Error);
            area = new(parsed.Value.Value, !exact);
        }
        else sprint = BenchQueryRule.ParseSprint(value);
        var rules = effective.Selectors.Where(s => s.Kind == SelectorKind.Query).Select(BenchQueryRule.Parse).ToList();
        if (rules.Count == 0)
        {
            var unique = effective.IsDefault ? (await defaults.BuildAsync(ct)).Single().QueryAssignedToUniqueName : null;
            rules.Add(new(null, unique, [], []));
        }
        var replacements = new List<BenchSelector>();
        foreach (var rule in rules)
        {
            var changed = rule;
            if (area is { } filter)
            {
                var updated = rule.Areas.Where(existing => !string.Equals(existing.Path, filter.Path, StringComparison.OrdinalIgnoreCase)).ToList();
                if (action == "add") updated.Add(filter);
                changed = rule with { Areas = updated };
            }
            else if (sprint is { } expression)
            {
                var updated = rule.Sprints.Where(existing => !string.Equals(existing.Raw, expression.Raw, StringComparison.OrdinalIgnoreCase)).ToList();
                if (action == "add") updated.Add(expression);
                changed = rule with { Sprints = updated };
            }
            replacements.Add(changed.ToSelector());
        }
        if (!await benches.TryReplaceQuerySelectorsAsync(effective.Id, BenchQueryRule.SettingsDigest(stored.Selectors), replacements.Distinct().ToArray(), ct))
            throw new InvalidOperationException("The captured Bench or automatic settings changed. Refresh and retry; no settings were changed.");
        return stored with { Selectors = stored.Selectors.Where(s => s.Kind != SelectorKind.Query).Concat(replacements).Distinct().ToArray() };
    }
}
