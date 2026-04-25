using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Terminal.Gui.Input;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
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

    // ── E3-T4: Extended fields (effort, priority, tags, description) ────────

    [Fact]
    public void LoadWorkItem_PopulatesEffortField_FromStoryPoints()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = new WorkItemBuilder(42, "Story")
            .AsUserStory()
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .WithField("Microsoft.VSTS.Scheduling.StoryPoints", "8")
            .Build();

        form.LoadWorkItem(item);

        form._effortField.Text.ShouldBe("8");
    }

    [Fact]
    public void LoadWorkItem_PopulatesEffortField_FromEffortSuffix()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = new WorkItemBuilder(42, "PBI")
            .AsProductBacklogItem()
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .WithField("Microsoft.VSTS.Scheduling.Effort", "5")
            .Build();

        form.LoadWorkItem(item);

        form._effortField.Text.ShouldBe("5");
    }

    [Fact]
    public void LoadWorkItem_PopulatesEffortField_FromSizeSuffix()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = new WorkItemBuilder(42, "Req")
            .AsRequirement()
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .WithField("Microsoft.VSTS.Scheduling.Size", "13")
            .Build();

        form.LoadWorkItem(item);

        form._effortField.Text.ShouldBe("13");
    }

    [Fact]
    public void LoadWorkItem_EffortField_EmptyWhenNoEffortKey()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(42, "Story");

        form.LoadWorkItem(item);

        form._effortField.Text.ShouldBe("");
    }

    [Fact]
    public void LoadWorkItem_PopulatesPriorityField()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = new WorkItemBuilder(42, "Story")
            .AsUserStory()
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .WithField("Microsoft.VSTS.Common.Priority", "2")
            .Build();

        form.LoadWorkItem(item);

        form._priorityField.Text.ShouldBe("2");
    }

    [Fact]
    public void LoadWorkItem_PriorityField_EmptyWhenMissing()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(42, "Story");

        form.LoadWorkItem(item);

        form._priorityField.Text.ShouldBe("");
    }

    [Fact]
    public void LoadWorkItem_PopulatesTagsField()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = new WorkItemBuilder(42, "Story")
            .AsUserStory()
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .WithField("System.Tags", "frontend; urgent")
            .Build();

        form.LoadWorkItem(item);

        form._tagsField.Text.ShouldBe("frontend; urgent");
    }

    [Fact]
    public void LoadWorkItem_TagsField_EmptyWhenMissing()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(42, "Story");

        form.LoadWorkItem(item);

        form._tagsField.Text.ShouldBe("");
    }

    [Fact]
    public void LoadWorkItem_PopulatesDescriptionField_WithHtmlStripped()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = new WorkItemBuilder(42, "Story")
            .AsUserStory()
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .WithField("System.Description", "<div>Hello <b>world</b></div>")
            .Build();

        form.LoadWorkItem(item);

        form._descriptionField.Text.ShouldBe("Hello world");
    }

    [Fact]
    public void LoadWorkItem_DescriptionField_EmptyWhenMissing()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = CreateWorkItem(42, "Story");

        form.LoadWorkItem(item);

        form._descriptionField.Text.ShouldBe("");
    }

    [Fact]
    public void LoadWorkItem_ExtendedFieldsUpdateOnSwitch()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);

        var item1 = new WorkItemBuilder(1, "Story 1")
            .AsUserStory()
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .WithField("Microsoft.VSTS.Scheduling.StoryPoints", "3")
            .WithField("Microsoft.VSTS.Common.Priority", "1")
            .WithField("System.Tags", "alpha")
            .WithField("System.Description", "<p>First</p>")
            .Build();

        var item2 = new WorkItemBuilder(2, "Story 2")
            .AsUserStory()
            .WithIterationPath("Project\\Sprint 2")
            .WithAreaPath("Project\\Team")
            .WithField("Microsoft.VSTS.Scheduling.StoryPoints", "8")
            .WithField("Microsoft.VSTS.Common.Priority", "3")
            .WithField("System.Tags", "beta; gamma")
            .WithField("System.Description", "<div>Second</div>")
            .Build();

        form.LoadWorkItem(item1);
        form._effortField.Text.ShouldBe("3");
        form._priorityField.Text.ShouldBe("1");
        form._tagsField.Text.ShouldBe("alpha");
        form._descriptionField.Text.ShouldBe("First");

        form.LoadWorkItem(item2);
        form._effortField.Text.ShouldBe("8");
        form._priorityField.Text.ShouldBe("3");
        form._tagsField.Text.ShouldBe("beta; gamma");
        form._descriptionField.Text.ShouldBe("Second");
    }

    [Fact]
    public void LoadWorkItem_ExtendedFieldsClearWhenSwitchingToItemWithoutFields()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);

        var itemWithFields = new WorkItemBuilder(1, "Rich Story")
            .AsUserStory()
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .WithField("Microsoft.VSTS.Scheduling.StoryPoints", "5")
            .WithField("Microsoft.VSTS.Common.Priority", "2")
            .WithField("System.Tags", "test")
            .WithField("System.Description", "<b>Desc</b>")
            .Build();

        var itemWithout = CreateWorkItem(2, "Plain Story");

        form.LoadWorkItem(itemWithFields);
        form._effortField.Text.ShouldBe("5");

        form.LoadWorkItem(itemWithout);
        form._effortField.Text.ShouldBe("");
        form._priorityField.Text.ShouldBe("");
        form._tagsField.Text.ShouldBe("");
        form._descriptionField.Text.ShouldBe("");
    }

    [Fact]
    public void ExtendedFields_AreReadOnly()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);

        form._effortField.ReadOnly.ShouldBeTrue();
        form._priorityField.ReadOnly.ShouldBeTrue();
        form._tagsField.ReadOnly.ShouldBeTrue();
        form._descriptionField.ReadOnly.ShouldBeTrue();
    }

    [Fact]
    public void LoadWorkItem_PriorityField_DoesNotMatchBacklogPriority()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = new WorkItemBuilder(42, "Story")
            .AsUserStory()
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .WithField("Microsoft.VSTS.Common.BacklogPriority", "999999992")
            .Build();

        form.LoadWorkItem(item);

        // BacklogPriority is a sort-order integer, not a user-visible priority value
        form._priorityField.Text.ShouldBe("");
    }

    [Fact]
    public void LoadWorkItem_PriorityField_ShowsRealPriority_WhenBothBacklogAndPriorityExist()
    {
        var store = Substitute.For<IPendingChangeStore>();
        var form = new WorkItemFormView(store);
        var item = new WorkItemBuilder(42, "Story")
            .AsUserStory()
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Team")
            .WithField("Microsoft.VSTS.Common.BacklogPriority", "999999992")
            .WithField("Microsoft.VSTS.Common.Priority", "2")
            .Build();

        form.LoadWorkItem(item);

        // Only the genuine Priority value should be shown, not BacklogPriority
        form._priorityField.Text.ShouldBe("2");
    }

    [Fact]
    public void StripHtmlTags_RemovesTags()
    {
        WorkItemFormView.StripHtmlTags("<div>Hello <b>world</b></div>")
            .ShouldBe("Hello world");
    }

    [Fact]
    public void StripHtmlTags_HandlesPlainText()
    {
        WorkItemFormView.StripHtmlTags("No tags here")
            .ShouldBe("No tags here");
    }

    [Fact]
    public void StripHtmlTags_HandlesEmptyString()
    {
        WorkItemFormView.StripHtmlTags("").ShouldBe("");
    }

    [Fact]
    public void StripHtmlTags_HandlesUnclosedTag()
    {
        // Unclosed '<' treated as literal
        WorkItemFormView.StripHtmlTags("a < b").ShouldBe("a < b");
    }
}
