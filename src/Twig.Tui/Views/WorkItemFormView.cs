using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;

namespace Twig.Tui.Views;

/// <summary>
/// Terminal.Gui form view for a work item. Its rows come from a
/// <see cref="WorkItemDetailDocument"/> — the shared projection — and never from a list
/// maintained here.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This view must never regain a field list of its own.</b> Before wayfinder ticket
/// 0004 it built ten <c>TextField</c> members in its constructor and looked each one up in
/// <c>WorkItem.Fields</c> directly, which is a second answer to "which fields do we show"
/// sitting beside the server's. Two such answers drift apart, and the direct dictionary
/// lookup also inherited the core-field hole ticket 0002 documents: <c>System.Title</c>
/// and seven siblings are absent from that dictionary, so the old code special-cased them
/// and silently blanked anything else the filter dropped.
/// </para>
/// <para>
/// The guard is <c>WorkItemFormViewDocumentWalkTests</c>. It asserts by reflection that
/// this class declares no <c>TextField</c> or <c>Label</c> members, and behaviourally that
/// the painted rows equal the document's controls, in the document's order. Re-adding a
/// hard-coded row breaks it.
/// </para>
/// <para>
/// <b>What is still this view's own decision, deliberately:</b> which of the document's
/// rows accept typing. That is <i>editability</i>, not field selection — the rows exist
/// because the document has them either way. <see cref="DetailControl.ReadOnly"/> is
/// reported by the projection and never enforced by it (0002 §6), so the authority here is
/// <see cref="EditableFieldRefs"/>: the three fields <see cref="IPendingChangeStore"/>
/// knows how to persist. Widening that is ticket 0005's problem, not this view's.
/// </para>
/// </remarks>
internal sealed class WorkItemFormView : View
{
    /// <summary>
    /// The fields this view lets the user type into. Not a field list — the document
    /// decides which rows exist; this decides which of them are typable, and it is bounded
    /// by what <see cref="IPendingChangeStore"/> can actually persist today.
    /// </summary>
    internal static readonly IReadOnlyList<string> EditableFieldRefs =
    [
        "System.Title", "System.State", "System.AssignedTo",
    ];

    private const int LabelWidth = 16;

    private readonly IPendingChangeStore _pendingChangeStore;

    private WorkItem? _currentItem;
    internal bool _isDirty;

    // Local cache of saved-but-not-yet-pushed edits keyed by work item ID.
    // This ensures re-selecting an item shows the saved values, not the stale
    // init-only properties on the WorkItem aggregate.
    private readonly Dictionary<int, Dictionary<string, string>> _savedEdits = new();

    // The painted rows, in document order. Rebuilt on every load; never pre-declared.
    private readonly List<FieldRow> _rows = [];

    // Chrome that belongs to the view rather than to any field.
    internal readonly Label _dirtyIndicator;
    internal readonly Button _saveButton;
    internal readonly Label _statusLabel;

    private readonly View _fieldArea;

    private sealed record FieldRow(
        string FieldReferenceName,
        string Label,
        DetailFieldValue Value,
        TextField? Editor,
        string OriginalText);

    public WorkItemFormView(IPendingChangeStore pendingChangeStore)
    {
        _pendingChangeStore = pendingChangeStore;
        CanFocus = true;

        _fieldArea = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(3),
            CanFocus = true,
        };
        Add(_fieldArea);

        _saveButton = new Button
        {
            Text = "Save Changes",
            X = 1,
            Y = Pos.AnchorEnd(3),
            Enabled = false,
        };
        _saveButton.Accepting += OnSave;
        Add(_saveButton);

        _dirtyIndicator = new Label { X = 20, Y = Pos.AnchorEnd(3), Text = "", Width = 12 };
        Add(_dirtyIndicator);

