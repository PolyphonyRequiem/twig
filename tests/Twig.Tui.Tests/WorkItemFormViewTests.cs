using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Terminal.Gui.Input;
using Twig.Domain.Aggregates;
using Twig.Domain.Projections;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Twig.Tui.Views;
using Xunit;

namespace Twig.Tui.Tests;

/// <summary>
/// Editing behaviour of <see cref="WorkItemFormView"/>: dirty tracking, batch save,
/// failure handling, and the saved-edit overlay.
/// </summary>
/// <remarks>
/// These arms survived the ticket-0004 migration unchanged in intent. What changed is how
/// the form is loaded — a <see cref="WorkItemDetailDocument"/> instead of a
/// <see cref="WorkItem"/> plus a hard-coded field list. The field-selection guarantees
/// live in <see cref="WorkItemFormViewDocumentWalkTests"/>.
/// </remarks>
public class WorkItemFormViewTests
{
    private static WorkItem CreateWorkItem(
        int id,
        string title,
        string state = "Active",
        string? assignedTo = "Alice",
        WorkItemType? type = null,
        IEnumerable<KeyValuePair<string, string?>>? fields = null)
    {
        var builder = new WorkItemBuilder(id, title)
            .AsType(type ?? WorkItemType.UserStory)
            .InState(state)
            .AssignedTo(assignedTo)
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project\\Area");

        if (fields is not null)
            builder = builder.WithFields(fields);

        return builder.Build();
    }

    private static WorkItemSnapshot SnapshotOf(WorkItem item) =>
        new Twig.Domain.Services.WorkItemMapper().ToSnapshot(item);

    /// <summary>
    /// A document over the item's own fallback layout — the same document the TUI paints
    /// when no server layout is served, so these tests exercise the real load path.
    /// </summary>
    private static WorkItemDetailDocument DocumentFor(WorkItem item)
    {
        var snapshot = SnapshotOf(item);
        return WorkItemDetailProjector.Project(FallbackFormLayout.For(snapshot), snapshot);
    }

    private static (WorkItemFormView Form, IPendingChangeStore Store) Load(WorkItem item)
    {
        var store = Substitute.For<IPendingChangeStore>();
        store.AddChangesBatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<(string, string?, string?, string?)>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var form = new WorkItemFormView(store);
        form.LoadDocument(DocumentFor(item), item);
        return (form, store);
    }

    [Fact]
    public void LoadDocument_SetsFieldValues()
    {
        var item = CreateWorkItem(42, "Test Story", "Active", "Bob");
        var (form, _) = Load(item);

        form.DisplayedValue("System.Title").ShouldBe("Test Story");
        form.DisplayedValue("System.State").ShouldBe("Active");
        form.DisplayedValue("System.AssignedTo").ShouldBe("Bob");
        form.DisplayedValue("System.Id").ShouldBe("42");
        form.DisplayedValue("System.WorkItemType").ShouldBe("User Story");
    }

