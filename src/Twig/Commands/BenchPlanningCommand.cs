using System.Diagnostics;
using System.Globalization;
using System.Text;
using Twig.Core;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Twig.Infrastructure.Telemetry;
using Twig.Rendering;
using Twig.RenderTree;
using Twig.Store.Sqlite;

namespace Twig.Commands;

internal sealed record BenchPlanningArguments(string? Bench = null, string? Value = null, bool Exact = false,
    string? Area = null, string? Iteration = null, bool Mine = false, string? Query = null, string? Backlog = null,
    int? Rule = null, long? ExpectedRevision = null);

internal sealed class BenchPlanningCommand(UserBenchStore store, RendererFactory renderers,
    CoreWorkspaceFactory? factory = null, ITelemetryClient? telemetry = null)
{
    internal async Task<int> ExecuteAsync(string action, BenchPlanningArguments args, string output, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var format = OutputFormats.Normalize(output);
        var exit = 1;
        try { return exit = await Execute(); }
        catch (OperationCanceledException) { return exit = Error("Planning operation cancelled. A local commit may already exist; re-read rules/metadata before retrying. No upstream item was published."); }
        catch (ArgumentException error) { return exit = Error(error.Message, 2); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return exit = Error("Planning operation could not complete: " + error.Message); }
        finally
        {
            var command = action switch
            {
                "area-add" => "workspace area add", "area-remove" => "workspace area remove", "area-list" => "workspace area list",
                "sprint-add" => "workspace sprint add", "sprint-remove" => "workspace sprint remove", "sprint-list" => "workspace sprint list",
                "rules" => "bench rules", "add-rule" => "bench rule add", "remove-rule" => "bench rule remove",
                "sources" => "bench sources", "browse" => "bench browse", "refresh" => "bench planning refresh", _ => "bench planning",
            };
            try { TelemetryHelper.TrackCommand(telemetry, command, format ?? "invalid", exit, started); }
            catch (Exception) { Debug.WriteLine("Optional planning telemetry failed; command outcome was preserved."); }
        }

        int Error(string message, int code = 1)
        {
            CommandError.Write(renderers, Console.Error, format ?? OutputFormats.Default, message);
            return code;
        }
        void Render(string title, string[] columns, IEnumerable<Dictionary<string, RenderCell>> values,
            Dictionary<string, RenderCell>? scope = null)
        {
            var nodes = new List<RenderNode>();
            if (scope is not null)
            {
                scope.TryAdd("message", RenderCell.String(title));
                nodes.Add(new RenderNode.Record("scope", scope));
            }
            if (columns.Length != 0) nodes.Add(new RenderNode.Table(title, columns.Select(column => new RenderColumn(column, column)).ToArray(),
                values.Select(value => new RenderRow("entry", value)).ToArray()));
            renderers.GetRenderer(format!).Render(new RenderTree.RenderTree(nodes));
        }
        Dictionary<string, RenderCell> BenchScope(Bench bench) => new()
        {
            ["benchId"] = RenderCell.String(bench.Id.ToString("D")), ["benchName"] = RenderCell.String(bench.Name),
            ["revision"] = RenderCell.Integer(bench.Revision),
        };
        async Task<int> Execute()
        {
            if (format is null) return Error($"Unsupported output format. Use {OutputFormats.Describe()}.", 2);
            if (format == "ids" && action is not ("rules" or "area-list" or "sprint-list" or "sources" or "browse"))
                return Error("IDs output is supported only by planning lists and native member browsing.", 2);
            if (args.Query is not null && args.Backlog is not null)
                return Error("Choose one saved query or backlog identity.", 2);
            if ((args.Query is not null || args.Backlog is not null) && (args.Area is not null || args.Iteration is not null || args.Mine))
                return Error("Native source references are standalone rules. Area, iteration and ownership conditions may be combined in a separate planning rule.", 2);
            if (args.ExpectedRevision is < 0 || args.Rule is < 1)
                return Error("Use a nonnegative captured revision and a positive one-based rule index.", 2);
            if (args.Rule is not null && args.ExpectedRevision is null)
                return Error("An explicit rule index requires --expect-revision from a fresh rule listing.", 2);
            if (args.Bench is not null) Text(args.Bench);
            if (action == "browse" && args.Query is null && args.Backlog is null)
                return Error("Choose --query <native GUID> or --backlog <native ID>.", 2);
            if (action == "add-rule" && args.Area is null && args.Iteration is null && !args.Mine && args.Query is null && args.Backlog is null)
                return Error("Choose --area, --iteration, --mine, --query or --backlog.", 2);
            if (args.Iteration is not null) Expression(Text(args.Iteration));
            if (args.Area is not null) Text(args.Area);
            if (action is "area-add" or "area-remove" or "sprint-add" or "sprint-remove")
            {
                var input = Text(args.Value ?? "");
                if (action.StartsWith("sprint-", StringComparison.Ordinal)) Expression(input);
            }

            CoreWorkspaceHandle? handle = null;
            IDisposable? operation = null;
            try
            {
                var needsContext = action is not ("rules" or "remove-rule") || args.Bench is null;
                if (needsContext)
                {
                    if (factory is null) return Error("Choose an existing initialized native Twig context. Nothing was initialized.");
                    var opened = await factory.OpenAsync(ct);
                    if (!opened.IsSuccess) return Error(opened.Error.Message);
                    handle = opened.Value;
                    operation = ConnectionOperationGate.Acquire(handle.Workspace.WorktreeRoot, exclusive: true);
                    var admitted = await factory.AdmitSourceAsync(handle, ct);
                    if (!admitted.IsSuccess) return Error(admitted.Error.Message);
                }
                var workspace = handle?.Workspace;
                if (action is "metadata" or "refresh")
                {
                    if (workspace is null) return Error("A native planning context is required.");
                    PlanningMetadata? metadata;
                    if (action == "refresh")
                    {
                        var observed = await workspace.DiscoverPlanningAsync(ct);
                        if (!observed.IsSuccess) return Error(observed.Error.Message);
                        var admitted = await factory!.AdmitSourceAsync(handle!, ct);
                        if (!admitted.IsSuccess) return Error(admitted.Error.Message);
                        var saved = await workspace.SavePlanningAsync(observed.Value, ct);
                        if (!saved.IsSuccess) return Error(saved.Error.Message);
                        metadata = saved.Value;
                    }
                    else
                    {
                        var cached = await workspace.PlanningAsync(ct);
                        if (!cached.IsSuccess) return Error(cached.Error.Message);
                        metadata = cached.Value.Metadata;
                    }
                    if (metadata is null) return Error("No native planning metadata is cached. Use 'twig bench planning refresh' explicitly.");
                    Render("Native planning metadata", ["kind", "path", "start", "finish", "selectedTeam"],
                        metadata.Areas.Select(path => new Dictionary<string, RenderCell>
                        {
                            ["kind"] = RenderCell.String("area"), ["path"] = RenderCell.String(path),
                        }).Concat(metadata.Iterations.Select(iteration => new Dictionary<string, RenderCell>
                        {
                            ["kind"] = RenderCell.String("iteration"), ["path"] = RenderCell.String(iteration.Path),
                            ["start"] = RenderCell.String(iteration.Start?.ToString("O") ?? ""),
                            ["finish"] = RenderCell.String(iteration.Finish?.ToString("O") ?? ""),
                            ["selectedTeam"] = RenderCell.Boolean(iteration.SelectedTeam),
                        })), new()
                        {
                            ["team"] = RenderCell.String(metadata.Team ?? ""), ["observedAt"] = RenderCell.String(metadata.ObservedAt.ToString("O")),
                            ["gaps"] = RenderCell.String(string.Join(Environment.NewLine, metadata.Gaps)),
                            ["message"] = RenderCell.String("Planning metadata only. Bench rules, work items, active item and selections were not changed."),
                        });
                    return 0;
                }
                if (action is "sources" or "browse")
                {
                    if (workspace is null) return Error("A native planning context is required.");
                    var discovered = await workspace.DiscoverPlanningSourcesAsync(ct);
                    if (!discovered.IsSuccess) return Error(discovered.Error.Message);
                    var choices = discovered.Value;
                    if (action == "sources")
                    {
                        if (format == "ids")
                        { foreach (var source in choices.Entries.Where(source => !source.IsFolder)) Console.WriteLine(source.Id); return 0; }
                        Render("Native saved queries and backlogs", ["kind", "id", "path", "available"],
                            choices.Entries.Select(source => new Dictionary<string, RenderCell>
                            {
                                ["kind"] = RenderCell.String(source.IsFolder ? "folder" : source.Kind.ToString()),
                                ["id"] = RenderCell.String(source.Id), ["path"] = RenderCell.String(source.Path),
                                ["available"] = RenderCell.Boolean(!source.IsFolder && (!source.RequiresTeam || choices.Team is not null)),
                            }), new() { ["team"] = RenderCell.String(choices.Team ?? ""), ["gaps"] = RenderCell.String(string.Join(Environment.NewLine, choices.Gaps)) });
                        return 0;
                    }
                    var kind = args.Query is not null ? PlanningSourceKind.SavedQuery : PlanningSourceKind.Backlog;
                    var id = args.Query ?? args.Backlog;
                    if (string.IsNullOrWhiteSpace(id)) return Error("Choose --query <native GUID> or --backlog <native ID>.", 2);
                    var selectedSource = choices.Entries.SingleOrDefault(source => !source.IsFolder && source.Kind == kind && source.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (selectedSource is null) return Error("That native source is not in this connection/project/team.");
                    var acquired = await workspace.BrowsePlanningAsync(kind, selectedSource.Id, choices.Team, ct);
                    if (!acquired.IsSuccess) return Error(acquired.Error.Message);
                    var admitted = await factory!.AdmitSourceAsync(handle!, ct);
                    if (!admitted.IsSuccess) return Error(admitted.Error.Message);
                    if (format == "ids")
                    { foreach (var key in acquired.Value.Items) Console.WriteLine(key.NativeId); return 0; }
                    var snapshot = await workspace.SnapshotAsync(ct);
                    if (!snapshot.IsSuccess) return Error(snapshot.Error.Message);
                    var schema = await workspace.SchemaAsync(ct);
                    if (!schema.IsSuccess) return Error(schema.Error.Message);
                    Render("Native source members", ["id", "type", "title", "webUrl"],
                        acquired.Value.Items.Select(key =>
                        {
                            var item = snapshot.Value.Items.Single(item => item.Key == key);
                            return new Dictionary<string, RenderCell>
                            {
                                ["id"] = RenderCell.String(key.NativeId!), ["type"] = RenderCell.String(item.Type),
                                ["title"] = RenderCell.String(item.Fields.GetValueOrDefault("System.Title")?.Text ?? ""),
                                ["webUrl"] = RenderCell.String(schema.Value.WebUrl is { } url && workspace.Connection.Provider.Value == "ado"
                                    ? new Uri(new Uri(url), "_workitems/edit/" + key.NativeId).AbsoluteUri : ""),
                            };
                        }), new()
                        {
                            ["kind"] = RenderCell.String(kind.ToString()), ["sourceId"] = RenderCell.String(selectedSource.Id),
                            ["team"] = RenderCell.String(acquired.Value.Team ?? ""),
                            ["membershipObservedAt"] = RenderCell.String(acquired.Value.ObservedAt.ToString("O")),
                            ["message"] = RenderCell.String("Explicit native read; no Bench was created/selected and no work item was published."),
                        });
                    return 0;
                }

                var read = await store.ReadAsync(ct);
                if (!read.IsSuccess) return Error(read.Error.Message);
                Guid? selected = null;
                if (args.Bench is null)
                {
                    if (workspace is null) return Error("An explicit Bench or native context is required.", 2);
                    var context = await workspace.Context.ReadAsync(ct);
                    if (!context.IsSuccess) return Error(context.Error.Message);
                    selected = context.Value.BenchId;
                }
                var matches = read.Value.Benches.Where(bench => args.Bench is null ? bench.Id == selected
                    : bench.Name.Normalize(NormalizationForm.FormC).Equals(args.Bench.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase)
                        || bench.Id.ToString("D").Equals(args.Bench, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) return Error("Choose an existing unambiguous Bench. No default was created.");
                var target = matches[0];
                if (args.ExpectedRevision is { } expected && target.Revision != expected)
                    return Error("The captured Bench revision changed. Newer rule intent was preserved.");
                if (action == "rules")
                {
                    if (format == "ids")
                    { for (var index = 1; index <= target.Selectors.Length; index++) Console.WriteLine(index); return 0; }
                    Render("Shared Bench pins and rules", ["index", "kind", "definition"],
                        target.Selectors.Select((selector, index) => new Dictionary<string, RenderCell>
                        {
                            ["index"] = RenderCell.Integer(index + 1), ["kind"] = RenderCell.String(selector.Kind.ToString()),
                            ["definition"] = RenderCell.String(BenchCodec.SerializeSelectors([selector])),
                        }), BenchScope(target));
                    return 0;
                }
                if (action == "remove-rule")
                {
                    if (args.Rule is not { } index || index > target.Selectors.Length || args.ExpectedRevision is null)
                        return Error("Use --index and --expect-revision from a fresh 'bench rules' listing.", 2);
                    var saved = await store.SaveAsync(new(target.Id, target.Name, target.Selectors.RemoveAt(index - 1)), target.Revision, ct);
                    if (!saved.IsSuccess) return Error(saved.Error.Message);
                    Render("Bench rule removed", [], [], BenchScope(saved.Value));
                    return 0;
                }
                if (workspace is null) return Error("A current native connection is required to qualify the rule.");
                if (workspace.Connection.Provider.Value != "ado") return Error("Area, iteration and native query/backlog rule management is unavailable for this provider.");
                if (action == "add-rule")
                {
                    Query query;
                    if (args.Query is not null || args.Backlog is not null)
                    {
                        var choices = await workspace.DiscoverPlanningSourcesAsync(ct);
                        if (!choices.IsSuccess) return Error(choices.Error.Message);
                        var kind = args.Query is not null ? PlanningSourceKind.SavedQuery : PlanningSourceKind.Backlog;
                        var id = args.Query ?? args.Backlog!;
                        var source = choices.Value.Entries.SingleOrDefault(source => source.Kind == kind && !source.IsFolder && source.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                        if (source is null || source.RequiresTeam && choices.Value.Team is null) return Error("Choose a native source available in this exact connection/project/team.");
                        query = new(kind == PlanningSourceKind.SavedQuery ? QueryKind.SavedQuery : QueryKind.Backlog,
                            value: FieldValue.String(source.Id), planningTeam: choices.Value.Team);
                    }
                    else
                    {
                        var parts = new PlanningParts();
                        if (args.Area is { } area) parts.Areas.Add((Text(area), args.Exact));
                        if (args.Iteration is { } iteration)
                        {
                            var expression = Expression(iteration);
                            parts.HasIterationScope = true;
                            parts.Iterations.Add(expression.Text);
                            if (expression.IsRelative)
                            {
                                var metadata = await workspace.PlanningAsync(ct);
                                if (!metadata.IsSuccess) return Error(metadata.Error.Message);
                                parts.Team = metadata.Value.Metadata?.Team;
                                if (parts.Team is null) return Error("Refresh the selected native team's planning calendar before adding relative rules.");
                            }
                        }
                        if (args.Mine) parts.Owner = workspace.NativeActor;
                        query = parts.Build() ?? throw new ArgumentException("Choose --area, --iteration, --mine, --query or --backlog.");
                    }
                    var admitted = await factory!.AdmitSourceAsync(handle!, ct);
                    if (!admitted.IsSuccess) return Error(admitted.Error.Message);
                    var saved = await store.SaveAsync(new(target.Id, target.Name,
                        target.Selectors.Add(new(SelectorKind.Query, query: query, connection: workspace.Connection))), target.Revision, ct);
                    if (!saved.IsSuccess) return Error(saved.Error.Message);
                    Render("Shared Bench rule added", [], [], BenchScope(saved.Value));
                    return 0;
                }

                var candidates = target.Selectors.Select((selector, index) => (selector, index))
                    .Where(entry => entry.selector.Connection == workspace.Connection && entry.selector.Query is { } query && PlanningParts.HasPlanningField(query)).ToArray();
                if (action is "area-list" or "sprint-list")
                {
                    var rows = new List<Dictionary<string, RenderCell>>();
                    foreach (var entry in candidates)
                    {
                        if (!PlanningParts.TryRead(entry.selector.Query!, out var parts)) return Error("A retained planning rule has a noncanonical shape; it was not reinterpreted.");
                        foreach (var value in action == "area-list"
                            ? parts.Areas.Select(area => (area.Path, Mode: area.Exact ? "exact" : "under"))
                            : parts.Iterations.Select(expression => (Path: expression, Mode: parts.Team ?? "absolute")))
                            rows.Add(new() { ["rule"] = RenderCell.Integer(entry.index + 1), ["value"] = RenderCell.String(value.Path), ["mode"] = RenderCell.String(value.Mode) });
                    }
                    if (format == "ids") { foreach (var row in rows) Console.WriteLine(row["value"].DisplayText); return 0; }
                    Render("Saved planning filters", ["rule", "value", "mode"], rows, BenchScope(target));
                    return 0;
                }
                if (action is not ("area-add" or "area-remove" or "sprint-add" or "sprint-remove"))
                    return Error("Unknown native planning operation.", 2);
                var chosen = args.Rule is { } rule
                    ? candidates.Where(entry => entry.index == rule - 1).ToArray() : candidates;
                if (chosen.Length > 1 || args.Rule is not null && chosen.Length != 1)
                    return Error("Choose one planning rule with --rule from a fresh 'bench rules' listing; no rule was combined or replaced.");
                var current = new PlanningParts();
                var position = -1;
                if (chosen.Length == 1)
                {
                    position = chosen[0].index;
                    if (!PlanningParts.TryRead(chosen[0].selector.Query!, out current))
                        return Error("The retained planning rule has a noncanonical shape; no broader filter was fabricated.");
                }
                var valueText = Text(args.Value ?? "");
                if (action.StartsWith("area-", StringComparison.Ordinal))
                {
                    if (action == "area-remove" && !current.Areas.Any(area => area.Path.Equals(valueText, StringComparison.OrdinalIgnoreCase)))
                        return Error("That area is not pinned in the selected planning rule. Nothing changed.");
                    current.Areas.RemoveAll(area => area.Path.Equals(valueText, StringComparison.OrdinalIgnoreCase));
                    if (action == "area-add") current.Areas.Add((valueText, args.Exact));
                }
                else
                {
                    var expression = Expression(valueText);
                    if (action == "sprint-remove" && !current.Iterations.Any(value => value.Equals(expression.Text, StringComparison.OrdinalIgnoreCase)))
                        return Error("That iteration expression is not pinned in the selected planning rule. Nothing changed.");
                    if (action == "sprint-add" && expression.IsRelative)
                    {
                        var metadata = await workspace.PlanningAsync(ct);
                        if (!metadata.IsSuccess) return Error(metadata.Error.Message);
                        var team = metadata.Value.Metadata?.Team;
                        if (team is null || current.Iterations.Any(value => new IterationExpression(value).IsRelative) && current.Team != team)
                            return Error("The selected team's cached calendar is missing or differs from the retained rule. Existing team intent was preserved.");
                        current.Team = team;
                    }
                    current.HasIterationScope = true;
                    current.Iterations.RemoveAll(value => value.Equals(expression.Text, StringComparison.OrdinalIgnoreCase));
                    if (action == "sprint-add") current.Iterations.Add(expression.Text);
                }
                var next = current.Build();
                var selectors = position < 0 ? target.Selectors : target.Selectors.RemoveAt(position);
                if (next is not null) selectors = position < 0
                    ? selectors.Add(new(SelectorKind.Query, query: next, connection: workspace.Connection))
                    : selectors.Insert(position, new(SelectorKind.Query, query: next, connection: workspace.Connection));
                var result = await store.SaveAsync(new(target.Id, target.Name, selectors), target.Revision, ct);
                if (!result.IsSuccess) return Error(result.Error.Message);
                Render("Shared planning rule updated", [], [], BenchScope(result.Value));
                return 0;
            }
            finally { operation?.Dispose(); handle?.Dispose(); }
        }
    }

    private static string Text(string value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Any(char.IsControl)
        ? value : throw new ArgumentException("Use trimmed nonempty native paths or expressions without control characters.");
    private static IterationExpression Expression(string value)
        => new(value.Equals("current-sprint", StringComparison.OrdinalIgnoreCase) ? "@Current" : value);

    private sealed class PlanningParts
    {
        internal List<(string Path, bool Exact)> Areas { get; } = [];
        internal List<string> Iterations { get; } = [];
        internal bool HasIterationScope { get; set; }
        internal string? Team { get; set; }
        internal string? Owner { get; set; }
        internal Query? Build()
        {
            var conditions = new List<Query>();
            var areas = Areas.Select(area => new Query(area.Exact ? QueryKind.PathEqual : QueryKind.Under, "System.AreaPath", FieldValue.String(area.Path))).ToArray();
            if (areas.Length != 0) conditions.Add(areas.Length == 1 ? areas[0] : new(QueryKind.Or, children: areas));
            if (HasIterationScope) conditions.Add(new(QueryKind.Iteration, "System.IterationPath", FieldValue.Strings(Iterations),
                planningTeam: Iterations.Any(value => new IterationExpression(value).IsRelative) ? Team : null));
            if (Owner is not null) conditions.Add(new(QueryKind.Equal, "System.AssignedTo", FieldValue.Identity(Owner)));
            return conditions.Count == 0 ? null : conditions.Count == 1 ? conditions[0] : new(QueryKind.And, children: conditions);
        }
        internal static bool HasPlanningField(Query query) => query.Field is "System.AreaPath" or "System.IterationPath" or "System.AssignedTo"
            || query.Children.Any(HasPlanningField);
        internal static bool TryRead(Query query, out PlanningParts parts)
        {
            parts = new();
            var categories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var condition in query.Kind == QueryKind.And ? query.Children : [query])
            {
                var leaves = Leaves(condition).ToArray();
                var fields = leaves.Select(leaf => leaf.Field).Distinct(StringComparer.Ordinal).ToArray();
                if (fields.Length != 1 || fields[0] is not { } field || !categories.Add(field)) return false;
                foreach (var leaf in leaves)
                {
                    if (field == "System.AreaPath" && leaf.Kind is QueryKind.Equal or QueryKind.PathEqual or QueryKind.Under && leaf.Value?.Kind == FieldKind.Text)
                        parts.Areas.Add((leaf.Value.Text!, leaf.Kind != QueryKind.Under));
                    else if (field == "System.IterationPath" && leaf.Kind == QueryKind.Iteration)
                    {
                        parts.HasIterationScope = true;
                        if (parts.Team is not null && leaf.PlanningTeam is not null && parts.Team != leaf.PlanningTeam) return false;
                        parts.Team ??= leaf.PlanningTeam;
                        parts.Iterations.AddRange(leaf.Value!.Values);
                    }
                    else if (field == "System.IterationPath" && leaf.Kind is QueryKind.Equal or QueryKind.PathEqual
                        && leaf.Value?.Kind == FieldKind.Text && !leaf.Value.Text!.StartsWith('@'))
                    {
                        parts.HasIterationScope = true;
                        parts.Iterations.Add(leaf.Value.Text!);
                    }
                    else if (field == "System.AssignedTo" && leaves.Length == 1 && leaf.Kind == QueryKind.Equal && leaf.Value?.Kind == FieldKind.Identity)
                        parts.Owner = leaf.Value.Text;
                    else return false;
                }
            }
            return true;
        }
        private static IEnumerable<Query> Leaves(Query query) => query.Kind == QueryKind.Or
            ? query.Children.SelectMany(Leaves) : [query];
    }
}
