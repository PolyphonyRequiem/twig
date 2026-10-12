using System.Globalization;
using Twig.Commands;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Services.Mutation;
using Twig.Rendering;

namespace Twig.Browser;

internal sealed record BrowserBenchItem(int Id, int? ParentId, string Type, string State, string Title,
    bool Cached, bool Member, bool Owed, bool Seed, bool SinglePin, bool TreePin, IReadOnlyList<int> SubtreeOwners);
internal sealed record BrowserBenchChoice(string Id, string Name, bool IsDefault, string ContentsDigest,
    IReadOnlyList<string> Contents);
internal sealed record BrowserBenchObservation(BrowserCapture Capture, string SettingsDigest,
    IReadOnlyList<BrowserBenchItem> Items, IReadOnlyList<AreaPathFilter> Areas, IReadOnlyList<string> Sprints,
    IReadOnlyList<BrowserBenchChoice> Benches, string Ownership);
internal sealed record BrowserBenchChange(string Message, BrowserBenchChoice? Confirmation = null, bool Changed = true,
    BrowserCapture? NextCapture = null);

/// <summary>Immutable local memberships and native arrangement workflows. No active-item writes or presync.</summary>
internal sealed class BrowserBenchModule(BrowserOperationModule operations, CurrentBenchResolver resolver,
    BenchEvaluator evaluator, IWorkItemRepository repository, IBenchRepository benches, DefaultBenchSelectors defaults,
    BenchWorkflow workflow, PinWorkflow pins, IIterationService iterations)
{
    internal Task<BrowserOperationResult<BrowserBenchObservation>> ReadAsync(BrowserCapture? captured = null, CancellationToken ct = default)
        => operations.ExecuteAsync(captured, async (capture, token) =>
        {
            var (effective, stored) = await resolver.ResolveCapturedAsync(token, capture.BenchId);
            var membership = await evaluator.EvaluateAsync(effective, ct: token);
            var all = membership.AllIds;
            var single = stored.Selectors.Where(s => s.Kind == SelectorKind.Item).Select(s => s.AsWorkItemId()).ToHashSet();
            var tree = stored.Selectors.Where(s => s.Kind == SelectorKind.Subtree).Select(s => s.AsWorkItemId()).ToHashSet();
            var seeds = membership.SeedIds.ToHashSet();
            var items = new Dictionary<int, BrowserBenchItem>();
            var queue = new Queue<int>(all.Order());
            var visited = new HashSet<int>();
            while (queue.TryDequeue(out var id))
            {
                if (!visited.Add(id)) continue;
                var item = await repository.GetByIdAsync(id, token);
                if (item is null)
                {
                    if (all.Contains(id)) items[id] = new(id, null, "", "", "uncached / unverified", false, true,
                        membership.DirtyItemIds.Contains(id), seeds.Contains(id), single.Contains(id), tree.Contains(id),
                        membership.OwningSubtreeIds.GetValueOrDefault(id)?.ToArray() ?? []);
                    continue;
                }
                // Copy scalars immediately; mutable domain aggregates do not escape.
                items[id] = new(id, item.ParentId, RichHtmlRenderer.SafeText(item.Type.Value), RichHtmlRenderer.SafeText(item.State),
                    RichHtmlRenderer.SafeText(item.Title), true, all.Contains(id), membership.DirtyItemIds.Contains(id),
                    seeds.Contains(id), single.Contains(id), tree.Contains(id), membership.OwningSubtreeIds.GetValueOrDefault(id)?.ToArray() ?? []);
                if (item.ParentId is { } parent) queue.Enqueue(parent);
            }
            var rules = effective.Selectors.Where(s => s.Kind == SelectorKind.Query).Select(BenchQueryRule.Parse).ToArray();
            return new BrowserBenchObservation(capture, BenchQueryRule.SettingsDigest(stored.Selectors),
                items.Values.OrderBy(i => i.Id).ToArray(), rules.SelectMany(r => r.Areas).Distinct().ToArray(),
                rules.SelectMany(r => r.Sprints).Select(s => s.Raw).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                (await benches.GetAllAsync(token)).Select(b => new BrowserBenchChoice(b.Id.ToString(CultureInfo.InvariantCulture),
                    b.Name, b.IsDefault, BenchQueryRule.ContentsDigest(b.Selectors), b.Selectors.Select(s => s.Kind == SelectorKind.Query
                        ? s.Payload : $"{s.Kind} #{s.AsWorkItemId()}").ToArray())).ToArray(),
                string.Join("; ", rules.Select(r => r.UniqueName ?? r.AssignedTo ?? "All assignees").Distinct()));
        }, ct);

    internal Task<BrowserOperationResult<IReadOnlyList<AreaPathFilter>>> ReadAreaCandidatesAsync(BrowserCapture capture, CancellationToken ct = default)
        => operations.ExecuteAsync<IReadOnlyList<AreaPathFilter>>(capture, async (_, token) =>
        {
            var areas = await iterations.GetTeamAreaPathsAsync(token);
            if (areas.Any(a => string.IsNullOrWhiteSpace(a.Path)))
                throw new InvalidOperationException("The configured team returned an invalid area path. No settings were changed.");
            return areas.Select(a => new AreaPathFilter(a.Path, a.IncludeChildren)).ToArray();
        }, ct);

    internal Task<BrowserOperationResult<BrowserBenchChange>> PinAsync(BrowserBenchObservation observation, int id, bool subtree, CancellationToken ct = default)
        => ChangePinAsync(observation, id, subtree, true, ct);

    internal Task<BrowserOperationResult<BrowserBenchChange>> AddPinAsync(BrowserBenchObservation observation, int id, bool subtree, CancellationToken ct = default)
        => ChangePinAsync(observation, id, subtree, false, ct);

    private Task<BrowserOperationResult<BrowserBenchChange>> ChangePinAsync(BrowserBenchObservation observation, int id, bool subtree,
        bool toggle, CancellationToken ct)
        => operations.ChangeBenchAsync(observation.Capture, async (_, token) =>
        {
            if (id <= 0) throw new BrowserReadException(BrowserReadFailureKind.Usage, "Local seeds cannot be pinned; owed work already appears on every Bench.");
            var stored = await resolver.ResolveStoredAsync(token, observation.Capture.BenchId);
            var present = observation.Items.FirstOrDefault(i => i.Id == id);
            if (toggle && (subtree ? present?.TreePin == true : present?.SinglePin == true))
            {
                var outcome = (PinOutcome.Unpinned)await pins.UnpinAsync(id, token, observation.Capture.BenchId, observation.SettingsDigest,
                    subtree ? TrackingMode.Tree : TrackingMode.Single);
                return new BrowserBenchChange($"Removed {(subtree ? "subtree" : "single")} pin #{id}; inherited/query/owed membership is unchanged.",
                    NextCapture: Capture(observation.Capture, outcome.Bench));
            }
            await pins.PinAsync(id, subtree, token, observation.Capture.BenchId, observation.SettingsDigest);
            var selector = subtree ? BenchSelector.ForSubtree(id) : BenchSelector.ForItem(id);
            var committed = stored with { Selectors = stored.Selectors.Append(selector).Distinct().ToArray() };
            return new BrowserBenchChange($"Pinned #{id} ({(subtree ? "subtree" : "single")}); no remote verification was performed.",
                NextCapture: Capture(observation.Capture, committed));
        }, change => change.NextCapture ?? observation.Capture, ct);

    internal Task<BrowserOperationResult<BrowserBenchChange>> SettingsAsync(BrowserBenchObservation observation,
        string section, string action, string value, bool exact = false, CancellationToken ct = default)
        => operations.ChangeBenchAsync(observation.Capture, async (_, token) =>
        {
            var (effective, stored) = await resolver.ResolveCapturedAsync(token, observation.Capture.BenchId);
            if (observation.SettingsDigest != BenchQueryRule.SettingsDigest(stored.Selectors))
                throw new InvalidOperationException("Automatic settings changed; refresh before editing.");
            var committed = await BenchSettingsEditor.ApplyAsync(effective, stored, defaults, benches, section, action, value, exact, token);
            return new BrowserBenchChange($"{section} {action}: {value}. Local Bench settings only.", NextCapture: Capture(observation.Capture, committed));
        }, change => change.NextCapture ?? observation.Capture, ct);

    internal Task<BrowserOperationResult<BrowserBenchChange>> CreateAsync(BrowserCapture capture, string name, CancellationToken ct = default)
        => operations.ChangeBenchAsync(capture, async (_, token) => Describe(await workflow.CreateAsync(name, token)), _ => capture, ct);
    internal Task<BrowserOperationResult<BrowserBenchChange>> SwitchAsync(BrowserCapture capture, BrowserBenchChoice target, CancellationToken ct = default)
        => operations.ChangeBenchAsync(capture, async (_, token) =>
        {
            var result = await workflow.SwitchAsync(target.Name, token, target.Id);
            return Describe(result) with
            {
                NextCapture = result is BenchOutcome.Switched
                ? new(target.Id, target.Name, capture.Origin, target.ContentsDigest) : capture
            };
        }, change => change.NextCapture ?? capture, ct);
    internal Task<BrowserOperationResult<BrowserBenchChange>> DeleteAsync(BrowserCapture capture, BrowserBenchChoice target,
        string? confirmedName = null, CancellationToken ct = default)
        => operations.ChangeBenchAsync(capture, async (_, token) =>
        {
            var fallback = target.Id == capture.BenchId
                ? await benches.GetByNameAsync(Twig.Domain.Aggregates.Bench.DefaultName, token) : null;
            var result = await workflow.DeleteAsync(target.Name, confirmedName, token, target.Id, target.ContentsDigest);
            return Describe(result) with
            {
                NextCapture = result is BenchOutcome.Deleted && target.Id == capture.BenchId
                ? fallback is not null ? Capture(capture, fallback) : throw new InvalidOperationException("Cached Default Bench is unavailable after deletion.")
                : capture
            };
        }, change => change.NextCapture ?? capture, ct);

    private static BrowserCapture Capture(BrowserCapture origin, Twig.Domain.Aggregates.Bench bench)
        => new(bench.Id.ToString(CultureInfo.InvariantCulture), bench.Name, origin.Origin, BenchQueryRule.ContentsDigest(bench.Selectors));

    private static BrowserBenchChange Describe(BenchOutcome outcome) => outcome switch
    {
        BenchOutcome.Created c => new($"Created empty Bench '{c.Bench.Name}'. Select it explicitly to switch."),
        BenchOutcome.Switched s => new($"Switched to '{s.Bench.Name}'."),
        BenchOutcome.Deleted d => new($"Deleted arrangement '{d.Bench.Name}'; pending work is untouched."),
        BenchOutcome.HoldsWork h => new($"'{h.Bench.Name}' holds saved selectors. Read the contents and retype the name to delete.",
            new(h.Bench.Id.ToString(CultureInfo.InvariantCulture), h.Bench.Name, h.Bench.IsDefault,
                BenchQueryRule.ContentsDigest(h.Bench.Selectors), h.Bench.Selectors.Select(s => s.Payload).ToArray()), false),
        BenchOutcome.DefaultBenchCannotBeDeleted => new("Default is protected and cannot be deleted.", Changed: false),
        BenchOutcome.NameAlreadyExists e => new($"Bench '{e.Existing.Name}' already exists; nothing was created.", Changed: false),
        BenchOutcome.NameRejected r => new(r.Reason, Changed: false),
        BenchOutcome.UnknownBench u => new($"Unknown Bench '{u.RequestedName}'; nothing was created.", Changed: false),
        _ => throw new InvalidOperationException("Unknown Bench workflow outcome.")
    };
}
