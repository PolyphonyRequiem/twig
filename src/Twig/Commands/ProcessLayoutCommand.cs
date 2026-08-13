using System.Diagnostics;
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
/// Internal rather than public, deliberately: nothing outside twig constructs a command,
/// and the command shell is an application detail rather than a contract.
/// </para>
/// <para>
/// 🔴 <b>This paragraph used to justify the command's visibility by
/// <see cref="FormLayout"/>'s.</b> It read: <i>"<see cref="FormLayout"/> is still under
/// design ... and freezing it into twig's public API surface now would make the shape
/// harder to correct once the renderer exists."</i> That was TRUE when written
/// (<c>0c6b45f8</c>, 2026-08-06) and went stale three days later: AB#155
/// (<c>25d9f59d</c>, 2026-08-09, <c>wayfinder-detail-projection</c> ticket 0003) promoted
/// <see cref="FormLayout"/> to <c>public</c> deliberately, so an external host could receive
/// one from <see cref="Twig.Domain.Projections.WorkItemDetailProjector.Project"/>.
/// Corrected under AB#253, which ruled that promotion correct and narrowed the spec clause
/// that contradicted it. The conclusion was never affected: this command stays internal on
/// its own merits. Kept as a note rather than deleted because the stale sentence is exactly
/// how the conflict AB#253 resolved stayed invisible for a week.
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
    /// <param name="typeName">
    /// Work item type display name (<c>Task</c>) or process reference name
    /// (<c>Niflheim.Task</c>). Both are accepted, matching the sibling
    /// <c>process description</c> verb (AB#247).
    /// </param>
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

        var result = await formLayoutProvider.GetFormLayoutAsync(typeName, ct);

        // 🔴 Three arms, matched exhaustively, because the provider reports three distinct
        // facts and this command previously collapsed one of them into a crash. See
        // FormLayoutResult's remarks for why an empty layout is none of them.
        FormLayout layout;
        switch (result)
        {
            case FormLayoutResult.Served served:
                layout = served.Layout;
                break;

            // 🔴 Degrades rather than failing (AB#247). A locked type answers the layout
            // route with 400 VS403115, and this command used to exit 1 with the raw server
            // error on stderr and no output at all. The sibling `process description` reports
            // the same answer as `unfetched: formLayout` and carries on; this is the same
            // honesty at the single-type surface, with a non-zero exit because the caller
            // asked for exactly this type and did not get it.
            case FormLayoutResult.Locked locked:
                _stderr.WriteLine(fmt.FormatError(
                    $"No form layout available for type '{locked.TypeReferenceName}': the type is " +
                    "locked by the process, so the server does not serve its layout. " +
                    "'twig process description' reports the same types with 'unfetched: formLayout'."));
                return 1;

            case FormLayoutResult.Unavailable:
                // Deliberately distinguished from an empty layout. Ticket 1004 carries an open
                // question — whether stock (non-inherited) processes serve a layout at all —
                // and collapsing "no layout served" into "layout with no tabs" would hide it.
                _stderr.WriteLine(fmt.FormatError(
                    $"No form layout available for type '{typeName}'. The type may not exist in this " +
                    "project, or this project's process does not serve a layout."));
                return 1;

            default:
                throw new UnreachableException(
                    $"Unhandled FormLayoutResult: {result.GetType().Name}");
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

        // 🔴 The server's system controls — state, reason, assigned-to, area and iteration
        // path, history, links, attachments. They were deserialized and discarded before
        // AB#247, so this command's rendering of "the form" omitted every control a person
        // sees at the top of a work item.
        var systemControlBranches = new List<RenderTreeBranch>(layout.SystemControls.Count);
        if (layout.SystemControls.Count > 0)
        {
            humanLines.Add(new RenderNode.Text("System controls"));

            foreach (var control in layout.SystemControls)
            {
                var flags = string.Concat(
                    control.ReadOnly ? " (read-only)" : string.Empty,
                    control.Visible ? string.Empty : " (hidden)");
                humanLines.Add(new RenderNode.Text(
                    $"  {control.Label,-28} {control.Id}{flags}"));

                systemControlBranches.Add(new RenderTreeBranch(
                    new RenderRow("systemControl", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
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
        }

        var root = new RenderTreeBranch(
            new RenderRow("formLayout", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["workItemType"] = RenderCell.String(layout.WorkItemTypeReferenceName),
                ["processId"] = RenderCell.String(layout.ProcessId),
            }),
            // 🔴 System controls are emitted as their own branch ALONGSIDE the pages, not
            // merged into them (AB#247). That mirrors the wire shape — the server returns
            // `systemControls` as a sibling of `pages`, precisely because these controls sit
            // outside the tab structure — and merging them into a page would invent a
            // placement the server never stated.
            [.. pageBranches, .. systemControlBranches]);

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
