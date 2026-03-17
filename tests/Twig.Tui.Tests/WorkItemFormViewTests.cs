using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Terminal.Gui.Input;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Tui.Views;
using Xunit;

namespace Twig.Tui.Tests;

public class WorkItemFormViewTests
{
    private static WorkItem CreateWorkItem(int id, string title, string state = "Active", string? assignedTo = "Alice", string type = "User Story")
    {
        var wit = WorkItemType.Parse(type).Value;
        return new WorkItem
        {
            Id = id,
            Title = title,
            Type = wit,
            State = state,
            AssignedTo = assignedTo,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project\\Area").Value,
        };
    }

    [Fact]
    public void LoadWorkItem_SetsFieldValues()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(42, "Test Story", "Active", "Bob");

        form.LoadWorkItem(item);

        // Assert specific field values are populated
        form._titleField.Text.ShouldBe("Test Story");
        form._stateField.Text.ShouldBe("Active");
        form._assignedToField.Text.ShouldBe("Bob");
        form._idLabel.Text.ShouldBe("#42");
        form._typeLabel.Text.ShouldBe("User Story");
    }

    [Fact]
    public void LoadWorkItem_SetsOriginalValues()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(42, "Test Story", "Active", "Bob");

        form.LoadWorkItem(item);

        form._originalTitle.ShouldBe("Test Story");
        form._originalState.ShouldBe("Active");
        form._originalAssignedTo.ShouldBe("Bob");
    }

    [Fact]
    public void CheckDirty_NoChanges_NotDirty()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(1, "Story");

        form.LoadWorkItem(item);
        form.CheckDirty();

        form._isDirty.ShouldBeFalse();
        form._saveButton.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void CheckDirty_TitleChanged_IsDirty()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(1, "Original Title");

        form.LoadWorkItem(item);
        form._titleField.Text = "New Title";
        form.CheckDirty();

        form._isDirty.ShouldBeTrue();
        form._saveButton.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void CheckDirty_StateChanged_IsDirty()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(1, "Story", "Active");

        form.LoadWorkItem(item);
        form._stateField.Text = "Closed";
        form.CheckDirty();

        form._isDirty.ShouldBeTrue();
    }

    [Fact]
    public void CheckDirty_AssignedToChanged_IsDirty()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(1, "Story", assignedTo: "Alice");

        form.LoadWorkItem(item);
        form._assignedToField.Text = "Bob";
        form.CheckDirty();

        form._isDirty.ShouldBeTrue();
    }

    [Fact]
    public void LoadWorkItem_NullAssignedTo_SetsEmptyString()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(1, "Story", assignedTo: null);

        form.LoadWorkItem(item);

        form._assignedToField.Text.ShouldBe("");
    }

    [Fact]
    public void OnSave_CallsAddChangesBatchAsync_ForChangedFieldsOnly()
    {
        var store = Substitute.For<IPendingChangeStore>();
        store.AddChangesBatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<(string, string?, string?, string?)>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(42, "Original Title", "Active", "Alice");
        form.LoadWorkItem(item);

        // Only change title
        form._titleField.Text = "New Title";
        form.CheckDirty();

        var args = new CommandEventArgs { Context = null };
        form.OnSave(null, args);

        // Should have called AddChangesBatchAsync with exactly one change (title only)
        store.Received(1).AddChangesBatchAsync(42,
            Arg.Is<IReadOnlyList<(string, string?, string?, string?)>>(list =>
                list.Count == 1 &&
                list[0].Item1 == "field" &&
                list[0].Item2 == "System.Title" &&
                list[0].Item3 == "Original Title" &&
                list[0].Item4 == "New Title"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OnSave_UpdatesOriginals_SoReloadShowsSavedValues()
    {
        var store = Substitute.For<IPendingChangeStore>();
        store.AddChangesBatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<(string, string?, string?, string?)>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(42, "Original", "Active", "Alice");
        form.LoadWorkItem(item);

        form._titleField.Text = "Updated";
        form.CheckDirty();
        form.OnSave(null, new CommandEventArgs { Context = null });

        // After save, originals should be updated
        form._originalTitle.ShouldBe("Updated");
        form._isDirty.ShouldBeFalse();
        form._saveButton.Enabled.ShouldBeFalse();

        // Re-loading the same item should show saved value via _savedEdits overlay
        form.LoadWorkItem(item);
        form._titleField.Text.ShouldBe("Updated");
    }

    [Fact]
    public void OnSave_NoDirtyFields_IsNoOp()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(42, "Story", "Active", "Alice");
        form.LoadWorkItem(item);

        // No changes made
        var args = new CommandEventArgs { Context = null };
        form.OnSave(null, args);

        store.DidNotReceive().AddChangesBatchAsync(
            Arg.Any<int>(),
            Arg.Any<IReadOnlyList<(string, string?, string?, string?)>>(),
            Arg.Any<CancellationToken>());
        store.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OnSave_Exception_SetsStatusLabel()
    {
        var store = Substitute.For<IPendingChangeStore>();
        store.AddChangesBatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<(string, string?, string?, string?)>>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(42, "Story", "Active", "Alice");
        form.LoadWorkItem(item);

        form._titleField.Text = "Changed";
        form.CheckDirty();
        form.OnSave(null, new CommandEventArgs { Context = null });

        form._statusLabel.Text.ShouldContain("Save failed");
        // Originals should NOT be updated on failure
        form._originalTitle.ShouldBe("Story");
    }

    [Fact]
    public void OnSave_NoDuplicatePersist_WhenSavedTwice()
    {
        var store = Substitute.For<IPendingChangeStore>();
        store.AddChangesBatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<(string, string?, string?, string?)>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(42, "Story", "Active", "Alice");
        form.LoadWorkItem(item);

        form._titleField.Text = "New Title";
        form.CheckDirty();
        form.OnSave(null, new CommandEventArgs { Context = null });

        // After save, dirty is false. Calling OnSave again should be a no-op.
        form.OnSave(null, new CommandEventArgs { Context = null });

        // AddChangesBatchAsync should have been called exactly once
        store.Received(1).AddChangesBatchAsync(42,
            Arg.Any<IReadOnlyList<(string, string?, string?, string?)>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OnSave_MultiFieldBatchFailure_NoPartialPersist_RetryIsSafe()
    {
        // Scenario: Title and State are both dirty. The batch insert throws.
        // On retry, the batch should be called again with both fields — no partial
        // writes exist because AddChangesBatchAsync is transactional (all-or-nothing).
        var callCount = 0;
        var store = Substitute.For<IPendingChangeStore>();
        store.AddChangesBatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<(string, string?, string?, string?)>>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Simulated DB error");
                return Task.CompletedTask;
            });

        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(42, "Original Title", "Active", "Alice");
        form.LoadWorkItem(item);

        // Make both Title and State dirty
        form._titleField.Text = "New Title";
        form._stateField.Text = "Closed";
        form.CheckDirty();

        // First save attempt — fails
        form.OnSave(null, new CommandEventArgs { Context = null });
        form._statusLabel.Text.ShouldContain("Save failed");

        // Originals must NOT be updated — form should still be dirty
        form._originalTitle.ShouldBe("Original Title");
        form._originalState.ShouldBe("Active");
        form._isDirty.ShouldBeTrue();

        // Retry — should succeed, sending the same batch with both fields
        form.OnSave(null, new CommandEventArgs { Context = null });
        form._statusLabel.Text.ShouldContain("Changes saved locally");
        form._originalTitle.ShouldBe("New Title");
        form._originalState.ShouldBe("Closed");
        form._isDirty.ShouldBeFalse();

        // Batch was called exactly twice (one failure + one success)
        store.Received(2).AddChangesBatchAsync(42,
            Arg.Is<IReadOnlyList<(string, string?, string?, string?)>>(list => list.Count == 2),
            Arg.Any<CancellationToken>());
    }
}
