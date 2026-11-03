using System.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;
using Twig.Core;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Twig.Infrastructure.Telemetry;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

internal sealed class WorkspaceExclusionCommand(CoreWorkspaceFactory factory, RendererFactory renderers,
    ITelemetryClient? telemetry = null)
{
    internal async Task<int> ExecuteAsync(string output, CancellationToken ct, int? add = null, int? remove = null, bool clear = false)
    {
        var started = Stopwatch.GetTimestamp();
        var format = OutputFormats.Normalize(output);
        var exit = 1;
        try { return exit = await Execute(); }
        catch (OperationCanceledException) { return exit = Error("Workspace exclusions were cancelled; no remote item was changed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return exit = Error("Workspace exclusions could not complete: " + ex.Message); }
        finally
        {
            try { TelemetryHelper.TrackCommand(telemetry, add is null ? "workspace exclusions" : "workspace exclude",
                format ?? "invalid", exit, started); }
            catch (Exception) { Debug.WriteLine("Optional exclusion telemetry failed; command outcome was preserved."); }
        }
        int Error(string message, int code = 1)
        {
            CommandError.Write(renderers, Console.Error, format ?? OutputFormats.Default, message);
            return code;
        }
        async Task<int> Execute()
        {
            if (format is null) return Error($"Unsupported output format. Use {OutputFormats.Describe()}.", 2);
            if (new[] { add is not null, remove is not null, clear }.Count(value => value) > 1)
                return Error("Choose one exclusion change: add, --remove, or --clear.", 2);
            if (add is <= 0 || remove is <= 0) return Error("Cannot exclude seeds or invalid IDs. Provide a positive work item ID.", 2);
            if (format == "ids" && (add is not null || remove is not null || clear))
                return Error("IDs output is available only for listing exclusions.", 2);
            using var operation = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: add is not null || remove is not null || clear);
            var opened = await factory.OpenAsync(ct);
            if (!opened.IsSuccess) return Error(opened.Error.Message);
            using var handle = opened.Value;
            var workspace = handle.Workspace;
            var captured = await workspace.ViewRulesAsync(ct);
            if (!captured.IsSuccess) return Error(captured.Error.Message);
            var rules = captured.Value.Exclusions;
            if (add is not null || remove is not null || clear)
            {
                var next = rules;
                if (clear) next = [];
                else
                {
                    var locator = add ?? remove ?? throw new InvalidOperationException("The exclusion target is missing.");
                    var target = await workspace.ResolveTargetAsync(locator, false, ct);
                    if (!target.IsSuccess) return Error(target.Error.Message);
                    var found = rules.Any(rule => rule.Kind == SelectorKind.Pin && rule.Item == target.Value);
                    if (add is not null && !found) next = rules.Add(new(SelectorKind.Pin, target.Value, connection: workspace.Connection));
                    if (remove is not null && found) next = rules.Where(rule => rule.Kind != SelectorKind.Pin || rule.Item != target.Value).ToImmutableArray();
                }
                if (!next.SequenceEqual(rules))
                {
                    var saved = await workspace.SaveViewRulesAsync(captured.Value.Revision, next, ct);
                    if (!saved.IsSuccess) return Error(saved.Error.Message);
                }
                renderers.GetRenderer(format).Render(new RenderTree.RenderTree([new RenderNode.Record("workspaceExclusionsChanged",
                    new Dictionary<string, RenderCell>
                    {
                        ["count"] = RenderCell.Integer(next.Length),
                        ["message"] = RenderCell.String("Workspace-view exclusions saved. Shared Bench pins, remote work items and protected drafts were unchanged."),
                    })]));
                return 0;
            }
            var snapshot = await workspace.SnapshotAsync(ct);
            if (!snapshot.IsSuccess) return Error(snapshot.Error.Message);
            var rows = new List<RenderRow>();
            foreach (var rule in rules)
            {
                var id = rule.Item is { } item ? CoreWorkspace.DisplayId(workspace.Connection, snapshot.Value, item)
                    : Result<int>.Fail(ErrorCode.Unsupported, "This view rule names a query, not one work item.");
                if (format == "ids")
                {
                    if (rule.Kind != SelectorKind.Pin || !id.IsSuccess)
                        return Error("IDs output cannot represent tree/query rules or unobserved native numbers.", 2);
                    continue;
                }
                rows.Add(new("workspaceExclusion", new Dictionary<string, RenderCell>
                {
                    ["id"] = id.IsSuccess ? RenderCell.Integer(id.Value) : RenderCell.String(rule.Item?.NativeId ?? ""),
                    ["kind"] = RenderCell.String(rule.Kind.ToString()),
                    ["field"] = RenderCell.String(rule.Query?.Field ?? ""),
                    ["relation"] = RenderCell.String(rule.HierarchyRelation ?? ""),
                }));
            }
            if (format == "ids")
                foreach (var rule in rules) Console.WriteLine(CoreWorkspace.DisplayId(workspace.Connection, snapshot.Value, rule.Item!).Value.ToString(CultureInfo.InvariantCulture));
            else renderers.GetRenderer(format).Render(new RenderTree.RenderTree([new RenderNode.Table("Workspace-view exclusions",
                [new("id", "ID"), new("kind", "Rule"), new("field", "Field"), new("relation", "Hierarchy")], rows)]));
            return 0;
        }
    }
}