    [Fact]
    public void CheckDirty_NoChanges_NotDirty()
    {
        var (form, _) = Load(CreateWorkItem(1, "Story"));

        form.CheckDirty();

        form._isDirty.ShouldBeFalse();
        form._saveButton.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void CheckDirty_TitleChanged_IsDirty()
    {
        var (form, _) = Load(CreateWorkItem(1, "Original Title"));

        form.EditorFor("System.Title")!.Text = "New Title";
        form.CheckDirty();

        form._isDirty.ShouldBeTrue();
        form._saveButton.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void CheckDirty_StateChanged_IsDirty()
    {
        var (form, _) = Load(CreateWorkItem(1, "Story", "Active"));

        form.EditorFor("System.State")!.Text = "Closed";
        form.CheckDirty();

        form._isDirty.ShouldBeTrue();
    }

    [Fact]
    public void CheckDirty_AssignedToChanged_IsDirty()
    {
        var (form, _) = Load(CreateWorkItem(1, "Story", assignedTo: "Alice"));

        form.EditorFor("System.AssignedTo")!.Text = "Bob";
        form.CheckDirty();

        form._isDirty.ShouldBeTrue();
    }

    [Fact]
    public void LoadDocument_NullAssignedTo_ShowsEmpty()
    {
        var (form, _) = Load(CreateWorkItem(1, "Story", assignedTo: null));

        form.DisplayedValue("System.AssignedTo").ShouldBe("");
        form.StateOf("System.AssignedTo").ShouldBe(DetailFieldState.EmptyOnServer);
    }

    [Fact]
    public void OnSave_CallsAddChangesBatchAsync_ForChangedFieldsOnly()
    {
        var item = CreateWorkItem(42, "Original Title", "Active", "Alice");
        var (form, store) = Load(item);

        form.EditorFor("System.Title")!.Text = "New Title";
        form.CheckDirty();

        form.OnSave(null, new CommandEventArgs { Context = null });

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
    public void OnSave_StateChange_IsTaggedAsAStateChange()
    {
        var item = CreateWorkItem(42, "Story", "Active");
        var (form, store) = Load(item);

        form.EditorFor("System.State")!.Text = "Closed";
        form.CheckDirty();
        form.OnSave(null, new CommandEventArgs { Context = null });

        store.Received(1).AddChangesBatchAsync(42,
            Arg.Is<IReadOnlyList<(string, string?, string?, string?)>>(list =>
                list.Count == 1 && list[0].Item1 == "state" && list[0].Item2 == "System.State"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OnSave_UpdatesOriginals_SoReloadShowsSavedValues()
    {
        var item = CreateWorkItem(42, "Original", "Active", "Alice");
        var (form, _) = Load(item);

        form.EditorFor("System.Title")!.Text = "Updated";
        form.CheckDirty();
        form.OnSave(null, new CommandEventArgs { Context = null });

        form._isDirty.ShouldBeFalse();
        form._saveButton.Enabled.ShouldBeFalse();

        // Re-loading the same item shows the saved value via the _savedEdits overlay.
        form.LoadDocument(DocumentFor(item), item);
        form.DisplayedValue("System.Title").ShouldBe("Updated");
    }

    [Fact]
    public void OnSave_NoDirtyFields_IsNoOp()
    {
        var (form, store) = Load(CreateWorkItem(42, "Story", "Active", "Alice"));

        form.OnSave(null, new CommandEventArgs { Context = null });

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

        var item = CreateWorkItem(42, "Story", "Active", "Alice");
        var form = new WorkItemFormView(store);
        form.LoadDocument(DocumentFor(item), item);

        form.EditorFor("System.Title")!.Text = "Changed";
        form.CheckDirty();
        form.OnSave(null, new CommandEventArgs { Context = null });

        form._statusLabel.Text.ShouldContain("Save failed");

        // Originals must NOT be updated on failure — the form is still dirty.
        form.CheckDirty();
        form._isDirty.ShouldBeTrue();
    }

    [Fact]
    public void OnSave_NoDuplicatePersist_WhenSavedTwice()
    {
        var item = CreateWorkItem(42, "Story", "Active", "Alice");
        var (form, store) = Load(item);

        form.EditorFor("System.Title")!.Text = "New Title";
        form.CheckDirty();
        form.OnSave(null, new CommandEventArgs { Context = null });

        // After save, dirty is false. Calling OnSave again should be a no-op.
        form.OnSave(null, new CommandEventArgs { Context = null });

        store.Received(1).AddChangesBatchAsync(42,
            Arg.Any<IReadOnlyList<(string, string?, string?, string?)>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OnSave_MultiFieldBatchFailure_NoPartialPersist_RetryIsSafe()
    {
        // Title and State are both dirty and the batch insert throws. On retry the batch is
        // sent again with both fields — no partial writes exist because AddChangesBatchAsync
        // is transactional (all-or-nothing).
        var callCount = 0;
        var store = Substitute.For<IPendingChangeStore>();
        store.AddChangesBatchAsync(Arg.Any<int>(),
            Arg.Any<IReadOnlyList<(string, string?, string?, string?)>>(),
            Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Simulated DB error");
                return Task.CompletedTask;
            });

        var item = CreateWorkItem(42, "Original Title", "Active", "Alice");
        var form = new WorkItemFormView(store);
        form.LoadDocument(DocumentFor(item), item);

        form.EditorFor("System.Title")!.Text = "New Title";
        form.EditorFor("System.State")!.Text = "Closed";
        form.CheckDirty();

        form.OnSave(null, new CommandEventArgs { Context = null });
        form._statusLabel.Text.ShouldContain("Save failed");
        form._isDirty.ShouldBeTrue();

        form.OnSave(null, new CommandEventArgs { Context = null });
        form._statusLabel.Text.ShouldContain("Changes saved locally");
        form._isDirty.ShouldBeFalse();

        store.Received(2).AddChangesBatchAsync(42,
            Arg.Is<IReadOnlyList<(string, string?, string?, string?)>>(list => list.Count == 2),
            Arg.Any<CancellationToken>());
    }

    // ── Extended fields now arrive through the document ─────────────

    [Fact]
    public void LoadDocument_ShowsCarriedFields_WithoutTheViewNamingThem()
    {
        var item = CreateWorkItem(42, "Story", fields:
        [
            new("Microsoft.VSTS.Scheduling.StoryPoints", "8"),
            new("Microsoft.VSTS.Common.Priority", "2"),
            new("System.Tags", "frontend; urgent"),
        ]);

        var (form, _) = Load(item);

        form.DisplayedValue("Microsoft.VSTS.Scheduling.StoryPoints").ShouldBe("8");
        form.DisplayedValue("Microsoft.VSTS.Common.Priority").ShouldBe("2");
        form.DisplayedValue("System.Tags").ShouldBe("frontend; urgent");
    }

    [Fact]
    public void LoadDocument_FieldAbsentFromTheDocument_HasNoRow()
    {
        var (form, _) = Load(CreateWorkItem(42, "Story"));

        form.DisplayedValue("Microsoft.VSTS.Common.Priority").ShouldBeNull();
        form.DisplayedValue("System.Tags").ShouldBeNull();
    }

    [Fact]
    public void LoadDocument_CarriedFieldsAreNotTypable()
    {
        // Only the three fields IPendingChangeStore can persist accept typing. Everything
        // else the document carries is shown read-only.
        var item = CreateWorkItem(42, "Story", fields:
        [
            new("Microsoft.VSTS.Common.Priority", "2"),
        ]);

        var (form, _) = Load(item);

        form.EditorFor("Microsoft.VSTS.Common.Priority").ShouldBeNull();
        form.EditorFor("System.Title").ShouldNotBeNull();
    }

    [Fact]
    public void LoadDocument_LongValueShowsShortForm_WithFullValuePreservedInTheDocument()
    {
        var longBody = new string('x', 200);
        var item = CreateWorkItem(42, "Story", fields:
        [
            new("System.Description", longBody),
        ]);

        var document = DocumentFor(item);
        var (form, _) = (new WorkItemFormView(Substitute.For<IPendingChangeStore>()), (IPendingChangeStore?)null);
        form.LoadDocument(document, item);

        var control = document.Pages
            .SelectMany(p => p.AllGroups)
            .SelectMany(g => g.Controls)
            .Single(c => c.Id == "System.Description");

        // Twig never truncated: the full value is still in the document.
        control.Value!.Full.ShouldBe(longBody);
        control.Value.IsAbbreviated.ShouldBeTrue();

        // The row shows the short form, which is the host's choice of treatment.
        form.DisplayedValue("System.Description").ShouldBe(control.Value.Short);
    }

    [Fact]
    public void LoadDocument_SwitchingItems_UpdatesValues()
    {
        var item1 = CreateWorkItem(1, "Story 1", fields:
        [
            new("Microsoft.VSTS.Scheduling.StoryPoints", "3"),
        ]);
        var item2 = CreateWorkItem(2, "Story 2", fields:
        [
            new("Microsoft.VSTS.Scheduling.StoryPoints", "8"),
        ]);

        var (form, _) = Load(item1);
        form.DisplayedValue("Microsoft.VSTS.Scheduling.StoryPoints").ShouldBe("3");

        form.LoadDocument(DocumentFor(item2), item2);
        form.DisplayedValue("Microsoft.VSTS.Scheduling.StoryPoints").ShouldBe("8");
        form.DisplayedValue("System.Title").ShouldBe("Story 2");
    }
}