        _statusLabel = new Label { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(1), Text = "" };
        Add(_statusLabel);
    }

    /// <summary>
    /// Loads a work item into the form by walking <paramref name="document"/>.
    /// </summary>
    /// <param name="document">
    /// The shared projection's output — the server's structure, or
    /// <see cref="FallbackFormLayout"/>'s when no layout was served. Either way this view
    /// walks it and takes no view of which fields it ought to contain.
    /// </param>
    /// <param name="item">The aggregate the edit path writes back through.</param>
    public void LoadDocument(WorkItemDetailDocument document, WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(item);

        _currentItem = item;
        _isDirty = false;

        foreach (var existing in _fieldArea.SubViews.ToList())
            _fieldArea.Remove(existing);
        _rows.Clear();

        var savedEdits = _savedEdits.TryGetValue(item.Id, out var edits)
            ? edits
            : new Dictionary<string, string>();

        var row = 0;
        foreach (var page in document.Pages)
        {
            // Host policy, not field selection: this pane draws only pages that carry field
            // controls. The document carried the others flagged, so a pane that wanted to
            // show a disabled History tab still could.
            if (!page.CarriesFieldControls) continue;

            foreach (var group in page.AllGroups)
            {
                if (!group.Visible) continue;

                foreach (var control in group.Controls)
                {
                    if (!control.Visible) continue;

                    // Contribution slots name no field and carry no value. Named, not drawn.
                    if (control.IsContribution || control.Value is null)
                    {
                        _fieldArea.Add(new Label
                        {
                            Text = $"{control.Label}:",
                            X = 1,
                            Y = row,
                        });
                        _fieldArea.Add(new Label
                        {
                            Text = "<add-in — not rendered here>",
                            X = LabelWidth,
                            Y = row,
                            Width = Dim.Fill(1),
                        });
                        row++;
                        continue;
                    }

                    AddFieldRow(control, savedEdits, ref row);
                }
            }
        }

        _isDirty = false;
        UpdateDirtyIndicator();
        _statusLabel.Text = "";
        _saveButton.Enabled = false;
    }

    private void AddFieldRow(
        DetailControl control,
        Dictionary<string, string> savedEdits,
        ref int row)
    {
        _fieldArea.Add(new Label { Text = $"{control.Label}:", X = 1, Y = row });

        var editable = EditableFieldRefs.Contains(control.Id, StringComparer.OrdinalIgnoreCase);

        // The caller only routes field controls here; contributions were handled above.
        var value = control.Value!;

        // The three states, rendered. The host chooses the treatment; the document supplied
        // the distinction, which a raw Fields lookup could not have.
        var displayed = value.State switch
        {
            DetailFieldState.HasValue => value.Short ?? value.Full!,
            DetailFieldState.EmptyOnServer => string.Empty,
            DetailFieldState.NotCarriedByTwig => "<not carried by twig>",
            _ => string.Empty,
        };

        // An edit is applied over the projected value, not instead of it: the row exists
        // because the document has it, and the overlay only changes what it shows.
        if (editable && savedEdits.TryGetValue(control.Id, out var saved))
            displayed = saved;

        var editor = new TextField
        {
            X = LabelWidth,
            Y = row,
            Width = Dim.Fill(1),
            Text = displayed,
            // ReadOnly here is a TUI editability decision (see EditableFieldRefs), not an
            // enforcement of the server's ReadOnly flag, which 0002 reports and never enforces.
            ReadOnly = !editable,
        };

        if (editable)
            editor.ValueChanged += OnFieldValueChanged;

        _fieldArea.Add(editor);

        _rows.Add(new FieldRow(
            control.Id,
            control.Label,
            value,
            editable ? editor : null,
            displayed));

        row++;
    }

    // ── Inspection surface for tests and hosts ──────────────────────

    /// <summary>The field reference names painted, in document order.</summary>
    internal IReadOnlyList<string> FieldOrder =>
        _rows.Select(r => r.FieldReferenceName).ToList();

    /// <summary>The labels painted, in document order.</summary>
    internal IReadOnlyList<string> LabelOrder => _rows.Select(r => r.Label).ToList();

    /// <summary>The editor for a field, or <c>null</c> when the row is not typable.</summary>
    internal TextField? EditorFor(string fieldReferenceName) =>
        _rows.FirstOrDefault(r =>
            string.Equals(r.FieldReferenceName, fieldReferenceName, StringComparison.OrdinalIgnoreCase))
            ?.Editor;

    /// <summary>The text currently shown for a field, or <c>null</c> when it has no row.</summary>
    internal string? DisplayedValue(string fieldReferenceName)
    {
        var match = _rows.FirstOrDefault(r =>
            string.Equals(r.FieldReferenceName, fieldReferenceName, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;
        return match.Editor?.Text ?? match.OriginalText;
    }

    /// <summary>The projected state a field resolved to, or <c>null</c> when it has no row.</summary>
    internal DetailFieldState? StateOf(string fieldReferenceName) =>
        _rows.FirstOrDefault(r =>
            string.Equals(r.FieldReferenceName, fieldReferenceName, StringComparison.OrdinalIgnoreCase))
            ?.Value.State;

    // ── Editing ─────────────────────────────────────────────────────

    private void OnFieldValueChanged(object? sender, ValueChangedEventArgs<string?> e) => CheckDirty();

    internal void CheckDirty()
    {
        if (_currentItem is null) return;

        _isDirty = _rows.Any(r => r.Editor is not null && r.Editor.Text != r.OriginalText);
        UpdateDirtyIndicator();
        _saveButton.Enabled = _isDirty;
    }

    private void UpdateDirtyIndicator() => _dirtyIndicator.Text = _isDirty ? "● Modified" : "";

    internal void OnSave(object? sender, CommandEventArgs e)
    {
        if (_currentItem is null || !_isDirty) return;

        try
        {
            // Collect all changes, then persist atomically via batch insert.
            // This prevents duplicate rows in pending_changes on retry after partial failure:
            // AddChangesBatchAsync wraps all inserts in a single SQLite transaction, so
            // either all changes are persisted or none are.
            var changed = _rows
                .Where(r => r.Editor is not null && r.Editor.Text != r.OriginalText)
                .ToList();

            var toSave = changed
                .Select(r => (
                    ChangeType: ChangeTypeFor(r.FieldReferenceName),
                    FieldName: (string?)r.FieldReferenceName,
                    OldValue: (string?)r.OriginalText,
                    NewValue: (string?)r.Editor!.Text))
                .ToList();

            Task.Run(() => _pendingChangeStore.AddChangesBatchAsync(_currentItem.Id, toSave))
                .GetAwaiter().GetResult();

            // Only update originals after all writes succeeded.
            var edits = _savedEdits.TryGetValue(_currentItem.Id, out var existing)
                ? new Dictionary<string, string>(existing)
                : new Dictionary<string, string>();

            foreach (var r in changed)
            {
                var index = _rows.IndexOf(r);
                _rows[index] = r with { OriginalText = r.Editor!.Text };
                edits[r.FieldReferenceName] = r.Editor.Text;
            }

            _savedEdits[_currentItem.Id] = edits;

            _isDirty = false;
            UpdateDirtyIndicator();
            _saveButton.Enabled = false;
            _statusLabel.Text = "✓ Changes saved locally. Run 'twig save' to push to ADO.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"✗ Save failed: {ex.Message}";
        }

        e.Handled = true;
    }

    private static string ChangeTypeFor(string fieldReferenceName) =>
        string.Equals(fieldReferenceName, "System.State", StringComparison.OrdinalIgnoreCase)
            ? "state"
            : "field";
}
