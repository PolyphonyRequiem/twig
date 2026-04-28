using Shouldly;
using Twig.Domain.Common;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class DirtyStateSummaryTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Empty / null input
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_EmptyList_ReturnsNull()
    {
        DirtyStateSummary.Build([]).ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Single field change — detailed mode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_SingleFieldChange_ShowsFieldNameChanged()
    {
        var changes = new[]
        {
            MakeField("System.Title", "Old Title", "New Title")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: Title changed");
    }

    [Fact]
    public void Build_SingleFieldChange_SimplifiesDottedFieldName()
    {
        var changes = new[]
        {
            MakeField("Microsoft.VSTS.Scheduling.StoryPoints", "3", "5")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: StoryPoints changed");
    }

    [Fact]
    public void Build_SingleFieldChange_UndottedFieldName()
    {
        var changes = new[]
        {
            MakeField("Priority", "1", "2")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: Priority changed");
    }

    [Fact]
    public void Build_SingleFieldChange_NullFieldName_UsesFieldFallback()
    {
        var changes = new[]
        {
            new PendingChangeRecord(42, "field", null, "old", "new")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: Field changed");
    }

    [Fact]
    public void Build_SingleFieldChange_EmptyFieldName_UsesFieldFallback()
    {
        var changes = new[]
        {
            new PendingChangeRecord(42, "field", "", "old", "new")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: Field changed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  State change — detailed mode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_SingleStateChange_ShowsOldToNew()
    {
        var changes = new[]
        {
            MakeState("To Do", "Doing")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: State To Do → Doing");
    }

    [Fact]
    public void Build_SingleStateChange_NullOldValue_ShowsArrowOnly()
    {
        var changes = new[]
        {
            new PendingChangeRecord(42, "state", "System.State", null, "Doing")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: State → Doing");
    }

    [Fact]
    public void Build_SingleStateChange_NullNewValue_FallsBackToFieldChanged()
    {
        var changes = new[]
        {
            new PendingChangeRecord(42, "state", "System.State", "To Do", null)
        };

        // When NewValue is null, falls back to generic "State changed" format
        DirtyStateSummary.Build(changes).ShouldBe("local: State changed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Two changes — detailed mode (at threshold)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_FieldAndStateChange_ShowsBothDetailed()
    {
        var changes = new[]
        {
            MakeField("System.Title", "Old", "New"),
            MakeState("To Do", "Doing")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: Title changed, State To Do → Doing");
    }

    [Fact]
    public void Build_TwoFieldChanges_ShowsBothDetailed()
    {
        var changes = new[]
        {
            MakeField("System.Title", "Old", "New"),
            MakeField("System.AssignedTo", "Alice", "Bob")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: Title changed, AssignedTo changed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Notes only — aggregated mode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_SingleNote_ShowsNoteCount()
    {
        var changes = new[]
        {
            MakeNote("some note text")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: 1 note");
    }

    [Fact]
    public void Build_MultipleNotes_ShowsNotesCount()
    {
        var changes = new[]
        {
            MakeNote("note 1"),
            MakeNote("note 2"),
            MakeNote("note 3")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: 3 notes");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Mixed changes — aggregated mode (exceeds detail threshold)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ThreeFieldChanges_AggregatedMode()
    {
        var changes = new[]
        {
            MakeField("System.Title", "A", "B"),
            MakeField("System.AssignedTo", "X", "Y"),
            MakeState("To Do", "Doing")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: 3 field changes");
    }

    [Fact]
    public void Build_FieldChangesAndNotes_AggregatedMode()
    {
        var changes = new[]
        {
            MakeField("System.Title", "A", "B"),
            MakeField("System.AssignedTo", "X", "Y"),
            MakeState("To Do", "Doing"),
            MakeNote("note 1")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: 3 field changes, 1 note");
    }

    [Fact]
    public void Build_SingleFieldAndNote_AggregatedBecauseNotes()
    {
        var changes = new[]
        {
            MakeField("System.Title", "A", "B"),
            MakeNote("note 1")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: 1 field change, 1 note");
    }

    [Fact]
    public void Build_ManyFieldsAndManyNotes_AggregatedMode()
    {
        var changes = new[]
        {
            MakeField("System.Title", "A", "B"),
            MakeField("System.AssignedTo", "X", "Y"),
            MakeField("System.AreaPath", "OldArea", "NewArea"),
            MakeField("System.IterationPath", "OldIter", "NewIter"),
            MakeNote("note 1"),
            MakeNote("note 2")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: 4 field changes, 2 notes");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Case insensitivity
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ChangeType_CaseInsensitive_Note()
    {
        var changes = new[]
        {
            new PendingChangeRecord(42, "NOTE", null, null, "note text")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: 1 note");
    }

    [Fact]
    public void Build_ChangeType_CaseInsensitive_State()
    {
        var changes = new[]
        {
            new PendingChangeRecord(42, "STATE", "System.State", "To Do", "Doing")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: State To Do → Doing");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_UnknownChangeType_TreatedAsFieldChange()
    {
        var changes = new[]
        {
            new PendingChangeRecord(42, "custom_type", "SomeField", null, "value")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: SomeField changed");
    }

    [Fact]
    public void Build_FieldChangeWithNoOldOrNewValue_StillShowsFieldChanged()
    {
        var changes = new[]
        {
            new PendingChangeRecord(42, "field", "System.Title", null, null)
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: Title changed");
    }

    [Fact]
    public void Build_MixedWorkItemIds_StillCountsAll()
    {
        // DirtyStateSummary doesn't filter by WorkItemId — that's the caller's job
        var changes = new[]
        {
            new PendingChangeRecord(42, "field", "System.Title", "A", "B"),
            new PendingChangeRecord(99, "note", null, null, "note text")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: 1 field change, 1 note");
    }

    [Fact]
    public void Build_OnlyNotesNoFields_ShowsNotesOnly()
    {
        var changes = new[]
        {
            MakeNote("note 1"),
            MakeNote("note 2")
        };

        DirtyStateSummary.Build(changes).ShouldBe("local: 2 notes");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Threshold boundary — exactly 2 field changes + note = aggregated
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ExactlyTwoFieldChanges_WithNote_Aggregated()
    {
        var changes = new[]
        {
            MakeField("System.Title", "A", "B"),
            MakeState("To Do", "Doing"),
            MakeNote("note")
        };

        var result = DirtyStateSummary.Build(changes);
        result.ShouldBe("local: 2 field changes, 1 note");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static PendingChangeRecord MakeField(string fieldName, string? oldValue, string? newValue) =>
        new(42, "field", fieldName, oldValue, newValue);

    private static PendingChangeRecord MakeState(string? oldValue, string? newValue) =>
        new(42, "state", "System.State", oldValue, newValue);

    private static PendingChangeRecord MakeNote(string text) =>
        new(42, "note", null, null, text);
}
