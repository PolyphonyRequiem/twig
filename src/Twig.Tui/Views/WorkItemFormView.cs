using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;

namespace Twig.Tui.Views;

/// <summary>
/// Terminal.Gui form view for editing work item fields.
/// Shows field labels, editable text fields, dirty indicators, and a save button.
/// </summary>
internal sealed class WorkItemFormView : View
{
    private readonly IPendingChangeStore _pendingChangeStore;

    private WorkItem? _currentItem;
    internal bool _isDirty;

    // Local cache of saved-but-not-yet-pushed edits keyed by work item ID.
    // This ensures re-selecting an item shows the saved values, not the stale
    // init-only properties on the WorkItem aggregate.
    private readonly Dictionary<int, Dictionary<string, string>> _savedEdits = new();

    // Form fields
    internal readonly Label _idLabel;
    internal readonly Label _typeLabel;
    internal readonly TextField _titleField;
    internal readonly TextField _stateField;
    internal readonly TextField _assignedToField;
    internal readonly TextField _iterationField;
    internal readonly TextField _areaField;
    internal readonly Label _dirtyIndicator;
    internal readonly Button _saveButton;
    internal readonly Label _statusLabel;

    // Track original values for dirty detection
    internal string _originalTitle = string.Empty;
    internal string _originalState = string.Empty;
    internal string _originalAssignedTo = string.Empty;

    public WorkItemFormView(IPendingChangeStore pendingChangeStore)
    {
        _pendingChangeStore = pendingChangeStore;
        CanFocus = true;

        var row = 0;

        // ID (read-only)
        Add(new Label { Text = "ID:", X = 1, Y = row });
        _idLabel = new Label { X = 16, Y = row, Width = Dim.Fill(1), Text = "—" };
        Add(_idLabel);
        row++;

        // Type (read-only)
        Add(new Label { Text = "Type:", X = 1, Y = row });
        _typeLabel = new Label { X = 16, Y = row, Width = Dim.Fill(1), Text = "—" };
        Add(_typeLabel);
        row++;

        row++; // spacer

        // Title (editable)
        Add(new Label { Text = "Title:", X = 1, Y = row });
        _titleField = new TextField { X = 16, Y = row, Width = Dim.Fill(1), Text = "" };
        _titleField.ValueChanged += OnFieldValueChanged;
        Add(_titleField);
        row++;

        // State (editable)
        Add(new Label { Text = "State:", X = 1, Y = row });
        _stateField = new TextField { X = 16, Y = row, Width = Dim.Fill(1), Text = "" };
        _stateField.ValueChanged += OnFieldValueChanged;
        Add(_stateField);
        row++;

        // Assigned To (editable)
        Add(new Label { Text = "Assigned To:", X = 1, Y = row });
        _assignedToField = new TextField { X = 16, Y = row, Width = Dim.Fill(1), Text = "" };
        _assignedToField.ValueChanged += OnFieldValueChanged;
        Add(_assignedToField);
        row++;

        // Iteration (read-only display)
        Add(new Label { Text = "Iteration:", X = 1, Y = row });
        _iterationField = new TextField { X = 16, Y = row, Width = Dim.Fill(1), Text = "", ReadOnly = true };
        Add(_iterationField);
        row++;

        // Area (read-only display)
        Add(new Label { Text = "Area:", X = 1, Y = row });
        _areaField = new TextField { X = 16, Y = row, Width = Dim.Fill(1), Text = "", ReadOnly = true };
        Add(_areaField);
        row += 2; // spacer

        // Save button
        _saveButton = new Button
        {
            Text = "Save Changes",
            X = 1,
            Y = row,
            Enabled = false,
        };
        _saveButton.Accepting += OnSave;
        Add(_saveButton);

        // Dirty indicator — positioned to the right of the save button
        _dirtyIndicator = new Label { X = 20, Y = row, Text = "", Width = 12 };
        Add(_dirtyIndicator);

        row++;

        // Status label (shows save result)
        _statusLabel = new Label { X = 1, Y = row, Width = Dim.Fill(1), Text = "" };
        Add(_statusLabel);
    }

