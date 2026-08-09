using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;

namespace Twig.DetailHost;

/// <summary>
/// A frame the CALLER owns. Twig contributes no part of it.
/// </summary>
/// <remarks>
/// <para>
/// Everything in this file is a host decision, and each one is a decision Twig could have
/// taken away by baking it into the projection: the pane's width and height, the border
/// glyphs, column merging, which rows are drawn at all, how a long value is abbreviated
/// for display versus expanded, scrolling, and the selection cursor. The projection is
/// consulted for facts and never for presentation.
/// </para>
/// <para>
/// Rendering is to a plain <see cref="string"/> so the probe has no terminal dependency
/// whatsoever — not even <c>System.Console</c> semantics — and can be asserted against.
/// </para>
/// </remarks>
internal sealed class HostPane
{
    private readonly int _width;
    private readonly int _height;
    private readonly List<Row> _rows = [];

    private int _scrollOffset;
    private int _selectedIndex;

    /// <summary>Host policy: which control types this pane knows how to draw.</summary>
    private static readonly HashSet<string> SupportedControlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "FieldControl", "HtmlFieldControl", "IdentityControl", "WorkItemClassificationControl",
    };

    internal HostPane(int width, int height) => (_width, _height) = (width, height);

    private sealed record Row(int Indent, string Text, bool Selectable, string? FullValue);

    /// <summary>
    /// Walks the document and appends host-owned rows. The walk is the whole point: the
    /// host reads structure and flags and decides, control by control, what to draw.
    /// </summary>
    internal void Load(WorkItemDetailDocument document, WorkItemTypeAppearance appearance)
    {
        _rows.Clear();

        // Appearance arrived SEPARATELY. The host asked for it; a host that did not want
        // Twig's styling opinion simply would not have.
        Add(0, $"[{appearance.Name}] #{document.WorkItemId} rev {document.Revision}", false, null);
        Add(0, $"type {document.WorkItemTypeReferenceName}  process {document.ProcessId}", false, null);
        Add(0, string.Empty, false, null);

        foreach (var page in document.Pages)
        {
            if (!page.CarriesFieldControls)
            {
                // Truthful treatment of a surface this host cannot draw: name it, say why,
                // and move on. The projection carried it flagged so this choice was available.
                Add(0, $"# {page.Label}  (server-rendered '{page.PageType}' page — not shown here)", true, null);
                continue;
            }

            Add(0, $"# {page.Label}", true, null);

            // Column merging is a RENDERING decision. This pane is narrow, so it walks
            // AllGroups and concatenates; a wider host would read Sections instead.
            foreach (var group in page.AllGroups)
            {
                if (!group.Visible) continue;

                var groupLabel = group.IsContribution
                    ? $"{group.Label}  (add-in)"
                    : group.Label;
                Add(1, $"{groupLabel}", true, null);

                if (group.IsContribution && group.Controls.Count == 0)
                {
                    Add(2, "· contributed content, not supplied by the layout", false, null);
                    continue;
                }

                foreach (var control in group.Controls)
                {
                    // Host policy: this pane hides what the server hid. Reported, not enforced —
                    // a diff pane that wanted to show hidden fields could ignore this line.
                    if (!control.Visible) continue;

                    if (control.IsContribution)
                    {
                        Add(2, $"{control.Label}: <add-in '{control.Id}' — this pane cannot draw it>", true, null);
                        continue;
                    }

                    if (!SupportedControlTypes.Contains(control.ControlType))
                    {
                        // Truthful unsupported-control treatment: the verbatim server string
                        // is still here to name, precisely because there is no closed enum.
                        Add(2, $"{control.Label}: <unsupported control type '{control.ControlType}'>", true, null);
                        continue;
                    }

                    var value = control.Value!;
                    var readOnlyMark = control.ReadOnly ? " (read-only)" : string.Empty;
                    var rendered = value.State switch
                    {
                        // Short form when there is one; the full value stays reachable for expansion.
                        DetailFieldState.HasValue => value.Short ?? value.Full!,
                        DetailFieldState.EmptyOnServer => "—",
                        DetailFieldState.NotCarriedByTwig => "<not carried by twig>",
                        _ => "?",
                    };

                    Add(2, $"{control.Label}{readOnlyMark}: {rendered}", true,
                        value.State == DetailFieldState.HasValue && value.IsAbbreviated ? value.Full : null);
                }
            }

            Add(0, string.Empty, false, null);
        }

        _scrollOffset = 0;
        _selectedIndex = _rows.FindIndex(r => r.Selectable);
        if (_selectedIndex < 0) _selectedIndex = 0;
    }

    private void Add(int indent, string text, bool selectable, string? full) =>
        _rows.Add(new Row(indent, text, selectable, full));

    internal int RowCount => _rows.Count;

    /// <summary>Caller-owned selection movement.</summary>
    internal void MoveSelection(int delta)
    {
        var next = _selectedIndex;
        while (true)
        {
            next += delta;
            if (next < 0 || next >= _rows.Count) return;
            if (!_rows[next].Selectable) continue;
            _selectedIndex = next;
            ClampScroll();
            return;
        }
    }

    /// <summary>Caller-owned scrolling.</summary>
    internal void Scroll(int delta)
    {
        _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, Math.Max(0, _rows.Count - InnerHeight));
    }

    /// <summary>The full source value behind the selected row, when it was abbreviated.</summary>
    internal string? SelectedFullValue =>
        _selectedIndex >= 0 && _selectedIndex < _rows.Count ? _rows[_selectedIndex].FullValue : null;

    private int InnerWidth => _width - 4;
    private int InnerHeight => _height - 2;

    private void ClampScroll()
    {
        if (_selectedIndex < _scrollOffset) _scrollOffset = _selectedIndex;
        else if (_selectedIndex >= _scrollOffset + InnerHeight) _scrollOffset = _selectedIndex - InnerHeight + 1;
    }

    /// <summary>Paints the pane into a string. Border glyphs are the host's choice.</summary>
    internal string Render()
    {
        var lines = new List<string>(_height) { "+" + new string('-', _width - 2) + "+" };

        for (var i = 0; i < InnerHeight; i++)
        {
            var index = _scrollOffset + i;
            string body;
            if (index >= _rows.Count)
            {
                body = new string(' ', InnerWidth + 2);
            }
            else
            {
                var row = _rows[index];
                var marker = index == _selectedIndex ? '>' : ' ';
                var text = new string(' ', row.Indent * 2) + row.Text;
                // The host clips to its own width. Twig never truncated anything.
                if (text.Length > InnerWidth) text = text[..InnerWidth];
                body = $"{marker} {text.PadRight(InnerWidth)}";
            }

            lines.Add("|" + body + "|");
        }

        lines.Add("+" + new string('-', _width - 2) + "+");
        return string.Join(Environment.NewLine, lines);
    }
}
