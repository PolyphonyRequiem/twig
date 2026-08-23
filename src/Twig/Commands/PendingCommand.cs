using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>
/// CLI adapter for <c>twig pending</c>: dumps every staged pending change in exact
/// staging order via <see cref="IPendingChangeReader"/>. No business logic and no ADO
/// mutation — this command is a strict read-only projection.
/// </summary>
/// <remarks>
/// The raw <see cref="PendingChangeDetail.OldValue"/>/<see cref="PendingChangeDetail.NewValue"/>
/// strings are preserved character-for-character in stdout, deliberately not routed
/// through <see cref="Twig.Domain.Interfaces.ITelemetryClient"/> — customer field content
/// is command output, never telemetry.
/// </remarks>
public sealed class PendingCommand(
    IPendingChangeReader reader,
    RendererFactory? rendererFactory = null,
    TextWriter? stdout = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();
    private readonly TextWriter _stdout = stdout ?? Console.Out;

    /// <summary>List raw staged pending changes in exact staging order. Always exit 0.</summary>
    public async Task<int> ExecuteAsync(string outputFormat, CancellationToken ct)
    {
        var rows = await reader.GetAllChangesAsync(ct);
        Render(rows, outputFormat);
        return 0;
    }

    private void Render(IReadOnlyList<PendingChangeDetail> rows, string outputFormat)
    {
        var fields = new List<DocumentField>
        {
            new("count", new RenderNode.KeyValue("count", RenderCell.Integer(rows.Count))),
            new(
                PendingChangeRenderer.PendingChangesKey,
                new RenderNode.KeyValue(
                    PendingChangeRenderer.PendingChangesKey,
                    PendingChangeRenderer.PendingChangesCell(rows))),
        };
        var doc = new RenderNode.Document("pending", fields);

        var human = BuildHumanNode(rows);
        var tree = new RenderTree.RenderTree([WrapHumanOverride(doc, human, outputFormat)]);
        _rendererFactory.GetRenderer(outputFormat, _stdout).Render(tree);
    }

    private static RenderNode BuildHumanNode(IReadOnlyList<PendingChangeDetail> rows)
    {
        if (rows.Count == 0)
            return new RenderNode.Text("No pending changes.");

        var lines = new List<RenderNode> { new RenderNode.Text($"{rows.Count} pending change(s):") };
        foreach (var row in rows)
            lines.Add(new RenderNode.Text("  " + PendingChangeRenderer.HumanLine(row)));
        return new RenderNode.Section(null, lines);
    }

    private static RenderNode WrapHumanOverride(RenderNode.Document machine, RenderNode human, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        return lower is "json" or "json-full" or "json-compact" or "ids" or "minimal"
            ? machine
            : human;
    }
}