    /// <summary>
    /// Loads a work item into the form for display and editing.
    /// </summary>
    public void LoadWorkItem(WorkItem item)
    {
        _currentItem = item;
        _isDirty = false;

        _idLabel.Text = $"#{item.Id}";
        _typeLabel.Text = item.Type.ToString();

        // Overlay saved-but-not-yet-pushed edits if the user previously saved changes
        // to this item. WorkItem properties are init-only and cannot be mutated in memory,
        // so we use our local _savedEdits cache to show the correct values.
        var title = item.Title;
        var state = item.State;
        var assignedTo = item.AssignedTo ?? "";

        if (_savedEdits.TryGetValue(item.Id, out var edits))
        {
            if (edits.TryGetValue("System.Title", out var savedTitle)) title = savedTitle;
            if (edits.TryGetValue("System.State", out var savedState)) state = savedState;
            if (edits.TryGetValue("System.AssignedTo", out var savedAssigned)) assignedTo = savedAssigned;
        }

        _titleField.Text = title;
        _stateField.Text = state;
        _assignedToField.Text = assignedTo;
        _iterationField.Text = item.IterationPath.ToString();
        _areaField.Text = item.AreaPath.ToString();

        _originalTitle = title;
        _originalState = state;
        _originalAssignedTo = assignedTo;

        // Reset dirty state after originals are set. TextField.ValueChanged fires during
        // the field assignments above (before originals were updated), which can leave
        // _isDirty = true. Explicitly reset here so the form starts in a clean state.
        _isDirty = false;
        UpdateDirtyIndicator();
        _statusLabel.Text = "";
        _saveButton.Enabled = false;
    }

    private void OnFieldValueChanged(object? sender, ValueChangedEventArgs<string?> e)
    {
        CheckDirty();
    }

    internal void CheckDirty()
    {
        if (_currentItem is null) return;

        var titleChanged = _titleField.Text != _originalTitle;
        var stateChanged = _stateField.Text != _originalState;
        var assignedChanged = _assignedToField.Text != _originalAssignedTo;

        _isDirty = titleChanged || stateChanged || assignedChanged;
        UpdateDirtyIndicator();
        _saveButton.Enabled = _isDirty;
    }

    private void UpdateDirtyIndicator()
    {
        _dirtyIndicator.Text = _isDirty ? "● Modified" : "";
    }

    internal void OnSave(object? sender, CommandEventArgs e)
    {
        if (_currentItem is null || !_isDirty) return;

        try
        {
            // Collect all changes, then persist atomically via batch insert.
            // This prevents duplicate rows in pending_changes on retry after partial failure:
            // AddChangesBatchAsync wraps all inserts in a single SQLite transaction, so
            // either all changes are persisted or none are.
            var toSave = new List<(string ChangeType, string? FieldName, string? OldValue, string? NewValue)>();

            if (_titleField.Text != _originalTitle)
                toSave.Add(("field", "System.Title", _originalTitle, _titleField.Text));
            if (_stateField.Text != _originalState)
                toSave.Add(("state", "System.State", _originalState, _stateField.Text));
            if (_assignedToField.Text != _originalAssignedTo)
                toSave.Add(("field", "System.AssignedTo", _originalAssignedTo, _assignedToField.Text));

            Task.Run(() => _pendingChangeStore.AddChangesBatchAsync(_currentItem.Id, toSave))
                .GetAwaiter().GetResult();

            // Only update originals after all writes succeeded
            _originalTitle = _titleField.Text;
            _originalState = _stateField.Text;
            _originalAssignedTo = _assignedToField.Text;

            // Cache only the fields that were actually changed so re-selecting this item
            // shows correct values without masking externally-updated unchanged fields.
            var edits = _savedEdits.TryGetValue(_currentItem.Id, out var existing)
                ? new Dictionary<string, string>(existing)
                : new Dictionary<string, string>();

            foreach (var (_, fieldName, _, newValue) in toSave)
                if (fieldName is not null && newValue is not null)
                    edits[fieldName] = newValue;

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
}
