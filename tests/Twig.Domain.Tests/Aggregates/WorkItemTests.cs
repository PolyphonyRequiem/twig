using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
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
    //  Guard clause tests
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ChangeState_ThrowsOnNullOrWhitespace(string? newState)
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        Should.Throw<ArgumentException>(() => wi.ChangeState(newState!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateField_ThrowsOnNullOrWhitespaceFieldName(string? fieldName)
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        Should.Throw<ArgumentException>(() => wi.UpdateField(fieldName!, "value"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  State transition tests (ITEM-040)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ChangeState_MutatesAndReturnsFieldChange()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };

        var change = wi.ChangeState("Active");
        wi.IsDirty.ShouldBeTrue();

        wi.State.ShouldBe("Active");
        change.FieldName.ShouldBe("System.State");
        change.OldValue.ShouldBe("New");
        change.NewValue.ShouldBe("Active");
        wi.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void ChangeState_ForwardThenBackward()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };

        var change1 = wi.ChangeState("Active");
        var change2 = wi.ChangeState("New");

        wi.State.ShouldBe("New");
        change1.OldValue.ShouldBe("New");
        change1.NewValue.ShouldBe("Active");
        change2.OldValue.ShouldBe("Active");
        change2.NewValue.ShouldBe("New");
    }

    [Fact]
    public void ChangeState_ToRemoved()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.UserStory, State = "Active" };

        var change = wi.ChangeState("Removed");

        wi.State.ShouldBe("Removed");
        change.FieldName.ShouldBe("System.State");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Field update tests (ITEM-041)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void UpdateField_MutatesAndReturnsFieldChange()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };

        wi.IsDirty.ShouldBeFalse();
        var change = wi.UpdateField("System.Description", "Some description");
        wi.IsDirty.ShouldBeTrue();

        change.FieldName.ShouldBe("System.Description");
        change.OldValue.ShouldBeNull();
        change.NewValue.ShouldBe("Some description");
        wi.Fields["System.Description"].ShouldBe("Some description");
        wi.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void UpdateField_OverwritesExistingValue()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        wi.SetField("Priority", "2");

        var change = wi.UpdateField("Priority", "1");

        change.OldValue.ShouldBe("2");
        change.NewValue.ShouldBe("1");
        wi.Fields["Priority"].ShouldBe("1");
    }

    [Fact]
    public void UpdateField_SetsNullValue()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        wi.SetField("System.AssignedTo", "bob@example.com");

        var change = wi.UpdateField("System.AssignedTo", null);

        change.OldValue.ShouldBe("bob@example.com");
        change.NewValue.ShouldBeNull();
        wi.Fields["System.AssignedTo"].ShouldBeNull();
    }

    [Fact]
    public void UpdateField_CaseInsensitive()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.Task, State = "New" };
        wi.SetField("system.description", "old");

        var change = wi.UpdateField("System.Description", "new");

        change.OldValue.ShouldBe("old");
        change.NewValue.ShouldBe("new");
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

        wi.PendingNotes.Count.ShouldBe(2);
        wi.PendingNotes[0].Text.ShouldBe("Note 1");
        wi.PendingNotes[1].Text.ShouldBe("<b>Note 2</b>");
        wi.PendingNotes[1].IsHtml.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Seed tests (ITEM-043)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AsSeed_ReturnsSeedWorkItem()
    {
        var before = DateTimeOffset.UtcNow;
        var seed = new WorkItemBuilder(-1, "New bug").AsBug().AsSeed().Build();
        var after = DateTimeOffset.UtcNow;

        seed.IsSeed.ShouldBeTrue();
        seed.Id.ShouldBe(-1);
        seed.Type.ShouldBe(WorkItemType.Bug);
        seed.Title.ShouldBe("New bug");
        seed.SeedCreatedAt.ShouldNotBeNull();
        seed.SeedCreatedAt!.Value.ShouldBeGreaterThanOrEqualTo(before);
        seed.SeedCreatedAt!.Value.ShouldBeLessThanOrEqualTo(after);
    }

    [Fact]
    public void AsSeed_HasEmptyFieldsAndNotes()
    {
        var seed = new WorkItemBuilder(-1, "Seed feature").AsFeature().AsSeed().Build();
        seed.Fields.ShouldBeEmpty();
        seed.PendingNotes.ShouldBeEmpty();
    }

    [Fact]
    public void AsSeed_IsDirty_IsFalseInitially()
    {
        var seed = new WorkItemBuilder(-1, "Seed").AsSeed().Build();
        seed.IsDirty.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Multiple direct mutations (ITEM-044)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void DirectMutations_AllTakeEffectImmediately()
    {
        var wi = new WorkItem { Id = 1, Type = WorkItemType.UserStory, State = "New" };

        var stateChange = wi.ChangeState("Active");
        var fieldChange = wi.UpdateField("System.Description", "Updated description");
        wi.AddNote(new PendingNote("Progress note", DateTimeOffset.UtcNow, false));

        stateChange.FieldName.ShouldBe("System.State");
        stateChange.OldValue.ShouldBe("New");
        stateChange.NewValue.ShouldBe("Active");
        fieldChange.FieldName.ShouldBe("System.Description");
        fieldChange.NewValue.ShouldBe("Updated description");

        // Note should be in PendingNotes
        wi.PendingNotes.Count.ShouldBe(1);
        wi.PendingNotes[0].Text.ShouldBe("Progress note");

        // State should be updated
        wi.State.ShouldBe("Active");
        wi.Fields["System.Description"].ShouldBe("Updated description");

        // IsDirty remains true until MarkSynced
        wi.IsDirty.ShouldBeTrue();
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

    // ═══════════════════════════════════════════════════════════════
    //  WithSeedFields tests (E1-T2)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WithSeedFields_PreservesIdentityProperties()
    {
        var iterPath = IterationPath.Parse(@"Project\Sprint1").Value;
        var areaPath = AreaPath.Parse(@"Project\Team").Value;

        var original = new WorkItem
        {
            Id = -5,
            Type = WorkItemType.UserStory,
            Title = "Original Title",
            State = "New",
            AssignedTo = "alice@example.com",
            IterationPath = iterPath,
            AreaPath = areaPath,
            ParentId = 42,
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
        };

        var fields = new Dictionary<string, string?> { ["System.Description"] = "Updated desc" };
        var copy = original.WithSeedFields("New Title", fields);

        copy.Id.ShouldBe(-5);
        copy.Type.ShouldBe(WorkItemType.UserStory);
        copy.State.ShouldBe("New");
        copy.AssignedTo.ShouldBe("alice@example.com");
        copy.IterationPath.Value.ShouldBe(@"Project\Sprint1");
        copy.AreaPath.Value.ShouldBe(@"Project\Team");
        copy.ParentId.ShouldBe(42);
        copy.IsSeed.ShouldBeTrue();
        copy.SeedCreatedAt.ShouldBe(original.SeedCreatedAt);
    }

    [Fact]
    public void WithSeedFields_UpdatesTitleAndFields()
    {
        var original = new WorkItem
        {
            Id = -1,
            Type = WorkItemType.Task,
            Title = "Old Title",
            State = "New",
            IsSeed = true,
        };
        original.ImportFields(new Dictionary<string, string?> { ["System.Description"] = "Old desc" });

        var newFields = new Dictionary<string, string?>
        {
            ["System.Description"] = "New desc",
            ["Microsoft.VSTS.Common.Priority"] = "1",
        };

        var copy = original.WithSeedFields("New Title", newFields);

        copy.Title.ShouldBe("New Title");
        copy.Fields["System.Description"].ShouldBe("New desc");
        copy.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("1");
    }

    [Fact]
    public void WithSeedFields_CopyIsNotDirty()
    {
        var original = new WorkItemBuilder(-1, "Seed").AsSeed().Build();
        original.UpdateField("System.Description", "Dirty");
        original.IsDirty.ShouldBeTrue();

        var copy = original.WithSeedFields("Updated", new Dictionary<string, string?>());
        copy.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void WithSeedFields_PreservesRevision()
    {
        var original = new WorkItem
        {
            Id = 100,
            Type = WorkItemType.Feature,
            Title = "Feature",
            State = "Active",
        };
        original.MarkSynced(7);

        var copy = original.WithSeedFields("Updated Feature", new Dictionary<string, string?>());

        copy.Revision.ShouldBe(7);
    }

    [Fact]
    public void WithSeedFields_DoesNotMutateOriginal()
    {
        var original = new WorkItemBuilder(-1, "Original").AsSeed().Build();
        original.ImportFields(new Dictionary<string, string?> { ["System.Description"] = "Orig desc" });

        var newFields = new Dictionary<string, string?> { ["System.Description"] = "Modified" };
        _ = original.WithSeedFields("Modified Title", newFields);

        original.Title.ShouldBe("Original");
        original.Fields["System.Description"].ShouldBe("Orig desc");
    }

    // ═══════════════════════════════════════════════════════════════
    //  SeedIdCounter tests (E1-T3, E1-T12) — via instance counter
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SeedIdCounter_SetsCounterBelowExistingSeeds()
    {
        var counter = new SeedIdCounter();
        counter.Initialize(-10);

        var id = counter.Next();

        id.ShouldBeLessThan(-10);
    }

    [Fact]
    public void SeedIdCounter_WithPositiveValue_ClampsToZero()
    {
        var counter = new SeedIdCounter();
        counter.Initialize(5);

        var id = counter.Next();

        // Math.Min(5, 0) = 0, so next is Decrement(0) = -1
        id.ShouldBeLessThan(0);
    }

    [Fact]
    public void SeedIdCounter_AvoidCollisionsWithExistingSeeds()
    {
        var counter = new SeedIdCounter();
        // Simulate existing seeds at -1, -2, -3 → min is -3
        counter.Initialize(-3);

        var newIds = new HashSet<int>();
        for (var i = 0; i < 5; i++)
        {
            var id = counter.Next();
            id.ShouldBeLessThan(-3, "New seed ID should be below all existing seed IDs");
            newIds.Add(id).ShouldBeTrue("Each seed should have a unique ID");
        }
    }

    [Fact]
    public void SeedIdCounter_WithZero_ProducesNegativeIds()
    {
        var counter = new SeedIdCounter();
        counter.Initialize(0);

        var id = counter.Next();
        id.ShouldBeLessThan(0);
    }
}
