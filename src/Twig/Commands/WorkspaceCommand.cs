using Twig.Infrastructure.Telemetry;
using System.Diagnostics;
using System.Globalization;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Formatters;
using Twig.Infrastructure.Services;
using Twig.Rendering;
using Twig.RenderTree;
using static Twig.Infrastructure.Services.CoreHostContext;

namespace Twig.Commands;

/// <summary>Static cached Bench membership and protected draft debt, not a browser or live workspace.</summary>
internal sealed class WorkspaceCommand(
    RendererFactory rendererFactory,
    CoreWorkspaceFactory? factory = null,
    ITelemetryClient? telemetryClient = null)
{
    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat,
        bool all = false, bool noLive = false, bool refresh = false, CancellationToken ct = default,
        bool sprintLayout = false, bool flat = false, bool tree = false, string? view = null,
        bool includeBrowser = false, string? expectBinding = null, string? expectIdentity = null,
        string? bench = null, int? browserVersion = null, string? expectBench = null, string? expectSettings = null)
    {
        var started = Stopwatch.GetTimestamp();
        var format = OutputFormats.Normalize(outputFormat);
        var exit = 1;
        try
        {
            return exit = await ReadAsync();
        }
        catch (OperationCanceledException)
        {
            return exit = Error("Workspace read was cancelled; no draft was published.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return exit = Error("Workspace read did not complete: " + ex.Message);
        }
        finally
        {
            try { TelemetryHelper.TrackCommand(telemetryClient, "workspace", format ?? "invalid", exit, started); }
            catch (Exception) { Debug.WriteLine("Optional workspace telemetry failed; command outcome was preserved."); }
        }

        int Error(string message, int code = 1)
        {
            CommandError.Write(rendererFactory, Console.Error, format ?? OutputFormats.Default, message);
            return code;
        }

        async Task<int> ReadAsync()
        {
            if (format is null) return Error($"Unsupported output format. Use {OutputFormats.Describe()}.", 2);
            if (all || sprintLayout || tree || view is not null && !view.Equals("table", StringComparison.OrdinalIgnoreCase))
                return Error("Only the static Bench table is qualified. --all, sprint, --tree and --view tree are unsupported; use workspace [--view table].", 2);
            if (includeBrowser || browserVersion is not null || expectBinding is not null || expectIdentity is not null || expectSettings is not null)
                return Error("Browser envelopes and captured browser binding/identity/settings flags are unsupported by the Core workspace read.", 2);
            if (bench is not null && (string.IsNullOrWhiteSpace(bench) || bench != bench.Trim()))
                return Error("--bench requires an exact saved name or GUID.", 2);
            if (expectBench is not null && !Guid.TryParseExact(expectBench, "D", out _))
                return Error("--expect-bench requires a canonical Bench GUID.", 2);
            if (factory is null)
                return Error("Workspace reads require an explicitly initialized Core workspace; preserved legacy data was not adopted.");
            using var operation = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: refresh);
            var opened = await factory.OpenAsync(ct);
            if (!opened.IsSuccess) return Error(opened.Error.Message);
            using var handle = opened.Value;
            var workspace = handle.Workspace;
            var github = workspace.Connection.Provider.Value == "github";
            if (github && refresh)
                return Error("GitHub workspace refresh/context expansion is not qualified; explicitly refresh numeric items.");
            var context = await workspace.Context.ReadAsync(ct);
            if (!context.IsSuccess) return Error(context.Error.Message);
            var read = await workspace.BenchSnapshotAsync(ct);
            if (!read.IsSuccess) return Error(read.Error.Message);
            var snapshot = read.Value;
            var matches = snapshot.Benches.Where(value => bench is null ? value.Id == context.Value.BenchId
                : value.Name.Equals(bench, StringComparison.OrdinalIgnoreCase)
                    || value.Id.ToString("D").Equals(bench, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                return Error("The Bench is missing or ambiguous. Use 'twig bench list', then 'twig bench switch <name>' or 'workspace --bench <name-or-guid>'; no default was created.");
            var selected = matches[0];
            if (expectBench is not null && Guid.Parse(expectBench) != selected.Id)
                return Error("The captured Bench identity changed; no replacement under the same name was adopted.");
            if (refresh && selected.Id != context.Value.BenchId)
                return Error("--refresh can pull only the current Bench. Switch it explicitly first; this read never changes selection.");
            if (refresh)
            {
                var depths = factory.TreeContextDepths;
                var pulled = await workspace.PullBenchAsync(selected.Id.ToString("D"), includeActiveContext: false,
                    depths.Up, depths.Down, depths.Sideways, ct);
                if (!pulled.IsSuccess) return Error(pulled.Error.Message);
                read = await workspace.BenchSnapshotAsync(ct);
                if (!read.IsSuccess) return Error(read.Error.Message);
                snapshot = read.Value;
            }
            // Schema is not persisted in StoreSnapshot. Only provider metadata is acquired on an ordinary read.
            var schema = await workspace.SchemaAsync(ct);
            if (!schema.IsSuccess) return Error(schema.Error.Message);
            var planning = await workspace.PlanningAsync(ct);
            if (!planning.IsSuccess) return Error(planning.Error.Message);
            var memberships = await workspace.MembershipsAsync(ct);
            if (!memberships.IsSuccess) return Error(memberships.Error.Message);
            schema = Result<ProcessSchema>.Ok(schema.Value with { Planning = planning.Value.Metadata, Memberships = memberships.Value });
            var evaluationTime = DateTimeOffset.UtcNow;
            if (KnownLocalKeys(snapshot).GroupBy(LocalAlias).Any(group => group.Count() != 1))
                return Error("Local aliases are ambiguous; no workspace identity was guessed.");
            ItemKey Resolve(ItemKey key) => key.IsLocal
                ? snapshot.Mappings.SingleOrDefault(mapping => mapping.Local == key)?.Remote ?? key : key;
            bool Qualified(ItemKey key) => CoreWorkspace.DisplayId(workspace.Connection, snapshot, key).IsSuccess;
            var selectors = selected.Selectors.Select(selector => selector.Item is null ? selector
                : new Selector(selector.Kind, Resolve(selector.Item), hierarchyRelation: selector.HierarchyRelation)).ToArray();
            if (selectors.Any(selector => selector.Item is { } key && !Qualified(key))
                || snapshot.Items.Any(item => !Qualified(item.Key))
                || snapshot.Drafts.Any(draft => !Qualified(Resolve(draft.Key))))
                return Error("The retained scope contains an unqualified native identity; no integer identity or broader scope was fabricated.");
            var projectedBench = new Bench(selected.Id, selected.Name, selectors);
            var projection = new StoreSnapshot(snapshot.Version, snapshot.Items,
                snapshot.Relationships.Select(edge => edge with { From = Resolve(edge.From), To = Resolve(edge.To) }),
                snapshot.Drafts, snapshot.Proposals, snapshot.Journal, snapshot.Mappings, snapshot.Benches);
            var resolved = Core.Core.ResolveBench(projectedBench, projection, schema.Value, evaluationTime);
            if (!resolved.IsSuccess) return Error(resolved.Error.Message);
            var rules = await workspace.ViewRulesAsync(ct);
            if (!rules.IsSuccess) return Error(rules.Error.Message);
            var candidates = resolved.Value.Concat(snapshot.Drafts.Select(draft => Resolve(draft.Key))).Distinct().ToArray();
            var visible = Core.Core.ApplyViewExclusions(candidates, rules.Value.Exclusions, projection, schema.Value, evaluationTime);
            if (!visible.IsSuccess) return Error(visible.Error.Message);
            var keys = visible.Value.Concat(snapshot.Drafts.Select(draft => Resolve(draft.Key))).Distinct().ToArray();
            var rows = new List<RenderRow>();
            foreach (var key in keys)
            {
                var debts = snapshot.Drafts.Where(draft => Resolve(draft.Key) == key).ToArray();
                if (debts.Length > 1)
                    return Error("Multiple retained drafts resolve to one native item; no protected debt was omitted or combined.");
                var draft = debts.SingleOrDefault();
                var cached = snapshot.Items.SingleOrDefault(item => item.Key == key);
                var baseline = cached ?? draft?.Baseline;
                if (baseline is null && draft is null)
                    return Error("A scoped item is unavailable locally; use an explicit --refresh on the current Bench.");
                if (!key.IsLocal && baseline is null)
                    return Error("A permanently mapped draft has no cached native observation; refresh the current Bench explicitly.");
                var proposed = draft?.Changes;
                var localKey = key.IsLocal ? key : snapshot.Mappings.SingleOrDefault(mapping => mapping.Remote == key)?.Local;
                var type = schema.Value.Types.SingleOrDefault(value => value.Reference == (draft?.Type ?? baseline!.Type));
                if (type is null)
                    return Error("A scoped item's discovered type is unavailable; no state field or process vocabulary was guessed.");
                string Text(string field) => (proposed?.GetValueOrDefault(field)
                    ?? baseline?.Fields.GetValueOrDefault(field))?.Text ?? "";
                rows.Add(new("workspaceItem", new Dictionary<string, RenderCell>
                {
                    ["id"] = RenderCell.Integer(CoreWorkspace.DisplayId(workspace.Connection, snapshot, key).Value),
                    ["localId"] = localKey is not null ? RenderCell.String(localKey.LocalId.ToString("D")) : new("", new RenderValue.Null()),
                    ["title"] = RenderCell.String(Text(github ? Twig.Providers.GitHub.GitHubProvider.Title : "System.Title")),
                    ["state"] = RenderCell.String(Text(type.StateField)),
                    ["type"] = RenderCell.String(draft?.Type ?? baseline!.Type),
                    ["source"] = RenderCell.String(draft is null ? "cached" : key.IsLocal ? "local-draft"
                        : cached is null ? "draft-baseline+proposed" : "cached+proposed"),
                    ["benchMember"] = RenderCell.Boolean(resolved.Value.Contains(key)),
                    ["hasDraft"] = RenderCell.Boolean(draft is not null),
                    ["isExcluded"] = RenderCell.Boolean(!visible.Value.Contains(key)),
                    ["cachedFields"] = Fields(baseline?.Fields),
                    ["proposedFields"] = Fields(proposed),
                }));
            }
            var table = new RenderNode.Table("Cached Bench items and protected drafts",
                [new("id", "ID"), new("title", "Title"), new("state", "State"), new("type", "Type"),
                    new("source", "Source"), new("proposedFields", "Proposed fields")], rows);
            rendererFactory.GetRenderer(format).Render(new RenderTree.RenderTree([new RenderNode.Document("workspace",
                [
                    new("projection", new RenderNode.KeyValue("projection", RenderCell.String("core-bench-workspace-v1"))),
                    new("benchId", new RenderNode.KeyValue("benchId", RenderCell.String(selected.Id.ToString("D")))),
                    new("benchName", new RenderNode.KeyValue("benchName", RenderCell.String(selected.Name))),
                    new("scope", new RenderNode.KeyValue("scope", RenderCell.String("Cached selector membership plus all protected drafts; not an exhaustive provider query."))),
                    new("schema", new RenderNode.KeyValue("schema", RenderCell.String("Provider schema metadata acquired; cached items have no freshness guarantee."))),
                    new("items", table),
                ])]));
            return 0;
        }
    }

    private static RenderCell Fields(FieldValues? fields) => new(string.Join(", ", fields?.Keys ?? []),
        new RenderValue.Array((fields ?? FieldValues.Empty).Select(field => new RenderCell(field.Key,
            new RenderValue.Object(new Dictionary<string, RenderCell>
            {
                ["reference"] = RenderCell.String(field.Key),
                ["kind"] = RenderCell.String(field.Value.Kind.ToString()),
                ["value"] = CoreCommandAdapter.Cell(field.Value),
            }))).ToArray()));
}
