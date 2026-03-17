using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Commands;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Aggregates;

public class WorkItemTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Construction + property tests (ITEM-039)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_DefaultProperties()
    {
        var wi = new WorkItem
        {
            Id = 42,
            Type = WorkItemType.Bug,
            Title = "Fix crash",
            State = "New",
        };

        wi.Id.ShouldBe(42);
        wi.Type.ShouldBe(WorkItemType.Bug);
        wi.Title.ShouldBe("Fix crash");
        wi.State.ShouldBe("New");
        wi.AssignedTo.ShouldBeNull();
        wi.ParentId.ShouldBeNull();
        wi.Revision.ShouldBe(0);
        wi.IsDirty.ShouldBeFalse();
        wi.IsSeed.ShouldBeFalse();
        wi.SeedCreatedAt.ShouldBeNull();
        wi.Fields.ShouldBeEmpty();
        wi.PendingNotes.ShouldBeEmpty();
    }

    [Fact]
    public void Constructor_WithAllProperties()
    {
        var iterResult = IterationPath.Parse("Project\\Sprint1");
        var areaResult = AreaPath.Parse("Project\\Team");

        var wi = new WorkItem
        {
            Id = 100,
            Type = WorkItemType.UserStory,
            Title = "Story",
            State = "Active",
            AssignedTo = "alice@example.com",
            IterationPath = iterResult.Value,
            AreaPath = areaResult.Value,
            ParentId = 50,
        };

        wi.AssignedTo.ShouldBe("alice@example.com");
        wi.IterationPath.Value.ShouldBe("Project\\Sprint1");
        wi.AreaPath.Value.ShouldBe("Project\\Team");
        wi.ParentId.ShouldBe(50);
    }

    [Fact]
    public void Fields_PublicSurface_IsReadOnly()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        wi.Fields.ShouldBeAssignableTo<IReadOnlyDictionary<string, string?>>();
    }

    [Fact]
    public void Fields_CannotBeMutatedViaCast()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        var fields = wi.Fields;

        // Casting to Dictionary should fail — the wrapper is ReadOnlyDictionary, not Dictionary
        fields.ShouldNotBeOfType<Dictionary<string, string?>>();
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<string, string?>)fields)["System.Title"] = "hacked");
    }

    [Fact]
    public void PendingNotes_PublicSurface_IsReadOnly()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        wi.PendingNotes.ShouldBeAssignableTo<IReadOnlyList<PendingNote>>();
    }

    [Fact]
    public void PendingNotes_CannotBeMutatedViaCast()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        var notes = wi.PendingNotes;

        // Casting to List should fail — the wrapper is ReadOnlyCollection, not List
        notes.ShouldNotBeOfType<List<PendingNote>>();
        Should.Throw<NotSupportedException>(() =>
            ((IList<PendingNote>)notes).Add(new PendingNote("hacked", DateTimeOffset.UtcNow, false)));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Command guard clause tests
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ChangeStateCommand_ThrowsOnNullOrWhitespace(string? newState)
    {
        Should.Throw<ArgumentException>(() => new ChangeStateCommand(newState!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateFieldCommand_ThrowsOnNullOrWhitespaceFieldName(string? fieldName)
    {
        Should.Throw<ArgumentException>(() => new UpdateFieldCommand(fieldName!, "value"));
    }

    [Fact]
    public void ChangeStateCommand_Confirmation_IsPreserved()
    {
        var cmd = new ChangeStateCommand("Resolved", "Confirmed by user");
        cmd.Confirmation.ShouldBe("Confirmed by user");

        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        cmd.Execute(wi);

        cmd.Confirmation.ShouldBe("Confirmed by user");
    }

    // ═══════════════════════════════════════════════════════════════
    //  State transition tests (ITEM-040)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ChangeState_EnqueuesAndApplies()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };

        wi.ChangeState("Active");
        wi.IsDirty.ShouldBeTrue();

        var changes = wi.ApplyCommands();

        wi.State.ShouldBe("Active");
        changes.Count.ShouldBe(1);
        changes[0].FieldName.ShouldBe("System.State");
        changes[0].OldValue.ShouldBe("New");
        changes[0].NewValue.ShouldBe("Active");
        wi.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void ChangeState_ForwardThenBackward()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };

        wi.ChangeState("Active");
        wi.ChangeState("New");

        var changes = wi.ApplyCommands();

        wi.State.ShouldBe("New");
        changes.Count.ShouldBe(2);
        changes[0].OldValue.ShouldBe("New");
        changes[0].NewValue.ShouldBe("Active");
        changes[1].OldValue.ShouldBe("Active");
        changes[1].NewValue.ShouldBe("New");
    }

    [Fact]
    public void ChangeState_WithConfirmation()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.UserStory, State = "Active" };

        wi.ChangeState("Removed", "User confirmed removal");
        var changes = wi.ApplyCommands();

        wi.State.ShouldBe("Removed");
        changes.Count.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Field update tests (ITEM-041)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void UpdateField_EnqueuesAndApplies()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };

        wi.UpdateField("System.Description", "Some description");
        wi.IsDirty.ShouldBeTrue();

        var changes = wi.ApplyCommands();

        changes.Count.ShouldBe(1);
        changes[0].FieldName.ShouldBe("System.Description");
        changes[0].OldValue.ShouldBeNull();
        changes[0].NewValue.ShouldBe("Some description");
        wi.Fields["System.Description"].ShouldBe("Some description");
        wi.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void UpdateField_OverwritesExistingValue()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        wi.SetField("Priority", "2");

        wi.UpdateField("Priority", "1");
        var changes = wi.ApplyCommands();

        changes.Count.ShouldBe(1);
        changes[0].OldValue.ShouldBe("2");
        changes[0].NewValue.ShouldBe("1");
        wi.Fields["Priority"].ShouldBe("1");
    }

    [Fact]
    public void UpdateField_SetsNullValue()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        wi.SetField("System.AssignedTo", "bob@example.com");

        wi.UpdateField("System.AssignedTo", null);
        var changes = wi.ApplyCommands();

        changes.Count.ShouldBe(1);
        changes[0].OldValue.ShouldBe("bob@example.com");
        changes[0].NewValue.ShouldBeNull();
        wi.Fields["System.AssignedTo"].ShouldBeNull();
    }

    [Fact]
    public void UpdateField_CaseInsensitive()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        wi.SetField("system.description", "old");

        wi.UpdateField("System.Description", "new");
        var changes = wi.ApplyCommands();

        changes.Count.ShouldBe(1);
        changes[0].OldValue.ShouldBe("old");
        changes[0].NewValue.ShouldBe("new");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Note tests (ITEM-042)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AddNote_AppendsToList()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        var note = new PendingNote("Test note", DateTimeOffset.UtcNow, false);

        wi.AddNote(note);
        wi.IsDirty.ShouldBeTrue();

        var changes = wi.ApplyCommands();

        changes.ShouldBeEmpty();
        wi.PendingNotes.Count.ShouldBe(1);
        wi.PendingNotes[0].Text.ShouldBe("Test note");
        wi.PendingNotes[0].IsHtml.ShouldBeFalse();
    }

    [Fact]
    public void AddNote_MultipleNotes()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };

        wi.AddNote(new PendingNote("Note 1", DateTimeOffset.UtcNow, false));
        wi.AddNote(new PendingNote("<b>Note 2</b>", DateTimeOffset.UtcNow, true));

        var changes = wi.ApplyCommands();

        changes.ShouldBeEmpty();
        wi.PendingNotes.Count.ShouldBe(2);
        wi.PendingNotes[0].Text.ShouldBe("Note 1");
        wi.PendingNotes[1].Text.ShouldBe("<b>Note 2</b>");
        wi.PendingNotes[1].IsHtml.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Seed tests (ITEM-043)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CreateSeed_ReturnsSeedWorkItem()
    {
        var before = DateTimeOffset.UtcNow;
        var seed = WorkItem.CreateSeed(WorkItemType.Bug, "New bug");
        var after = DateTimeOffset.UtcNow;

        seed.IsSeed.ShouldBeTrue();
        seed.Id.ShouldBeLessThan(0);
        seed.Type.ShouldBe(WorkItemType.Bug);
        seed.Title.ShouldBe("New bug");
        seed.SeedCreatedAt.ShouldNotBeNull();
        seed.SeedCreatedAt!.Value.ShouldBeGreaterThanOrEqualTo(before);
        seed.SeedCreatedAt!.Value.ShouldBeLessThanOrEqualTo(after);
    }

    [Fact]
    public void CreateSeed_DefaultState_IsEmpty()
    {
        var seed = WorkItem.CreateSeed(WorkItemType.Task, "Seed task");
        seed.State.ShouldBe(string.Empty);
    }

    [Fact]
    public void CreateSeed_HasEmptyFieldsAndNotes()
    {
        var seed = WorkItem.CreateSeed(WorkItemType.Feature, "Seed feature");
        seed.Fields.ShouldBeEmpty();
        seed.PendingNotes.ShouldBeEmpty();
    }

    [Fact]
    public void CreateSeed_IsDirty_IsFalseInitially()
    {
        var seed = WorkItem.CreateSeed(WorkItemType.Task, "Seed");
        seed.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void CreateSeed_MultipleSeedsHaveUniqueIds()
    {
        var seed1 = WorkItem.CreateSeed(WorkItemType.Task, "Seed 1");
        var seed2 = WorkItem.CreateSeed(WorkItemType.Task, "Seed 2");
        var seed3 = WorkItem.CreateSeed(WorkItemType.Bug, "Seed 3");

        seed1.Id.ShouldBeLessThan(0);
        seed2.Id.ShouldBeLessThan(0);
        seed3.Id.ShouldBeLessThan(0);
        seed1.Id.ShouldNotBe(seed2.Id);
        seed1.Id.ShouldNotBe(seed3.Id);
        seed2.Id.ShouldNotBe(seed3.Id);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Multi-command atomic apply (ITEM-044)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ApplyCommands_MultiCommand_AtomicApply()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.UserStory, State = "New" };

        wi.ChangeState("Active");
        wi.UpdateField("System.Description", "Updated description");
        wi.AddNote(new PendingNote("Progress note", DateTimeOffset.UtcNow, false));

        var changes = wi.ApplyCommands();

        // FieldChange list should contain 2 (state + field), note produces null
        changes.Count.ShouldBe(2);
        changes[0].FieldName.ShouldBe("System.State");
        changes[0].OldValue.ShouldBe("New");
        changes[0].NewValue.ShouldBe("Active");
        changes[1].FieldName.ShouldBe("System.Description");
        changes[1].NewValue.ShouldBe("Updated description");

        // Note should be in PendingNotes
        wi.PendingNotes.Count.ShouldBe(1);
        wi.PendingNotes[0].Text.ShouldBe("Progress note");

        // State should be updated
        wi.State.ShouldBe("Active");
        wi.Fields["System.Description"].ShouldBe("Updated description");

        // IsDirty remains true until MarkSynced
        wi.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void ApplyCommands_EmptyQueue_ReturnsEmpty()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        var changes = wi.ApplyCommands();
        changes.ShouldBeEmpty();
        wi.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void ApplyCommands_CalledTwice_SecondCallReturnsEmpty()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        wi.ChangeState("Active");
        wi.ApplyCommands();

        var secondChanges = wi.ApplyCommands();
        secondChanges.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  MarkSynced tests (ITEM-045)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MarkSynced_ClearsDirty_UpdatesRevision()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };

        wi.ChangeState("Active");
        wi.IsDirty.ShouldBeTrue();

        wi.ApplyCommands();
        wi.IsDirty.ShouldBeTrue();

        wi.MarkSynced(5);

        wi.IsDirty.ShouldBeFalse();
        wi.Revision.ShouldBe(5);
    }

    [Fact]
    public void MarkSynced_UpdatesRevision_Incrementally()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };

        wi.MarkSynced(1);
        wi.Revision.ShouldBe(1);

        wi.ChangeState("Active");
        wi.ApplyCommands();
        wi.MarkSynced(2);
        wi.Revision.ShouldBe(2);
    }

    [Fact]
    public void MarkSynced_WithoutPriorChanges_StillUpdatesRevision()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        wi.MarkSynced(10);

        wi.IsDirty.ShouldBeFalse();
        wi.Revision.ShouldBe(10);
    }
}
