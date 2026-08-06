using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig process layout &lt;type&gt;</c>: reads the server-defined work item
/// form layout — tabs, boxes, and ordered fields — and renders it, optionally writing it
/// to a file with <c>--out</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the visible half of wayfinder-1.0 ticket 1004. The fetch-and-parse behind it
/// (<see cref="IFormLayoutProvider"/>) is production code for the server-driven 1.0 editor
/// and ships regardless; this command exists so the layout can be pulled from inside a
/// work-data boundary, reviewed, and handed out as a structural file.
/// </para>
/// <para>
/// <b>It reads structure only.</b> No work item values are fetched or written — the layout
/// endpoint returns field names and arrangement, never field contents. That is what makes
/// the exported file safe to review and pass on.
/// </para>
/// <para>
/// Both experiences per the map's standing rule: <c>--out</c> is script-shaped and works
/// with every format, and the same content renders to stdout when <c>--out</c> is omitted.
/// The file receives the chosen format verbatim, so <c>-o json --out layout.json</c> is a
/// machine artifact while <c>--out layout.txt</c> is a readable one.
/// </para>
/// <para>
/// Internal rather than public, deliberately: <see cref="FormLayout"/> is still under
/// design (ticket 1003's editor is not built yet), and freezing it into twig's public
/// API surface now would make the shape harder to correct once the renderer exists.
/// </para>
/// </remarks>
internal sealed class ProcessLayoutCommand(
    IFormLayoutProvider formLayoutProvider,
    OutputFormatterFactory formatterFactory,
    RendererFactory rendererFactory,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>
    /// Executes <c>twig process layout &lt;type&gt; [--out path]</c>.
    /// </summary>
    /// <param name="typeName">Work item type display name (e.g. <c>Bug</c>, <c>Task</c>).</param>
    /// <param name="outPath">Optional file to write the rendered layout to.</param>
    public async Task<int> ExecuteAsync(
        string typeName,
        string? outPath = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (string.IsNullOrWhiteSpace(typeName))
        {
            _stderr.WriteLine(fmt.FormatError("A work item type is required. Try 'twig process' to list types."));
            return 1;
        }

        var layout = await formLayoutProvider.GetFormLayoutAsync(typeName, ct);

        if (layout is null)
        {
            // Deliberately distinguished from an empty layout. Ticket 1004 carries an open
            // question — whether stock (non-inherited) processes serve a layout at all —
            // and collapsing "no layout served" into "layout with no tabs" would hide it.
            _stderr.WriteLine(fmt.FormatError(
                $"No form layout available for type '{typeName}'. The type may not exist in this " +
                "project, or this project's process does not serve a layout."));
            return 1;
        }

        var tree = BuildLayoutTree(layout);

        if (outPath is null)
        {
            rendererFactory.GetRenderer(outputFormat).Render(tree);
            return 0;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var writer = new StreamWriter(outPath, append: false);
            rendererFactory.GetRenderer(outputFormat, writer).Render(tree);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _stderr.WriteLine(fmt.FormatError($"Could not write '{outPath}': {ex.Message}"));
            return 1;
        }

        // Confirmation goes to stderr so `--out` stays silent on stdout and composes in
        // scripts; the file is the output.
        _stderr.WriteLine($"Wrote form layout for '{typeName}' to {outPath}");
        return 0;
    }

    // ─────────────────────────────────────────────────────────────
    //  RenderTree builder
    // ─────────────────────────────────────────────────────────────

    /// <remarks>
    /// Human output merges the columns into a single top-to-bottom list, because a
    /// terminal is one column wide. Machine output keeps them as their own level, so a
    /// consumer that wants side-by-side placement still has the fact. Merging is the
    /// renderer's choice; the parse no longer makes it — see <c>FormLayout</c>.
    /// </remarks>
    private static RenderTree.RenderTree BuildLayoutTree(FormLayout layout)
    {
        var humanLines = new List<RenderNode>();
        var pageBranches = new List<RenderTreeBranch>(layout.Pages.Count);

        foreach (var page in layout.Pages)
        {
            var visibility = page.Visible ? string.Empty : " (hidden)";
            humanLines.Add(new RenderNode.Text($"{page.Label} [{page.PageType}]{visibility}"));

            var sectionBranches = new List<RenderTreeBranch>(page.Sections.Count);
            foreach (var section in page.Sections)
            {
                var groupBranches = new List<RenderTreeBranch>(section.Groups.Count);
                foreach (var group in section.Groups)
                {
                    // Human: columns collapse, so groups print at one level under the tab.
                    humanLines.Add(new RenderNode.Text($"  {group.Label}"));

                    var controlBranches = new List<RenderTreeBranch>(group.Controls.Count);
                    foreach (var control in group.Controls)
                    {
                        var flags = string.Concat(
                            control.ReadOnly ? " (read-only)" : string.Empty,
                            control.Visible ? string.Empty : " (hidden)");
                        humanLines.Add(new RenderNode.Text(
                            $"    {control.Label,-28} {control.Id}{flags}"));

                        controlBranches.Add(new RenderTreeBranch(
                            new RenderRow("control", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                            {
                                ["id"] = RenderCell.String(control.Id),
                                ["label"] = RenderCell.String(control.Label),
                                ["controlType"] = RenderCell.String(control.ControlType),
                                ["readOnly"] = RenderCell.Boolean(control.ReadOnly),
                                ["visible"] = RenderCell.Boolean(control.Visible),
                                ["isContribution"] = RenderCell.Boolean(control.IsContribution),
                            }),
                            []));
                    }

                    groupBranches.Add(new RenderTreeBranch(
                        new RenderRow("group", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                        {
                            ["id"] = RenderCell.String(group.Id),
                            ["label"] = RenderCell.String(group.Label),
                            ["visible"] = RenderCell.Boolean(group.Visible),
                            ["isContribution"] = RenderCell.Boolean(group.IsContribution),
                        }),
                        controlBranches));
                }

                sectionBranches.Add(new RenderTreeBranch(
                    new RenderRow("section", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                    {
                        ["id"] = RenderCell.String(section.Id),
                    }),
                    groupBranches));
            }

            pageBranches.Add(new RenderTreeBranch(
                new RenderRow("page", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["id"] = RenderCell.String(page.Id),
                    ["label"] = RenderCell.String(page.Label),
                    ["pageType"] = RenderCell.String(page.PageType),
                    ["visible"] = RenderCell.Boolean(page.Visible),
                    ["isContribution"] = RenderCell.Boolean(page.IsContribution),
                }),
                sectionBranches));
        }

        var root = new RenderTreeBranch(
            new RenderRow("formLayout", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["workItemType"] = RenderCell.String(layout.WorkItemTypeReferenceName),
                ["processId"] = RenderCell.String(layout.ProcessId),
            }),
            pageBranches);

        var doc = new RenderNode.Document(null, [
            new DocumentField(
                Key: "layout",
                Node: new RenderNode.TreeView(root),
                HumanOverride: new RenderNode.Section(null, humanLines)),
            new DocumentField(
                Key: "pageCount",
                Node: new RenderNode.KeyValue("pageCount", RenderCell.Integer(layout.Pages.Count)),
                Audience: RenderAudience.MachineOnly),
        ]);

        return new RenderTree.RenderTree([doc]);
    }
}
