using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements the <c>twig workspace sprint</c> command group for managing sprint iteration subscriptions:
/// <c>add</c>, <c>remove</c>, and <c>list</c>.
/// Sprint expressions can be relative (@current, @current±N) or absolute iteration paths.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// add/remove emit "sprintAdded"/"sprintRemoved" records; list emits a "sprintList" record with
/// an entries array on machine formats and streamed lines on human format.
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error formatting.
/// </remarks>
public sealed class SprintCommand(
    TwigConfiguration config,
    TwigPaths paths,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Add a sprint iteration expression to the workspace configuration.</summary>
    public async Task<int> AddAsync(string expression, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        _ = ct;
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (string.IsNullOrWhiteSpace(expression))
        {
            Console.Error.WriteLine(fmt.FormatError("Sprint expression cannot be empty."));
            return 2;
        }

        config.Workspace.Sprints ??= [];

        var existing = config.Workspace.Sprints
            .FindIndex(e => string.Equals(e.Expression, expression, StringComparison.OrdinalIgnoreCase));

        if (existing >= 0)
        {
            Console.Error.WriteLine(fmt.FormatError($"Sprint expression '{expression}' is already configured."));
            return 1;
        }

        var entry = new SprintEntry { Expression = expression };
        config.Workspace.Sprints.Add(entry);
        await config.SaveSplitAsync(paths, ct);

        RenderOutcome("sprintAdded", $"Added sprint expression '{expression}'.", expression, outputFormat, Severity.Success);
        return 0;
    }

    /// <summary>Remove a sprint iteration expression from the workspace configuration.</summary>
    public async Task<int> RemoveAsync(string expression, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (config.Workspace.Sprints is not { Count: > 0 })
        {
            Console.Error.WriteLine(fmt.FormatError("No sprint expressions configured."));
            return 1;
        }

        var index = config.Workspace.Sprints
            .FindIndex(e => string.Equals(e.Expression, expression, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            Console.Error.WriteLine(fmt.FormatError($"Sprint expression '{expression}' is not configured."));
            return 1;
        }

        config.Workspace.Sprints.RemoveAt(index);
        await config.SaveSplitAsync(paths, ct);

        RenderOutcome("sprintRemoved", $"Removed sprint expression '{expression}'.", expression, outputFormat, Severity.Success);
        return 0;
    }

    /// <summary>List all configured sprint iteration expressions.</summary>
    public Task<int> ListAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        _ = ct;
        var entries = config.Workspace.Sprints;
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();

        if (lower is "json" or "json-full" or "json-compact" or "ids")
        {
            var list = entries ?? new List<SprintEntry>();
            var columns = new List<RenderColumn> { new("expression", "Expression") };
            var rows = new List<RenderRow>(list.Count);
            foreach (var e in list)
            {
                var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["expression"] = RenderCell.String(e.Expression),
                };
                rows.Add(new RenderRow("sprint", cells));
            }

            var fields = new List<DocumentField>(2)
            {
                new("count", new RenderNode.KeyValue("count", RenderCell.Integer(rows.Count))),
                new("entries", new RenderNode.Table(null, columns, rows)),
            };
            var document = new RenderNode.Document("sprintList", fields);
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { (RenderNode)document }));
            return Task.FromResult(0);
        }

        if (entries is not { Count: > 0 })
        {
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[]
            {
                (RenderNode)new RenderNode.Text("No sprint expressions configured. Use 'twig workspace sprint add <expr>' to configure.", Severity.Info),
            }));
            return Task.FromResult(0);
        }

        var nodes = new List<RenderNode>();
        foreach (var entry in entries)
            nodes.Add(new RenderNode.Text(entry.Expression, Severity.Info));
        nodes.Add(new RenderNode.Text($"{entries.Count} sprint expression(s) configured.", Severity.Info));
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(nodes));
        return Task.FromResult(0);
    }

    private void RenderOutcome(string kind, string message, string expression, string outputFormat, Severity severity)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                new RenderNode.Record(kind, new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["expression"] = RenderCell.String(expression),
                    ["message"] = RenderCell.String(message),
                }),
            _ => new RenderNode.Text(message, severity),
        };
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }
}
